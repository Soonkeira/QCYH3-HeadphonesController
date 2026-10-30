using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Core.App;

/// <summary>连接状态机状态。</summary>
public enum ConnectionState
{
    Disconnected,
    Scanning,
    Connecting,
    Connected,
}

/// <summary>降噪/EQ/游戏模式数据快照。任一项更新都会整体抛 <see cref="ConnectionManager.DeviceDataChanged"/>。</summary>
public sealed record DeviceData
{
    /// <summary>
    /// 降噪模式旧索引：0=关闭 1=通透 2=降噪低 3=降噪中 4=降噪高 5=自适应；
    /// 场景模式（通勤/室内/抗风噪）无索引 → null。展示用 <see cref="NoiseModeInfo"/>。
    /// </summary>
    public int? NoiseMode { get; init; }

    /// <summary>
    /// 降噪模式完整解码（家族 + 档位 + 中文显示名，含场景模式，真机抓包 2026-10）；
    /// null=未知。
    /// </summary>
    public AncModeInfo? NoiseModeInfo { get; init; }

    /// <summary>当前 EQ 预设 ID（0x00=自定义 … 0x06=古典）；null=未知。</summary>
    public byte? EqPresetId { get; init; }

    /// <summary>10 段 EQ 增益（1/100-dB）；null=未知。</summary>
    public int[]? EqOffsets { get; init; }

    /// <summary>
    /// EQ v1 表快照（7 段 + masterGain + eqType，cmd 0x20）；null=未知。
    /// 本机固件 EQ 读走 v1（v2 回空载荷），UI 预设高亮/编辑器都以它为准。
    /// </summary>
    public EqV1Info? EqV1 { get; init; }

    /// <summary>游戏模式（低延迟）；null=未知。</summary>
    public bool? GameMode { get; init; }
}

/// <summary>降噪模式写入结果。</summary>
/// <param name="Acknowledged">false = 耳机对写入无响应，模式可能未生效。</param>
/// <param name="ReadBackIndex">回读到的旧六模式索引（场景模式为 null）；null = 回读无响应。</param>
/// <param name="ReadBackInfo">回读到的完整解码结果（含场景模式）；null = 回读无响应或负载不合法。</param>
public sealed record NoiseModeSetResult(bool Acknowledged, int? ReadBackIndex, AncModeInfo? ReadBackInfo = null);

