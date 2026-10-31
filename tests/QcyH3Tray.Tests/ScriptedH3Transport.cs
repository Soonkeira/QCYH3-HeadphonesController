using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 脚本化假传输层：像真耳机一样应答（握手 / 电量 / 降噪模式 / EQ v1+v2 / 游戏模式），
/// 用于端到端驱动 <see cref="Core.App.ConnectionManager"/> 的连接初始化流程。
/// <see cref="WriteAsync"/> 用 Task.Yield 切出，模拟真实异步时序；
/// 可注入 <see cref="WriteDelay"/> 观察并发行为（写串行化测试）。
/// </summary>
internal sealed class ScriptedH3Transport : IBleTransport
{
    public event Action<byte[]>? DataReceived;

    public event Action? Disconnected;

    public List<byte[]> WrittenFrames { get; } = [];

    public int ConnectCalls { get; private set; }

    public bool FailConnect { get; set; }

    /// <summary>非 null 时 ConnectAsync 抛出该异常（AccessDenied 冷却测试用，优先于 FailConnect）。</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>非 null 时 WriteAsync 抛出该异常（模拟写入瞬间链路已释放/断开，命令异常路径测试用）。</summary>
    public Exception? WriteException { get; set; }

    /// <summary>非 null 时 ConnectAsync 挂起直到闸门放开（generation 竞争测试用）。</summary>
    public TaskCompletionSource<bool>? ConnectGate { get; set; }

    /// <summary>让 SET 游戏模式（0xC0 02 05 …）无应答（模拟写入超时，命令健康测试用）。</summary>
    public bool SilentGameModeWrite { get; set; }

    /// <summary>每次 WriteAsync 的延迟（0 = 不延迟）；配合 MaxConcurrentWrites 验证串行化。</summary>
    public TimeSpan WriteDelay { get; set; }

    /// <summary>当前正在进行的 WriteAsync 数（峰值见 <see cref="MaxConcurrentWrites"/>）。</summary>
    private int _concurrentWrites;

    public int MaxConcurrentWrites { get; private set; }

    /// <summary>让指定 GET 无应答（模拟超时）：降噪 FE 01 17 / EQ FE 01 20 / 游戏模式（掩码 0x20）。</summary>
    public bool SilentNoiseRead { get; set; }

    public bool SilentEqRead { get; set; }

    public bool SilentGameModeRead { get; set; }

    /// <summary>自动关机（0x14）/ 按键表（ADV type=2）读、写通道各自的静音开关（模拟超时/静默忽略）。</summary>
    public bool SilentAutoOffRead { get; set; }

    public bool SilentAutoOffWrite { get; set; }

    public bool SilentKeyMapRead { get; set; }

    public bool SilentKeyMapWrite { get; set; }

    /// <summary>提示音（3D）/ 查找耳机（4B）写无应答（模拟超时，ack 判定测试用）。</summary>
    public bool SilentToneFindWrite { get; set; }

    /// <summary>自动关机状态（powerOff u16LE 分钟；0xFFFF = 不自动关机），SET 0x14 写入会更新。</summary>
    public int PowerOffMinutes { get; set; } = PowerManagerInfo.NeverOffMinutes;

    /// <summary>按键映射表状态（GET_ADV_INFO type=2 整表），SET 写入会更新；默认 4 键×5 动作 + 2 条出厂槽。</summary>
    public List<KeyMappingEntry> KeyTable { get; set; } =
    [
        new(0x07, 0x01, 0x01), new(0x07, 0x02, 0x07), new(0x07, 0x03, 0x04), new(0x07, 0x04, 0x0A), new(0x07, 0x05, 0x0B),
        new(0x03, 0x01, 0x06), new(0x03, 0x02, 0x02), new(0x03, 0x03, 0x03), new(0x03, 0x04, 0x08), new(0x03, 0x05, 0x09),
        new(0x05, 0x01, 0x05), new(0x05, 0x02, 0x00), new(0x05, 0x03, 0x00), new(0x05, 0x04, 0x00), new(0x05, 0x05, 0x01),
        new(0x09, 0x01, 0xFF), new(0x09, 0x02, 0x00), new(0x09, 0x03, 0x00), new(0x09, 0x04, 0x00), new(0x09, 0x05, 0x00),
        new(0x07, 0x81, 0x01), new(0x07, 0x82, 0x02),   // 出厂槽（语义未知，编辑器只读展示）
        new(0x03, 0x81, 0x05),
    ];

