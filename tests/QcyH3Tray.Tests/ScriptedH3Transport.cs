using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 脚本化假传输层：像真耳机一样应答（握手 / 电量 / 降噪模式 / EQ v1+v2 / 游戏模式），
/// 用于端到端驱动 <see cref="Core.App.ConnectionManager"/> 的连接初始化流程。
/// <see cref="WriteAsync"/> 用 Task.Yield 切出，模拟真实异步时序。
/// </summary>
internal sealed class ScriptedH3Transport : IBleTransport
{
    public event Action<byte[]>? DataReceived;

    public event Action? Disconnected;

    public List<byte[]> WrittenFrames { get; } = [];

    public int ConnectCalls { get; private set; }

    public bool FailConnect { get; set; }

    /// <summary>游戏模式状态（ADV TLV 回报值；0x02=开 0x01=关），SET_ADV_INFO 写入会更新。</summary>
    public byte GameModeState { get; set; } = 0x02;

    /// <summary>最后一次收到的 EQ v1 写载荷（null=尚未写过，读回基线平线）。</summary>
    public byte[]? LastEqV1Write { get; private set; }

    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        ConnectCalls++;
        if (FailConnect)
        {
            throw new InvalidOperationException("模拟：设备不可达");
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        WrittenFrames.Add(data);
        var request = Packet.Decode(data);
        if (request is null)
        {
            return;
        }
        var response = BuildResponse(request);
        if (response is not null)
        {
            DataReceived?.Invoke(response.Encode());
        }
    }

    /// <summary>根据请求构造响应包（opCode/type/与真机一致，按请求 sn 回）。</summary>
    private Packet? BuildResponse(Packet request)
    {
        byte[]? parameters = request.OpCode switch
        {
            OpCode.NotifyCommunicationWay => [0x00, 0x00],
            OpCode.GetAdvInfo => AdvInfoPayload(request.Params),
            OpCode.Custom when request.Params is [0xFE, 0x01, 0x17] => [0x17, 0x03, 0x01, 0x01, 0x02], // GET 降噪状态 → 降噪高（索引 4）
            OpCode.Custom when request.Params is [0xFE, 0x01, 0x20] => EqV1Payload(),                  // GET EQ v1 → 7 段表
            OpCode.Custom when request.Params is [0xFE, 0x01, 0x22] => EqPayload(),
            OpCode.Custom when request.Params is [0x17, 0x03, ..] => request.Params,                   // SET 降噪模式 → 回显确认
            OpCode.Custom when request.Params is [EqV1.Cmd, ..] => RecordEqV1Write(request.Params),    // SET EQ v1 → 回显确认
            OpCode.SetAdvInfo when request.Params is [0x02, AdvType.WorkMode, ..] => RecordGameMode(request.Params), // SET 游戏模式
            _ => null,
        };
        return parameters is null
            ? null
            : new Packet(request.OpCode, Packet.TypeResponse, hasResponse: 0, status: 0, sn: request.Sn, parameters: parameters);
    }

    /// <summary>GET_ADV_INFO 掩码 → TLV：bit0 电量 85%、bit5 游戏模式（当前 <see cref="GameModeState"/>）。</summary>
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
        if ((mask & (1u << AdvType.WorkMode)) != 0)
        {
            payload.AddRange([0x02, AdvType.WorkMode, GameModeState]);
        }
        return [.. payload];
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

/// <summary>假扫描器：记录 Start/Stop，测试直接投放广播。</summary>
internal sealed class FakeScanner : IBleScanner
{
    public event Action<BleScanResult>? AdvertisementReceived;

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public bool IsScanning { get; private set; }

    public void Start()
    {
        StartCalls++;
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