/// <summary>
/// H3 连接管理：状态机（Disconnected / Scanning / Connecting / Connected）。
/// 连上后执行 Python 参考实现的初始化序列（握手 → 200ms → 电量 → 降噪模式 → EQ →
/// 游戏模式），全部经事件抛出；Connected 期间每 15 秒刷新电量；耳机主动推送
/// （未匹配 sn 的通知）也会解析成事件。目标设备不可达时自动进入重连：
/// 监听广播（地址或名称兜底"QCY H3"前缀），失败按 1s/5s/30s 封顶退避，成功归零。
/// <para>
/// 线程模型：内部一把锁串行化状态转移；每个连接流程持有代数（generation），
/// 过期流程不得改动状态。事件在线程池线程触发，UI 层自行 Dispatcher 处理。
/// </para>
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    /// <summary>名称兜底匹配前缀（广播名以此开头即视为目标，供地址漂移时兜底）。</summary>
    public const string FallbackNamePrefix = "QCY H3";

    /// <summary>重连退避序列（超出后封顶用最后一个）；可整体替换以便测试。</summary>
    public TimeSpan[] ReconnectBackoff { get; set; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
    ];

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostHandshakeDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly IBleTransportFactory _transportFactory;
    private readonly IBleScanner _scanner;
    private readonly bool _ownsScanner;

    private ConnectionState _state = ConnectionState.Disconnected;
    private string _statusText = "未连接";
    private ulong? _targetAddress;
    private string _targetName = "";
    private DeviceData _deviceData = new();

    private IBleTransport? _transport;
    private H3Client? _client;
    private Task<bool>? _connectTask;
    private Task? _reconnectLoopTask;
    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _sessionCts;
    private TaskCompletionSource<BleScanResult>? _advertisementSignal;

    private int _generation;
    private int _consecutiveFailures;
    private bool _disposed;

    /// <summary>目标不可达/连接失败时是否自动重连（CLI 一次性命令可关掉）。</summary>
    public bool AutoReconnect { get; set; } = true;

    public ConnectionManager(IBleTransportFactory? transportFactory = null, IBleScanner? scanner = null)
    {
        if (scanner is null)
        {
            _scanner = new BleScanner();
            _ownsScanner = true;
        }
        else
        {
            _scanner = scanner;
            _ownsScanner = false;
        }
        _transportFactory = transportFactory ?? new BleTransportFactory();
        _scanner.AdvertisementReceived += OnAdvertisementReceived;
    }

    /// <summary>当前状态。</summary>
    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>最近一条状态文本（对应 <see cref="StatusText"/> 事件）。</summary>
    public string CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return _statusText;
            }
        }
    }

    public ulong? TargetAddress
    {
        get
        {
            lock (_gate)
            {
                return _targetAddress;
            }
        }
    }

    public string TargetName
    {
        get
        {
            lock (_gate)
            {
                return _targetName;
            }
        }
    }

    /// <summary>最近一次设备数据快照。</summary>
    public DeviceData CurrentDeviceData
    {
        get
        {
            lock (_gate)
            {
                return _deviceData;
            }
        }
    }

    /// <summary>状态转移（每次实际变化触发一次）。</summary>
    public event Action<ConnectionState>? StateChanged;

    /// <summary>人类可读的过程状态（"正在连接…" / "连接失败：…" 等）。</summary>
    public event Action<string>? StatusText;

    /// <summary>电量百分比（0-100）：初始读数、15s 定时刷新、耳机主动推送。</summary>
    public event Action<int>? BatteryChanged;

    /// <summary>降噪模式 / EQ / 游戏模式任一更新时抛整体快照。</summary>
    public event Action<DeviceData>? DeviceDataChanged;

    /// <summary>设置目标设备（重连与连接都用它）。</summary>
    public void SetTarget(ulong address, string name)
    {
        lock (_gate)
        {
            _targetAddress = address;
            _targetName = name ?? string.Empty;
        }
    }

    /// <summary>
    /// 连接目标设备并完成初始化读数（幂等：连接中重复调用返回同一任务，
    /// 已连接直接成功）。返回 false = 连接失败（原因见 <see cref="CurrentStatus"/>）。
    /// </summary>
    /// <exception cref="InvalidOperationException">未设置目标（先调用 <see cref="SetTarget"/>）。</exception>
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == ConnectionState.Connected)
            {
                return Task.FromResult(true);
            }
            if (_connectTask is not null && !_connectTask.IsCompleted)
            {
                return _connectTask;
            }

            ulong address = _targetAddress
                ?? throw new InvalidOperationException("尚未选择目标设备：请先调用 SetTarget(address, name)。");
            return ConnectAttemptLocked(address, fromReconnectLoop: false, cancellationToken);
        }
    }

    /// <summary>
    /// 已连接时写入降噪模式并回读确认。返回 null = 耳机对写入无响应（模式可能未生效）。
    /// </summary>
    public async Task<NoiseModeSetResult?> SetNoiseModeAsync(byte[] modeBytes, CancellationToken cancellationToken = default)
    {
        var client = CurrentClient()
            ?? throw new InvalidOperationException("尚未连接耳机，无法切换降噪模式。");

        var response = await client.SetNoiseModeAsync(modeBytes, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        AncModeInfo? readBack = await client.GetAncModeInfoAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (readBack is not null)
        {
            UpdateDeviceData(data => data with { NoiseMode = AncMode.ToIndex(readBack), NoiseModeInfo = readBack });
        }
        return new NoiseModeSetResult(Acknowledged: true, ReadBackIndex: AncMode.ToIndex(readBack), ReadBackInfo: readBack);
    }

    /// <summary>
    /// 全量刷新（电量 / 降噪模式 / EQ / 游戏模式，顺序单次读）。
    /// 未连接时是安全的空操作（各项内部自检 CurrentClient）。
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        await RefreshNoiseModeAsync(cancellationToken).ConfigureAwait(false);
        await RefreshEqAsync(cancellationToken).ConfigureAwait(false);
        await RefreshGameModeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 已连接时写入 EQ v1 表（eqType 保持读到的值，见 <see cref="H3Client.SetEqV1Async"/>），
    /// 随后回读确认并更新 <see cref="DeviceData.EqV1"/>（回读失败时用写入值做乐观快照）。
    /// 返回 null = 耳机对写入无响应。
    /// </summary>
    public async Task<EqV1Info?> SetEqV1Async(
        byte eqType, int masterGain, IReadOnlyList<EqV1Band> bands, CancellationToken cancellationToken = default)
    {
        var client = CurrentClient()
            ?? throw new InvalidOperationException("尚未连接耳机，无法写入 EQ。");

        var response = await client.SetEqV1Async(eqType, masterGain, bands, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        EqV1Info? confirmed = await client.GetEqV1Async(CommandTimeout, cancellationToken).ConfigureAwait(false);
        var snapshot = confirmed ?? new EqV1Info(eqType, masterGain, new List<EqV1Band>(bands));
        UpdateDeviceData(data => data with { EqV1 = snapshot });
        return snapshot;
    }

    /// <summary>
    /// 已连接时写入游戏模式（低延迟）并回读确认，更新 <see cref="DeviceData.GameMode"/>。
    /// 返回 null = 耳机对写入无响应。
    /// </summary>
    public async Task<bool?> SetGameModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var client = CurrentClient()
            ?? throw new InvalidOperationException("尚未连接耳机，无法切换游戏模式。");

        var response = await client.SetGameModeAsync(enabled, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        bool? readBack = await client.GetWorkModeAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        bool value = readBack ?? enabled;
        UpdateDeviceData(data => data with { GameMode = value });
        return value;
    }

    /// <summary>
    /// 已连接时读取 EQ v1 表并更新 <see cref="DeviceData.EqV1"/>。
    /// 返回 null = 未连接或设备未应答（不抛异常）。
    /// </summary>
    public async Task<EqV1Info?> GetEqV1Async(CancellationToken cancellationToken = default)
    {
        var client = CurrentClient();
        if (client is null)
        {
            return null;
        }
        EqV1Info? info = await client.GetEqV1Async(CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (info is not null)
        {
            UpdateDeviceData(data => data with { EqV1 = info });
        }
        return info;
    }

    /// <summary>主动断开（停掉定时刷新与重连循环）。幂等。</summary>
    public void Disconnect()
    {
        (IBleTransport?, H3Client?) session;
        ConnectionState before;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            before = _state;
            _generation++;                       // 使在途连接流程过期
            CancelReconnectLoopLocked();
            session = TeardownSessionLocked("已断开连接。");
        }
        DisposeSession(session);
        FireTransition(ConnectionState.Disconnected, "已断开连接。", stateChanged: before != ConnectionState.Disconnected);
    }

    public void Dispose()
    {
        (IBleTransport?, H3Client?) session = (null, null);
        ConnectionState before;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            before = _state;
            _generation++;
            CancelReconnectLoopLocked();
            session = TeardownSessionLocked("已释放。");
            _scanner.AdvertisementReceived -= OnAdvertisementReceived;
        }

        DisposeSession(session);
        if (before != ConnectionState.Disconnected)
        {
            FireTransition(ConnectionState.Disconnected, "已释放。", stateChanged: true);
        }
        try
        {
            _scanner.Stop();
        }
        catch
        {
        }
        if (_ownsScanner)
        {
            _scanner.Dispose();
        }
    }

    // ───────────────────────── 连接流程 ─────────────────────────

    /// <summary>发起一次连接尝试（调用方须持有 _gate）。返回代表该尝试的任务。</summary>
    private Task<bool> ConnectAttemptLocked(ulong address, bool fromReconnectLoop, CancellationToken cancellationToken)
    {
        if (!fromReconnectLoop)
        {
            // 外部显式连接：取消重连循环（循环自身发起时不取消，否则会杀掉自己的 token）
            CancelReconnectLoopLocked();
        }

        int generation = ++_generation;
        _connectTask = RunConnectAttemptAsync(address, generation, fromReconnectLoop, cancellationToken);
        return _connectTask;
    }

    /// <summary>包装连接流程：结束后若 <see cref="_connectTask"/> 仍是自己则清空，避免陈旧任务被复用。</summary>
    private async Task<bool> RunConnectAttemptAsync(
        ulong address, int generation, bool fromReconnectLoop, CancellationToken cancellationToken)
    {
        var attempt = ConnectCoreAsync(address, generation, fromReconnectLoop, cancellationToken);
        try
        {
            return await attempt.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connectTask, attempt))
                {
                    _connectTask = null;
                }
            }
        }
    }

    /// <summary>
    /// 完整连接流程：GATT 连接 → 握手 → 200ms → 依次读电量/降噪模式/EQ/游戏模式
    /// （Python ble_worker._do_connect → _do_refresh）。成功后进入 Connected 并启动
    /// 15s 电量刷新；失败清理并按需调度重连。
    /// </summary>
    private async Task<bool> ConnectCoreAsync(
        ulong address, int generation, bool fromReconnectLoop, CancellationToken cancellationToken)
    {
        string name;
        lock (_gate)
        {
            name = _targetName;
        }
        string displayName = name.Length > 0 ? name : BleScanResult.FormatAddress(address);
        SetState(ConnectionState.Connecting, $"正在连接 {displayName}…");

        IBleTransport transport = _transportFactory.Create();
        var client = new H3Client(transport);
        client.NotificationReceived += OnDeviceNotification;
        transport.Disconnected += () => OnTransportDisconnected(transport);
        try
        {
            await transport.ConnectAsync(address, cancellationToken).ConfigureAwait(false);

            SetState(ConnectionState.Connecting, "已建立 BLE 链路，正在握手…");
            Packet? handshake = await client.HandshakeAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (handshake is null)
            {
                throw new TimeoutException("耳机握手无响应：可能不是 QCY H3，或正被其它设备占用。");
            }

            // Python 参考实现：握手后 sleep 0.2 再读状态
            await Task.Delay(PostHandshakeDelay, cancellationToken).ConfigureAwait(false);

            bool installed;
            lock (_gate)
            {
                installed = generation == _generation && !_disposed;
                if (installed)
                {
                    _transport = transport;
                    _client = client;
                }
            }
            if (!installed)
            {
                throw new OperationCanceledException("连接流程已过期。");
            }

            await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
            await RefreshNoiseModeAsync(cancellationToken).ConfigureAwait(false);
            await RefreshEqAsync(cancellationToken).ConfigureAwait(false);
            await RefreshGameModeAsync(cancellationToken).ConfigureAwait(false);

            string connectedStatus;
            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    throw new OperationCanceledException("连接流程已过期。");
                }
                _consecutiveFailures = 0;
                StartBatteryLoopLocked();
                connectedStatus = $"已连接 {displayName}";
                ApplyTransitionLocked(ConnectionState.Connected, connectedStatus);
            }
            FireTransition(ConnectionState.Connected, connectedStatus, stateChanged: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DisposeSession((transport, client));
            bool current;
            lock (_gate)
            {
                current = generation == _generation && !_disposed;
            }
            if (current)
            {
                // 过期流程不动状态（新流程已在跑）
                SetState(ConnectionState.Disconnected, "连接已取消。");
            }
            return false;
        }
        catch (Exception exception)
        {
            DisposeSession((transport, client));
            bool current;
            lock (_gate)
            {
                current = generation == _generation && !_disposed;
            }
            if (current)
            {
                string status;
                lock (_gate)
                {
                    _consecutiveFailures++;
                    status = $"连接失败：{exception.Message}";
                    ApplyTransitionLocked(ConnectionState.Disconnected, status);
                }
                FireTransition(ConnectionState.Disconnected, status, stateChanged: true);
                if (!fromReconnectLoop)
                {
                    ScheduleReconnect();
                }
            }
            return false;
        }
    }

    // ───────────────────────── 周期刷新与推送解析 ─────────────────────────

    private void StartBatteryLoopLocked()
    {
        var previous = _sessionCts;
        _sessionCts = new CancellationTokenSource();
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            previous.Dispose();
        }

        CancellationToken cancellationToken = _sessionCts.Token;
        _ = Task.Run(() => BatteryLoopAsync(cancellationToken));
    }

    private async Task BatteryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(BatteryRefreshInterval);
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RefreshBatteryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 电量刷新失败不主动断开：真断线由 transport.Disconnected 统一处理
            }
        }
    }

    private async Task RefreshBatteryAsync(CancellationToken cancellationToken)
    {
        var client = CurrentClient();
        if (client is null)
        {
            return;
        }
        int? percent = await client.GetBatteryPercentAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (percent is not int value)
        {
            return;
        }
        Action<int>? handler;
        lock (_gate)
        {
            if (_client != client)
            {
                return;
            }
            handler = BatteryChanged;
        }
        handler?.Invoke(value);
    }

    private async Task RefreshNoiseModeAsync(CancellationToken cancellationToken)
    {
        var client = CurrentClient();
        if (client is null)
        {
            return;
        }
        AncModeInfo? info = await client.GetAncModeInfoAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (info is not null)
        {
            UpdateDeviceData(data => data with { NoiseMode = AncMode.ToIndex(info), NoiseModeInfo = info });
        }
    }

    /// <summary>
    /// EQ 读取：v1（cmd 0x20）优先——本机固件对 v2 读回空载荷；v1 为空时回退 v2（旧固件兼容）。
    /// </summary>
    private async Task RefreshEqAsync(CancellationToken cancellationToken)
    {
        var client = CurrentClient();
        if (client is null)
        {
            return;
        }
        EqV1Info? v1 = await client.GetEqV1Async(CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (v1 is not null)
        {
            UpdateDeviceData(data => data with { EqV1 = v1 });
            return;
        }
        EqInfo? eq = await client.GetEqInfoAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (eq is not null)
        {
            UpdateDeviceData(data => data with { EqPresetId = eq.PresetId, EqOffsets = eq.Offsets });
        }
    }

    private async Task RefreshGameModeAsync(CancellationToken cancellationToken)
    {
        var client = CurrentClient();
        if (client is null)
        {
            return;
        }
        bool? game = await client.GetWorkModeAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (game is bool value)
        {
            UpdateDeviceData(data => data with { GameMode = value });
        }
    }

    /// <summary>
    /// 耳机主动推送（未匹配 pending sn 的通知帧）：
    /// ADV 推送里的电量/游戏模式 TLV、CUSTOM 17 03… 降噪模式负载。
    /// </summary>
    private void OnDeviceNotification(Packet packet)
    {
        lock (_gate)
        {
            if (_client is null)
            {
                return;
            }
        }

        if (packet.OpCode is OpCode.NotifyAdvInfo or OpCode.GetAdvInfo)
        {
            foreach (var entry in TlvParser.Parse(packet.Params))
            {
                if (entry.Type == AdvType.BatteryQuantity && entry.Value.Length >= 1)
                {
                    RaiseBattery(entry.Value[0]);
                }
                else if (entry.Type == AdvType.WorkMode && entry.Value.Length >= 1)
                {
                    UpdateDeviceData(data => data with { GameMode = entry.Value[0] == 0x02 });
                }
            }
        }

        if (packet.OpCode == OpCode.Custom
            && packet.Params.Length >= 5
            && packet.Params[0] == 0x17
            && packet.Params[1] == 0x03)
        {
            AncModeInfo? info = AncMode.Decode(packet.Params);
            if (info is not null)
            {
                UpdateDeviceData(data => data with { NoiseMode = AncMode.ToIndex(info), NoiseModeInfo = info });
            }
        }
    }

    private void RaiseBattery(int percent)
    {
        Action<int>? handler;
        lock (_gate)
        {
            handler = BatteryChanged;
        }
        handler?.Invoke(percent);
    }

    private void UpdateDeviceData(Func<DeviceData, DeviceData> update)
    {
        Action<DeviceData>? handler;
        DeviceData snapshot;
        lock (_gate)
        {
            _deviceData = update(_deviceData);
            snapshot = _deviceData;
            handler = DeviceDataChanged;
        }
        handler?.Invoke(snapshot);
    }

    // ───────────────────────── 自动重连 ─────────────────────────

    private void OnAdvertisementReceived(BleScanResult result)
    {
        TaskCompletionSource<BleScanResult>? signal = null;
        lock (_gate)
        {
            if (_advertisementSignal is not null && MatchesTargetLocked(result))
            {
                signal = _advertisementSignal;
                _advertisementSignal = null;
            }
        }
        signal?.TrySetResult(result);
    }

    /// <summary>目标匹配：地址相等，或名称前缀匹配目标名，或兜底"QCY H3"前缀。</summary>
    private bool MatchesTargetLocked(BleScanResult result)
    {
        if (_targetAddress == result.Address)
        {
            return true;
        }
        if (_targetName.Length > 0
            && result.Name.StartsWith(_targetName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return result.Name.StartsWith(FallbackNamePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>调度重连循环（已在跑/不该跑时不做任何事）。</summary>
    private void ScheduleReconnect()
    {
        lock (_gate)
        {
            if (_disposed || !AutoReconnect || _targetAddress is null
                || _state is ConnectionState.Connecting or ConnectionState.Connected)
            {
                return;
            }
            if (_reconnectLoopTask is not null && !_reconnectLoopTask.IsCompleted)
            {
                return;
            }

            var cancellationTokenSource = new CancellationTokenSource();
            _reconnectCts = cancellationTokenSource;
            _reconnectLoopTask =
                ReconnectLoopAsync(cancellationTokenSource, cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// 重连循环：按退避等待 → 搜索目标广播 → 看到即连接；失败继续循环
    /// （退避随 _consecutiveFailures 递增到封顶）。连接成功即退出。
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ulong target;
                string name;
                TimeSpan delay;
                lock (_gate)
                {
                    if (_disposed || !AutoReconnect || _targetAddress is null
                        || _state is ConnectionState.Connecting or ConnectionState.Connected)
                    {
                        return;
                    }
                    target = _targetAddress.Value;
                    name = _targetName;
                    delay = _consecutiveFailures == 0
                        ? TimeSpan.Zero
                        : ReconnectBackoff[Math.Min(_consecutiveFailures - 1, ReconnectBackoff.Length - 1)];
                }

                if (delay > TimeSpan.Zero)
                {
                    SetState(ConnectionState.Scanning, $"连接失败，{delay.TotalSeconds:0} 秒后重试…");
                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                string searching;
                lock (_gate)
                {
                    if (_disposed || _state is ConnectionState.Connecting or ConnectionState.Connected)
                    {
                        return;
                    }
                    searching = name.Length > 0 ? name : BleScanResult.FormatAddress(target);
                }

                // 先挂信号源再开扫描：任何时刻到达的目标广播都不会丢
                var signal = new TaskCompletionSource<BleScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    if (_disposed || _state is ConnectionState.Connecting or ConnectionState.Connected)
                    {
                        return;
                    }
                    _advertisementSignal = signal;
                }

                try
                {
                    _scanner.Start();
                }
                catch (BluetoothUnavailableException exception)
                {
                    // 蓝牙没开：不再空转，等下次显式触发（连接失败/断线）重试
                    SetState(ConnectionState.Disconnected, exception.Message);
                    return;
                }

                SetState(ConnectionState.Scanning, $"正在搜索 {searching}…");

                BleScanResult seen;
                try
                {
                    seen = await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    _scanner.Stop();
                }
                catch
                {
                }

                bool connected = await ConnectAttemptLocked(seen.Address, fromReconnectLoop: true, cancellationToken)
                    .ConfigureAwait(false);
                if (connected)
                {
                    return;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _advertisementSignal = null;
                if (ReferenceEquals(_reconnectCts, cancellationTokenSource))
                {
                    _reconnectCts = null;
                }
            }
            cancellationTokenSource.Dispose();
            try
            {
                _scanner.Stop();
            }
            catch
            {
            }
        }
    }

    private void CancelReconnectLoopLocked()
    {
        _advertisementSignal = null;
        var cancellationTokenSource = _reconnectCts;
        _reconnectCts = null;
        if (cancellationTokenSource is not null)
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            cancellationTokenSource.Dispose();
        }
    }

    /// <summary>当前会话的传输层意外断开：拆会话 → 抛事件 → 调度重连。</summary>
    private void OnTransportDisconnected(IBleTransport transport)
    {
        (IBleTransport?, H3Client?) session;
        lock (_gate)
        {
            if (_transport != transport)
            {
                return;   // 陈旧尝试的断线通知，当前会话不是它
            }
            _generation++;
            session = TeardownSessionLocked("连接已断开。");
        }
        DisposeSession(session);
        FireTransition(ConnectionState.Disconnected, "连接已断开。", stateChanged: true);
        ScheduleReconnect();
    }

    // ───────────────────────── 状态与清理 ─────────────────────────

    private H3Client? CurrentClient()
    {
        lock (_gate)
        {
            return _client;
        }
    }

    private void SetState(ConnectionState state, string status)
    {
        bool stateChanged;
        lock (_gate)
        {
            stateChanged = ApplyTransitionLocked(state, status);
        }
        FireTransition(state, status, stateChanged);
    }

    /// <summary>更新状态字段（调用方持有 _gate），返回状态是否实际变化。</summary>
    private bool ApplyTransitionLocked(ConnectionState state, string status)
    {
        bool stateChanged = _state != state;
        _state = state;
        _statusText = status;
        return stateChanged;
    }

    /// <summary>在锁外触发状态/状态文本事件（处理器不得同步重入管理器）。</summary>
    private void FireTransition(ConnectionState state, string status, bool stateChanged)
    {
        if (stateChanged)
        {
            StateChanged?.Invoke(state);
        }
        StatusText?.Invoke(status);
    }

    /// <summary>
    /// 拆除当前会话（调用方持有 _gate）：停 15s 电量刷新、清 pending 广播信号、
    /// 状态置 Disconnected。返回需要在锁外 Dispose 的对象。
    /// </summary>
    private (IBleTransport? Transport, H3Client? Client) TeardownSessionLocked(string status)
    {
        var transport = _transport;
        var client = _client;
        _transport = null;
        _client = null;
        _advertisementSignal = null;

        var sessionCts = _sessionCts;
        _sessionCts = null;
        if (sessionCts is not null)
        {
            try
            {
                sessionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            sessionCts.Dispose();
        }

        ApplyTransitionLocked(ConnectionState.Disconnected, status);
        return (transport, client);
    }

    private static void DisposeSession((IBleTransport? Transport, H3Client? Client) session)
    {
        session.Client?.Dispose();
        session.Transport?.Dispose();
    }
}