    /// <summary>可选响应闸门：谓词命中的请求在闸门放开前不回包（观察 Loading/同步中间态）。</summary>
    public TaskCompletionSource<bool>? ResponseGate { get; set; }

    public Func<Packet, bool>? ResponseGatePredicate { get; set; }

    /// <summary>游戏模式状态（ADV TLV 回报值；0x02=开 0x01=关），SET_ADV_INFO 写入会更新。</summary>
    public byte GameModeState { get; set; } = 0x02;

    /// <summary>最后一次收到的 EQ v1 写载荷（null=尚未写过，读回基线平线）。</summary>
    public byte[]? LastEqV1Write { get; private set; }

    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        ConnectCalls++;
        if (ConnectGate is not null)
        {
            await ConnectGate.Task.ConfigureAwait(false);
        }
        if (ConnectException is not null)
        {
            throw ConnectException;
        }
        if (FailConnect)
        {
            throw new InvalidOperationException("模拟：设备不可达");
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (WriteException is not null)
        {
            throw WriteException;
        }
        int now = Interlocked.Increment(ref _concurrentWrites);
        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, now);
        try
        {
            if (WriteDelay > TimeSpan.Zero)
            {
                await Task.Delay(WriteDelay, cancellationToken).ConfigureAwait(false);
            }
            WrittenFrames.Add(data);
            var request = Packet.Decode(data);
            if (request is null)
            {
                return;
            }
            if (ResponseGate is not null && ResponseGatePredicate?.Invoke(request) == true)
            {
                await ResponseGate.Task.ConfigureAwait(false);
            }
            var response = BuildResponse(request);
            if (response is not null)
            {
                DataReceived?.Invoke(response.Encode());
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentWrites);
        }
    }

    /// <summary>根据请求构造响应包（opCode/type/与真机一致，按请求 sn 回）。</summary>
    private Packet? BuildResponse(Packet request)
    {
        byte[]? parameters = request.OpCode switch
        {
            OpCode.NotifyCommunicationWay => [0x00, 0x00],
            OpCode.GetAdvInfo when SilentGameModeRead && IsWorkModeMask(request.Params) => null,
            OpCode.GetAdvInfo when SilentKeyMapRead && IsKeySettingsMask(request.Params) => null,
            OpCode.GetAdvInfo => AdvInfoPayload(request.Params),
            OpCode.Custom when request.Params is [0xFE, 0x01, 0x17] && !SilentNoiseRead => [0x17, 0x03, 0x01, 0x01, 0x02], // GET 降噪状态 → 降噪高（索引 4）
            OpCode.Custom when request.Params is [0xFE, 0x01, 0x20] && !SilentEqRead => EqV1Payload(),                     // GET EQ v1 → 7 段表
            OpCode.Custom when request.Params is [0xFE, 0x01, 0x22] && !SilentEqRead => EqPayload(),                       // GET EQ v2（v1 失败时回退；静音 = EQ 读取整体失败）
            OpCode.Custom when request.Params is [0xFE, 0x01, PowerManager.Cmd] && !SilentAutoOffRead => PowerManagerPayload(), // GET 自动关机
            OpCode.Custom when request.Params is [ToneCommands.PlayToneCmd, ..] && !SilentToneFindWrite => request.Params, // 播放提示音 → 回显确认
            OpCode.Custom when request.Params is [ToneCommands.FindEarbudsCmd, ..] && !SilentToneFindWrite => request.Params, // 查找耳机 → 回显确认
            OpCode.Custom when request.Params is [PowerManager.Cmd, ..] && !SilentAutoOffWrite => RecordPowerManagerWrite(request.Params), // SET 自动关机 → 记录并回显确认
            OpCode.Custom when request.Params is [0x17, 0x03, ..] => request.Params,                   // SET 降噪模式 → 回显确认
            OpCode.Custom when request.Params is [EqV1.Cmd, ..] => RecordEqV1Write(request.Params),    // SET EQ v1 → 回显确认
            OpCode.SetAdvInfo when request.Params is [0x02, AdvType.WorkMode, ..] && !SilentGameModeWrite => RecordGameMode(request.Params), // SET 游戏模式
            OpCode.SetAdvInfo when request.Params is [_, AdvType.KeySettings, ..] && !SilentKeyMapWrite => RecordKeyMapWrite(request.Params), // SET 按键表 → 记录并回显确认
            _ => null,
        };
        return parameters is null
            ? null
            : new Packet(request.OpCode, Packet.TypeResponse, hasResponse: 0, status: 0, sn: request.Sn, parameters: parameters);
    }

    /// <summary>GET_ADV_INFO 掩码是否只含 WorkMode（bit5）。</summary>
    private static bool IsWorkModeMask(byte[] requestParams)
    {
        uint mask = 0;
        for (int i = 0; i < 4 && i < requestParams.Length; i++)
        {
            mask = (mask << 8) | requestParams[i];
        }
        return mask == 1u << AdvType.WorkMode;
    }

    /// <summary>GET_ADV_INFO 掩码是否只含 KeySettings（bit2）。</summary>
    private static bool IsKeySettingsMask(byte[] requestParams)
    {
        uint mask = 0;
        for (int i = 0; i < 4 && i < requestParams.Length; i++)
        {
            mask = (mask << 8) | requestParams[i];
        }
        return mask == 1u << AdvType.KeySettings;
    }

    /// <summary>GET_ADV_INFO 掩码 → TLV：bit0 电量 85%、bit2 按键表、bit5 游戏模式（当前 <see cref="GameModeState"/>）。</summary>
    private byte[] AdvInfoPayload(byte[] requestParams)
    {
        uint mask = 0;
        for (int i = 0; i < 4 && i < requestParams.Length; i++)
        {
            mask = (mask << 8) | requestParams[i];
        }

        var payload = new List<byte>();
        if ((mask & (1u << AdvType.BatteryQuantity)) != 0)
        {
            payload.AddRange([0x02, AdvType.BatteryQuantity, 0x55]);
        }
        if ((mask & (1u << AdvType.KeySettings)) != 0)
        {
            payload.AddRange(KeyTablePayload());
        }
        if ((mask & (1u << AdvType.WorkMode)) != 0)
        {
            payload.AddRange([0x02, AdvType.WorkMode, GameModeState]);
        }
        return [.. payload];
    }

    /// <summary>按键表 TLV：<c>[len = 1 + 3N][0x02][keyNum action function × N]</c>（L 含类型字节，与真机整表同构）。</summary>
    private byte[] KeyTablePayload()
    {
        var value = new List<byte>(1 + KeyTable.Count * KeyMap.BytesPerEntry) { AdvType.KeySettings };
        foreach (KeyMappingEntry entry in KeyTable)
        {
            value.Add(entry.KeyNumber);
            value.Add(entry.Action);
            value.Add(entry.Function);
        }
        value.Insert(0, (byte)(1 + KeyTable.Count * KeyMap.BytesPerEntry));
        return [.. value];
    }

    /// <summary>GET 0x14 → <c>[0x14][04][powerOff u16LE][current u16LE]</c>（真机基线 FF FF 00 00）。</summary>
    private byte[] PowerManagerPayload()
    {
        int minutes = PowerOffMinutes & 0xFFFF;
        return [PowerManager.Cmd, PowerManager.DataLength, (byte)(minutes & 0xFF), (byte)(minutes >> 8), 0x00, 0x00];
    }

    /// <summary>SET 0x14：记录新时长（此后 GET 回读它）并回显确认。</summary>
    private byte[] RecordPowerManagerWrite(byte[] requestParams)
    {
        if (requestParams.Length >= 4)
        {
            PowerOffMinutes = requestParams[2] | (requestParams[3] << 8);
        }
        return requestParams;
    }

    /// <summary>SET 按键表：解析 LTV 三元组记录为整表（此后 GET 回读它）并回显确认。</summary>
    private byte[] RecordKeyMapWrite(byte[] requestParams)
    {
        // LTV: [len][0x02][三元组 × N]
        if (requestParams.Length >= 2 && requestParams[1] == AdvType.KeySettings)
        {
            int declaredLength = requestParams[0];
            int available = Math.Min(declaredLength, requestParams.Length - 2);
            List<KeyMappingEntry>? entries = available > 0
                ? KeyMap.ParseAdvInfoValue(requestParams[2..(2 + available)])
                : null;
            if (entries is not null)
            {
                KeyTable = entries;
            }
        }
        return requestParams;
    }

    /// <summary>SET 游戏模式：记录新状态（0x01=关 0x02=开）并回显确认。</summary>
    private byte[] RecordGameMode(byte[] requestParams)
    {
        if (requestParams.Length >= 3)
        {
            GameModeState = requestParams[2] == 0x02 ? (byte)0x02 : (byte)0x01;
        }
        return requestParams;
    }

    /// <summary>SET EQ v1：记录写载荷（此后 GET 0x20 回读它）并回显确认。</summary>
    private byte[] RecordEqV1Write(byte[] requestParams)
    {
        LastEqV1Write = (byte[])requestParams.Clone();
        return requestParams;
    }

    /// <summary>GET 0x20 → v1 表：上次写入的曲线，否则基线平线（eqType=02，masterGain=-5.5dB，7 段 Q=1.20）。</summary>
    private byte[] EqV1Payload()
    {
        if (LastEqV1Write is not null)
        {
            return (byte[])LastEqV1Write.Clone();
        }
        var parameters = new List<byte>
        {
            EqV1.Cmd,
            (byte)(EqV1.HeaderLength + EqV1BandMap.SevenBandCount * EqV1.BytesPerBand),  // 45 = 0x2D
            0x02,                                                                        // eqType 基线
            0xDA, 0xFD,                                                                  // masterGain = -550（-5.5 dB）
        };
        for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
        {
            parameters.AddRange(
            [
                (byte)(EqV1BandMap.Frequencies[i] & 0xFF), (byte)(EqV1BandMap.Frequencies[i] >> 8),
                0x00, 0x00,                                                              // 增益 0
                0x78, 0x00,                                                              // Q = 120（×100）
            ]);
        }
        return [.. parameters];
    }

    /// <summary>GET 0x22 → params = 22 49 03 70 FE + 10 段（增益 -50×i，校验符号扩展）。</summary>
    private static byte[] EqPayload()
    {
        var parameters = new List<byte> { 0x22, 0x49, 0x03, 0x70, 0xFE };
        for (int i = 0; i < EqBands.BandCount; i++)
        {
            short gain = (short)(-50 * i);
            parameters.AddRange(
            [
                (byte)(EqBands.Frequencies[i] & 0xFF), (byte)(EqBands.Frequencies[i] >> 8),
                (byte)gain, (byte)((uint)gain >> 8),
                (byte)(EqBands.Defaults[i].RefGain & 0xFF), (byte)(EqBands.Defaults[i].RefGain >> 8),
                EqBands.Defaults[i].Q,
            ]);
        }
        return [.. parameters];
    }

    /// <summary>注入任意下行帧（推送测试用）。</summary>
    public void RaiseReceived(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            DataReceived?.Invoke(chunk);
        }
    }

    public void RaiseDisconnected() => Disconnected?.Invoke();

    public void Dispose()
    {
    }
}

/// <summary>假传输层工厂：按队列吐出脚本化实例；队列空时按 <see cref="FailNew"/> 造默认实例。</summary>
internal sealed class FakeTransportFactory : IBleTransportFactory
{
    public Queue<ScriptedH3Transport> Transports { get; } = [];

    public List<ScriptedH3Transport> Created { get; } = [];

    /// <summary>队列空时新建的实例是否一律连接失败（重连退避测试用）。</summary>
    public bool FailNew { get; set; }

    /// <summary>每次 Create 的时间戳（退避间隔断言用）。</summary>
    public List<DateTime> CreateTimestamps { get; } = [];

    public IBleTransport Create()
    {
        CreateTimestamps.Add(DateTime.UtcNow);
        var transport = Transports.Count > 0 ? Transports.Dequeue() : new ScriptedH3Transport { FailConnect = FailNew };
        Created.Add(transport);
        return transport;
    }
}

/// <summary>假扫描器：记录 Start/Stop，测试直接投放广播；可模拟蓝牙不可用。</summary>
internal sealed class FakeScanner : IBleScanner
{
    public event Action<BleScanResult>? AdvertisementReceived;

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public bool IsScanning { get; private set; }

    /// <summary>true = Start() 抛 BluetoothUnavailableException（模拟蓝牙未开启，D4 测试用）。</summary>
    public bool FailStart { get; set; }

    public void Start()
    {
        StartCalls++;
        if (FailStart)
        {
            throw new BluetoothUnavailableException("蓝牙未开启：请打开 Windows 设置中的蓝牙开关后再试。（测试模拟）");
        }
        IsScanning = true;
    }

    public void Stop()
    {
        StopCalls++;
        IsScanning = false;
    }

    public Task<List<BleScanResult>> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<BleScanResult>());

    public void Emit(BleScanResult result) => AdvertisementReceived?.Invoke(result);

    public void Dispose()
    {
    }
}
