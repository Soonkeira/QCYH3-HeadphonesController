using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Core.App;

/// <summary>
/// 统一连接状态机（9 态）：
/// <list type="bullet">
/// <item><see cref="Disconnected"/>：无链路（初始 / 手动断开 / 显式失败且不重连）。</item>
/// <item><see cref="Reconnecting"/>：重连循环退避等待中。</item>
/// <item><see cref="Scanning"/>：重连循环正在搜索目标广播。</item>
/// <item><see cref="Unreachable"/>：搜索超时仍未见到目标广播（耳机关机/超范围），持续监听中。</item>
/// <item><see cref="Connecting"/>：GATT 连接 + 握手进行中。</item>
/// <item><see cref="Syncing"/>：链路已建立，握手后的初始化读进行中。</item>
/// <item><see cref="Connected"/>：正常使用中。</item>
/// <item><see cref="Degraded"/>：链路已建立但最近命令连续失败（阈值 2）——横幅由它驱动。</item>
/// <item><see cref="BluetoothOff"/>：蓝牙 radio 关闭/缺失（D4），恢复后自动回到重连流程。</item>
/// </list>
/// 链路级判断用 <see cref="IsLinkEstablished"/>（Syncing/Connected/Degraded 视为已连接）。
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Reconnecting,
    Scanning,
    Unreachable,
    Connecting,
    Syncing,
    Connected,
    Degraded,
    BluetoothOff,
}

/// <summary>降噪模式写入结果。</summary>
/// <param name="Acknowledged">false = 耳机对写入无响应，模式可能未生效。</param>
/// <param name="ReadBackIndex">回读到的旧六模式索引（场景模式为 null）；null = 回读无响应。</param>
/// <param name="ReadBackInfo">回读到的完整解码结果（含场景模式）；null = 回读无响应或负载不合法。</param>
public sealed record NoiseModeSetResult(bool Acknowledged, int? ReadBackIndex, AncModeInfo? ReadBackInfo = null);

/// <summary>
/// H3 连接管理：统一连接状态机 + 命令健康 + 设备数据快照 + 写命令串行化。
/// <para>
/// 职责（阶段 2 落地，plan.md §1.2）：
/// 1. 所有状态转换集中在此并带合法性校验（非法转换经 <see cref="TransitionRejected"/> 上报，不崩溃）；
/// 2. 命令健康：连接态下命令（读/写）失败连续计数，达 2 次进入 <see cref="ConnectionState.Degraded"/>，
///    任一命令成功或重新完成全量同步即恢复 <see cref="ConnectionState.Connected"/>（横幅语义的唯一权威）；
/// 3. 设备数据快照 <see cref="DeviceData"/>：每项带 <see cref="DataValidity"/>，断线全量转 Stale，
///    读失败转 Error（保留旧值，绝不写默认值），整体快照原子发布；
/// 4. 所有 RCSP 命令（读/写，含回读）经 <c>SemaphoreSlim(1,1)</c> 串行化（plan.md D8）；
/// 5. 蓝牙不可用进入 <see cref="ConnectionState.BluetoothOff"/> 并监视 radio（或轮询兜底），
///    蓝牙恢复后自动重新连接（plan.md D4）。
/// </para>
/// <para>
/// 连接生命周期：握手后的初始化读逐项容错——单项失败只标 <see cref="DataValidity.Error"/> 并计入命令健康，
/// 链路保持（最多进入 <see cref="ConnectionState.Degraded"/>，绝不断开）；状态先经
/// <see cref="ConnectionState.Syncing"/> 再填充数据（数据事件不会先于状态事件到达）。
/// 每个连接流程持有代数（generation）：过期流程的结果与事件一律丢弃，不得覆盖新会话。
/// 单次读超时不视为断线（只影响命令健康）；只有 transport 断开事件（GATT 会话丢失）才走断线路径：
/// 数据全部失效 + 自动重连（1s/5s/30s 封顶退避）。
/// <para>线程模型：内部一把锁串行化状态转移；事件在线程池线程触发，处理器不得同步重入管理器
/// （<see cref="TransitionRejected"/> 在锁内触发，处理器只允许记日志）。UI 层自行 Dispatcher 处理。</para>
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

    /// <summary>
    /// 重连搜索超时：持续监听目标广播超过此时长仍未见到 → 进入
    /// <see cref="ConnectionState.Unreachable"/>（继续监听，广播出现立即连接）。
    /// </summary>
    public TimeSpan UnreachableAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 计划内断开（transport 断开事件）后的首次自动重连最小延迟。真机实测（阶段 4b）：
    /// 耳机断开链路后立即重连会被 Windows 拒绝（AccessDenied），间隔 ≥6-15 秒才稳定；
    /// 这里取 2 秒起步，若仍遇 AccessDenied 失败则由 <see cref="AccessDeniedReconnectDelay"/> 接管。
    /// </summary>
    public TimeSpan PostDisconnectReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 连接失败为 AccessDenied（拒绝访问）类时的单独重连延迟（不走常规退避序列）。
    /// 真机实测该失败源于 Windows 对刚断开链路的冷却，5 秒重试通常已脱离拒绝窗口。
    /// </summary>
    public TimeSpan AccessDeniedReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>命令健康阈值：连接态下连续失败的命令达到该数量 → 进入 <see cref="ConnectionState.Degraded"/>。</summary>
    public const int CommandFailureThreshold = 2;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostHandshakeDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly IBleTransportFactory _transportFactory;
    private readonly IBleScanner _scanner;
    private readonly bool _ownsScanner;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private ConnectionState _state = ConnectionState.Disconnected;
    private string _statusText = "未连接";
    private ulong? _targetAddress;
    private string _targetName = "";
    private DeviceData _deviceData = new();

    private IBleTransport? _transport;
    private H3Client? _client;
    private Task<bool>? _connectTask;
    private int _connectTaskGeneration;
    private Task? _reconnectLoopTask;
    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _sessionCts;
    private TaskCompletionSource<BleScanResult>? _advertisementSignal;

    private int _generation;
    private bool _disposed;

    /// <summary>重连失败计数（驱动退避递增；D10 改名，与命令健康计数 <see cref="_commandFailures"/> 区分）。</summary>
    private int _reconnectFailures;

    /// <summary>命令健康计数：连接态下连续失败次数（成功清零；D1：从 AppState 下沉到此处唯一所有）。</summary>
    private int _commandFailures;

    /// <summary>最近一次进入连接态时的目标显示名（Degraded 恢复文案用）。</summary>
    private string _connectedName = "";

    /// <summary>
    /// 最近一次会话结束（transport 断开事件）时刻；null=无会话结束史。
    /// 计划内断开后的首次重连冷却（<see cref="PostDisconnectReconnectDelay"/>）依据。
    /// </summary>
    private DateTimeOffset? _sessionEndedUtc;

    /// <summary>
    /// 最近一次连接失败是否为 AccessDenied 类（<see cref="IsAccessDenied"/>）。
    /// 保持至下一次失败（覆盖）或连接成功（清除）；期间重连一律用
    /// <see cref="AccessDeniedReconnectDelay"/> 而非常规退避序列。
    /// </summary>
    private bool _lastFailureWasAccessDenied;

    private IBluetoothRadioMonitor? _radioMonitor;

    /// <summary>目标不可达/连接失败时是否自动重连（CLI 一次性命令可关掉）。</summary>
    public bool AutoReconnect { get; set; } = true;

    public ConnectionManager(
        IBleTransportFactory? transportFactory = null,
        IBleScanner? scanner = null,
        IBluetoothRadioMonitor? radioMonitor = null)
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
        _radioMonitor = radioMonitor;
        if (_radioMonitor is not null)
        {
            _radioMonitor.AvailabilityChanged += OnRadioAvailabilityChanged;
        }
        _scanner.AdvertisementReceived += OnAdvertisementReceived;
    }

    // ───────────────────────── 状态与快照查询 ─────────────────────────

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

    /// <summary>最近一条状态文本（人类可读中文，对应 <see cref="StatusText"/> 事件）。</summary>
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

    /// <summary>链路是否已建立（<see cref="ConnectionState.Syncing"/> / Connected / Degraded）。</summary>
    public bool IsLinkEstablished
    {
        get
        {
            lock (_gate)
            {
                return IsSessionState(_state);
            }
        }
    }

    /// <summary>是否处于命令健康降级（<see cref="ConnectionState.Degraded"/>）。</summary>
    public bool IsDegraded
    {
        get
        {
            lock (_gate)
            {
                return _state == ConnectionState.Degraded;
            }
        }
    }

    /// <summary>命令健康连续失败计数（诊断/测试用；0 = 健康）。</summary>
    public int CommandFailures
    {
        get
        {
            lock (_gate)
            {
                return _commandFailures;
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

    /// <summary>最近一次设备数据快照（不可变）。</summary>
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

    // ───────────────────────── 事件 ─────────────────────────

    /// <summary>状态转移（每次实际变化触发一次）。</summary>
    public event Action<ConnectionState>? StateChanged;

    /// <summary>人类可读的过程状态（"正在连接…" / "连接失败：…" 等）。</summary>
    public event Action<string>? StatusText;

    /// <summary>电量百分比（0-100）：初始读数、15s 定时刷新、耳机主动推送（兼容通道，快照同时更新）。</summary>
    public event Action<int>? BatteryChanged;

    /// <summary>设备数据快照任一更新时抛整体不可变快照（原子发布，UI 不会看到半新半旧）。</summary>
    public event Action<DeviceData>? DeviceDataChanged;

    /// <summary>诊断消息（后台异常、扫描失败等）；App 层应接到 ErrorLog。</summary>
    public event Action<string>? Diagnostics;

    /// <summary>
    /// 非法状态转换被拒绝（记日志不崩溃）。注意：在内部锁内触发，
    /// 处理器只允许记日志，不得同步调用管理器其它成员。
    /// </summary>
    public event Action<string>? TransitionRejected;

    /// <summary>
    /// 状态转换合法性表（纯函数，测试与诊断用）。同状态重入总是允许（仅刷新状态文本）。
    /// 任何状态都允许回到 <see cref="ConnectionState.Disconnected"/>（手动断开/释放/兜底）。
    /// </summary>
    public static bool IsLegalTransition(ConnectionState from, ConnectionState to)
    {
        if (from == to)
        {
            return true;
        }
        return to switch
        {
            ConnectionState.Disconnected => true,
            ConnectionState.Connecting => from is ConnectionState.Disconnected
                or ConnectionState.Scanning or ConnectionState.Reconnecting
                or ConnectionState.Unreachable or ConnectionState.BluetoothOff,
            ConnectionState.Syncing => from is ConnectionState.Connecting,
            ConnectionState.Connected => from is ConnectionState.Syncing or ConnectionState.Degraded,
            ConnectionState.Degraded => from is ConnectionState.Syncing
                or ConnectionState.Connected or ConnectionState.Degraded,
            ConnectionState.Reconnecting => from is ConnectionState.Connecting
                or ConnectionState.Syncing or ConnectionState.Connected or ConnectionState.Degraded
                or ConnectionState.Scanning or ConnectionState.Reconnecting
                or ConnectionState.Unreachable or ConnectionState.BluetoothOff,
            ConnectionState.Scanning => from is ConnectionState.Disconnected
                or ConnectionState.Scanning or ConnectionState.Reconnecting
                or ConnectionState.BluetoothOff,
            ConnectionState.Unreachable => from is ConnectionState.Scanning or ConnectionState.Unreachable,
            ConnectionState.BluetoothOff => from is ConnectionState.Disconnected
                or ConnectionState.Scanning or ConnectionState.Reconnecting
                or ConnectionState.Unreachable or ConnectionState.Connecting
                or ConnectionState.BluetoothOff,
            _ => false,
        };
    }

    /// <summary>链路已建立（连接任务进行中/连接完成）的状态集合。</summary>
    private static bool IsSessionState(ConnectionState state)
        => state is ConnectionState.Connecting or ConnectionState.Syncing
            or ConnectionState.Connected or ConnectionState.Degraded;

    // ───────────────────────── 目标与连接入口 ─────────────────────────

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
    /// 已连接/响应异常直接成功）。返回 false = 连接失败（原因见 <see cref="CurrentStatus"/>）。
    /// </summary>
    /// <exception cref="InvalidOperationException">未设置目标（先调用 <see cref="SetTarget"/>）。</exception>
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is ConnectionState.Connected or ConnectionState.Degraded
                || (_state == ConnectionState.Syncing && _connectTask is null))
            {
                return Task.FromResult(true);
            }
            // 幂等仅对"当前代数"的在途任务成立；被断开/新会话取代的旧任务直接废弃
            if (_connectTask is not null && !_connectTask.IsCompleted && _connectTaskGeneration == _generation)
            {
                return _connectTask;
            }

            ulong address = _targetAddress
                ?? throw new InvalidOperationException("尚未选择目标设备：请先调用 SetTarget(address, name)。");
            return ConnectAttemptLocked(address, fromReconnectLoop: false, cancellationToken);
        }
    }

    /// <summary>主动断开（停掉定时刷新与重连循环，数据全部失效）。幂等。</summary>
    public void Disconnect()
    {
        SessionTeardown teardown;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation++;                       // 使在途连接流程过期
            CancelReconnectLoopLocked();
            teardown = TeardownSessionLocked(ConnectionState.Disconnected, "已断开连接。");
        }
        DisposeSession((teardown.Transport, teardown.Client));
        FireTransition(ConnectionState.Disconnected, "已断开连接。", teardown.StateChanged);
        PublishSnapshot(teardown.Snapshot);
    }

    public void Dispose()
    {
        SessionTeardown teardown;
        IBluetoothRadioMonitor? radioMonitor;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _generation++;
            CancelReconnectLoopLocked();
            teardown = TeardownSessionLocked(ConnectionState.Disconnected, "已释放。");
            _scanner.AdvertisementReceived -= OnAdvertisementReceived;
            radioMonitor = _radioMonitor;
        }

        DisposeSession((teardown.Transport, teardown.Client));
        if (teardown.StateChanged)
        {
            FireTransition(ConnectionState.Disconnected, "已释放。", stateChanged: true);
        }
        PublishSnapshot(teardown.Snapshot);
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
        radioMonitor?.Dispose();
        _commandGate.Dispose();
    }

    // ───────────────────────── 写命令（串行化 + 健康记账） ─────────────────────────

    /// <summary>
    /// 已连接时写入降噪模式并回读确认。返回 null = 耳机对写入无响应（模式可能未生效）。
    /// </summary>
    public Task<NoiseModeSetResult?> SetNoiseModeAsync(byte[] modeBytes, CancellationToken cancellationToken = default)
    {
        return WithCommandGateAsync("切换降噪模式", async (client, token) =>
        {
            try
            {
                NoiseModeSetResult? result = null;
                var response = await client.SetNoiseModeAsync(modeBytes, CommandTimeout, token).ConfigureAwait(false);
                if (response is not null)
                {
                    AncModeInfo? readBack = await client.GetAncModeInfoAsync(CommandTimeout, token).ConfigureAwait(false);
                    if (readBack is not null)
                    {
                        UpdateDeviceData(data => data with
                        {
                            NoiseMode = AncMode.ToIndex(readBack),
                            NoiseModeInfo = readBack,
                            NoiseModeValidity = DataValidity.Available,
                            NoiseModeUpdatedUtc = DateTimeOffset.UtcNow,
                        });
                    }
                    else
                    {
                        MarkNoiseModeError();
                    }
                    result = new NoiseModeSetResult(Acknowledged: true, ReadBackIndex: AncMode.ToIndex(readBack), ReadBackInfo: readBack);
                }
                else
                {
                    MarkNoiseModeError();
                }
                CompleteCommand(result is not null);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkNoiseModeError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 全量刷新（电量 / 降噪模式 / EQ / 游戏模式 / 自动关机，顺序单次读）。
    /// 未连接时是安全的空操作（各项内部自检 CurrentClient）。
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        await RefreshNoiseModeAsync(cancellationToken).ConfigureAwait(false);
        await RefreshEqAsync(cancellationToken).ConfigureAwait(false);
        await RefreshGameModeAsync(cancellationToken).ConfigureAwait(false);
        await RefreshPowerManagerAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 已连接时写入 EQ v1 表（eqType 保持读到的值，见 <see cref="H3Client.SetEqV1Async"/>），
    /// 随后回读确认并更新 <see cref="DeviceData.EqV1"/>（回读失败时用写入值做乐观快照）。
    /// 返回 null = 耳机对写入无响应。
    /// </summary>
    public Task<EqV1Info?> SetEqV1Async(
        byte eqType, int masterGain, IReadOnlyList<EqV1Band> bands, CancellationToken cancellationToken = default)
    {
        return WithCommandGateAsync("写入 EQ", async (client, token) =>
        {
            try
            {
                EqV1Info? result = null;
                var response = await client.SetEqV1Async(eqType, masterGain, bands, CommandTimeout, token)
                    .ConfigureAwait(false);
                if (response is not null)
                {
                    EqV1Info? confirmed = await client.GetEqV1Async(CommandTimeout, token).ConfigureAwait(false);
                    var snapshot = confirmed ?? new EqV1Info(eqType, masterGain, new List<EqV1Band>(bands));
                    UpdateDeviceData(data => data with
                    {
                        EqV1 = snapshot,
                        EqValidity = DataValidity.Available,
                        EqUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                    result = snapshot;
                }
                else
                {
                    MarkEqError();
                }
                CompleteCommand(result is not null);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkEqError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 已连接时读取 EQ v1 表并更新 <see cref="DeviceData.EqV1"/>。
    /// 返回 null = 未连接或设备未应答（不抛异常）。
    /// </summary>
    public Task<EqV1Info?> GetEqV1Async(CancellationToken cancellationToken = default)
    {
        if (CurrentClient() is null)
        {
            return Task.FromResult<EqV1Info?>(null);
        }
        return WithCommandGateAsync("读取 EQ", async (client, token) =>
        {
            try
            {
                EqV1Info? info = await client.GetEqV1Async(CommandTimeout, token).ConfigureAwait(false);
                if (info is not null)
                {
                    UpdateDeviceData(data => data with
                    {
                        EqV1 = info,
                        EqValidity = DataValidity.Available,
                        EqUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                }
                else
                {
                    MarkEqError();
                }
                CompleteCommand(info is not null);
                return info;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkEqError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 已连接时写入游戏模式（低延迟）并回读确认，更新 <see cref="DeviceData.GameMode"/>。
    /// 返回 null = 耳机对写入无响应。
    /// </summary>
    public Task<bool?> SetGameModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        return WithCommandGateAsync("切换游戏模式", async (client, token) =>
        {
            try
            {
                bool? result = null;
                var response = await client.SetGameModeAsync(enabled, CommandTimeout, token).ConfigureAwait(false);
                if (response is not null)
                {
                    bool? readBack = await client.GetWorkModeAsync(CommandTimeout, token).ConfigureAwait(false);
                    bool value = readBack ?? enabled;
                    UpdateDeviceData(data => data with
                    {
                        GameMode = value,
                        GameModeValidity = DataValidity.Available,
                        GameModeUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                    result = value;
                }
                else
                {
                    MarkGameModeError();
                }
                CompleteCommand(result is not null);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkGameModeError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 已连接时读取自动关机配置（CUSTOM cmd 0x14）并更新 <see cref="DeviceData.AutoOffMinutes"/>。
    /// 返回 null = 未连接或设备未应答（不抛异常）。
    /// </summary>
    public Task<PowerManagerInfo?> GetPowerManagerAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentClient() is null)
        {
            return Task.FromResult<PowerManagerInfo?>(null);
        }
        UpdateDeviceDataWhen(
            data => data.AutoOffValidity == DataValidity.Unknown,
            data => data with { AutoOffValidity = DataValidity.Loading });
        return WithCommandGateAsync("自动关机读取", async (client, token) =>
        {
            try
            {
                PowerManagerInfo? info = await client.GetPowerManagerAsync(CommandTimeout, token).ConfigureAwait(false);
                if (info is not null)
                {
                    UpdateDeviceData(data => data with
                    {
                        AutoOffMinutes = info.PowerOffMinutes,
                        AutoOffValidity = DataValidity.Available,
                        AutoOffUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                }
                else
                {
                    MarkAutoOffError();
                }
                CompleteCommand(info is not null);
                return info;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkAutoOffError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 已连接时写入自动关机时长（分钟，<see cref="PowerManagerInfo.NeverOffMinutes"/>=不自动关机）
    /// 并回读确认，更新 <see cref="DeviceData.AutoOffMinutes"/>（回读失败时用写入值做乐观快照，
    /// 模式对齐 <see cref="SetEqV1Async"/>；写入的当前计时字段固定 0——重设时长即重新计时）。
    /// 返回 null = 耳机对写入无响应。
    /// </summary>
    public Task<PowerManagerInfo?> SetPowerManagerAsync(int minutes, CancellationToken cancellationToken = default)
    {
        return WithCommandGateAsync("写入自动关机", async (client, token) =>
        {
            try
            {
                PowerManagerInfo? result = null;
                var response = await client
                    .SetPowerManagerAsync(minutes, currentMinutes: 0, CommandTimeout, token)
                    .ConfigureAwait(false);
                if (response is not null)
                {
                    PowerManagerInfo? readBack = await client.GetPowerManagerAsync(CommandTimeout, token)
                        .ConfigureAwait(false);
                    PowerManagerInfo snapshot = readBack ?? new PowerManagerInfo(minutes, 0);
                    UpdateDeviceData(data => data with
                    {
                        AutoOffMinutes = snapshot.PowerOffMinutes,
                        AutoOffValidity = DataValidity.Available,
                        AutoOffUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                    result = snapshot;
                }
                else
                {
                    MarkAutoOffError();
                }
                CompleteCommand(result is not null);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkAutoOffError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 已连接时读取按键映射整表（GET_ADV_INFO type=2，含出厂槽 action≥0x81 的原始三元组）
    /// 并更新 <see cref="DeviceData.KeyMap"/>。返回 null = 未连接或设备未应答（不抛异常）。
    /// </summary>
    public Task<List<KeyMappingEntry>?> GetKeyMappingsAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentClient() is null)
        {
            return Task.FromResult<List<KeyMappingEntry>?>(null);
        }
        UpdateDeviceDataWhen(
            data => data.KeyMapValidity == DataValidity.Unknown,
            data => data with { KeyMapValidity = DataValidity.Loading });
        return WithCommandGateAsync("按键映射读取", async (client, token) =>
        {
            try
            {
                List<KeyMappingEntry>? entries = await client
                    .GetKeyMappingsAsync(CommandTimeout, token)
                    .ConfigureAwait(false);
                if (entries is not null)
                {
                    UpdateDeviceData(data => data with
                    {
                        KeyMap = entries,
                        KeyMapValidity = DataValidity.Available,
                        KeyMapUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                }
                else
                {
                    MarkKeyMapError();
                }
                CompleteCommand(entries is not null);
                return entries;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkKeyMapError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 已连接时整表写入按键映射并回读确认，更新 <see cref="DeviceData.KeyMap"/>
    /// （回读失败时用写入值做乐观快照）。<b>必须整表写入</b>——SDK 把载荷当完整配置，
    /// 部分表会清掉其余按键（BOTOES.md 实测）；空表由 <see cref="KeyMap.BuildSetAdvInfoPayload"/> 拒绝。
    /// 返回 null = 耳机对写入无响应。
    /// </summary>
    public Task<IReadOnlyList<KeyMappingEntry>?> SetKeyMappingsAsync(
        IReadOnlyList<KeyMappingEntry> entries, CancellationToken cancellationToken = default)
    {
        return WithCommandGateAsync("写入按键映射", async (client, token) =>
        {
            try
            {
                IReadOnlyList<KeyMappingEntry>? result = null;
                var response = await client.SetKeyMappingsAsync(entries, CommandTimeout, token).ConfigureAwait(false);
                if (response is not null)
                {
                    List<KeyMappingEntry>? readBack = await client.GetKeyMappingsAsync(CommandTimeout, token)
                        .ConfigureAwait(false);
                    IReadOnlyList<KeyMappingEntry> snapshot = readBack ?? entries;
                    UpdateDeviceData(data => data with
                    {
                        KeyMap = snapshot,
                        KeyMapValidity = DataValidity.Available,
                        KeyMapUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                    result = snapshot;
                }
                else
                {
                    MarkKeyMapError();
                }
                CompleteCommand(result is not null);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkKeyMapError();
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 已连接时播放提示音（CUSTOM <c>3D 01 &lt;toneID&gt;</c>，R2 级外放音效）：
    /// 写 + ack 判定（status=0），无回读。返回 false = 未连接或耳机无应答（命令可能未生效）。
    /// </summary>
    public Task<bool> PlayToneAsync(byte toneId, CancellationToken cancellationToken = default)
        => WithAckCommandAsync("播放提示音", (client, token) => client.PlayToneAsync(toneId, CommandTimeout, token), cancellationToken);

    /// <summary>
    /// 已连接时查找耳机（CUSTOM <c>4B 01 01</c>，当前音量响铃定位，R2 级外放音效）：
    /// 写 + ack 判定（status=0），无回读。返回 false = 未连接或耳机无应答。
    /// </summary>
    public Task<bool> FindEarbudsAsync(CancellationToken cancellationToken = default)
        => WithAckCommandAsync("查找耳机", (client, token) => client.FindEarbudsAsync(CommandTimeout, token), cancellationToken);

    /// <summary>
    /// 「写 + ack 判定」命令（提示音 / 查找耳机共用）：无回读通道，应答包（status=0）即成功。
    /// 成败照常计入命令健康；不更新设备数据快照。
    /// </summary>
    private async Task<bool> WithAckCommandAsync(
        string action, Func<H3Client, CancellationToken, Task<Packet?>> command, CancellationToken cancellationToken)
    {
        return await WithCommandGateAsync(action, async (client, token) =>
        {
            try
            {
                Packet? response = await command(client, token).ConfigureAwait(false);
                bool acknowledged = response is not null && response.Status == 0;
                CompleteCommand(acknowledged);
                return acknowledged;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                CompleteCommand(false);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 命令串行化入口：所有 RCSP 命令（读/写，含回读）经 <c>SemaphoreSlim(1,1)</c> 排队；
    /// 拿到信号量后复查会话（等待期间链路可能断开）——未连接直接抛
    /// <see cref="InvalidOperationException"/>，不发起命令、不计命令失败（D1 短路语义的 Core 侧兜底）。
    /// 命令成败由命令体经 <see cref="CompleteCommand"/> 记账。
    /// </summary>
    private async Task<T> WithCommandGateAsync<T>(
        string action, Func<H3Client, CancellationToken, Task<T>> command, CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            H3Client? client = CurrentClient()
                ?? throw new InvalidOperationException($"尚未连接耳机，无法{action}。");
            return await command(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    // ───────────────────────── 命令健康（D1：唯一所有者在 Core） ─────────────────────────

    /// <summary>
    /// 命令成败记账：成功 → 计数清零；若处于 Degraded 则恢复 Connected（横幅随之清除）。
    /// 失败 → 计数 +1；达到阈值且当前 Connected → 进入 Degraded（横幅出现）。
    /// 仅在链路已建立的状态下转换状态；断线路径自会清场。
    /// </summary>
    private void CompleteCommand(bool succeeded)
    {
        ConnectionState? transition = null;
        string status = string.Empty;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (succeeded)
            {
                _commandFailures = 0;
                if (_state == ConnectionState.Degraded)
                {
                    transition = ConnectionState.Connected;
                    status = $"已连接 {_connectedName}";
                }
            }
            else
            {
                if (_commandFailures < int.MaxValue)
                {
                    _commandFailures++;
                }
                if (_state == ConnectionState.Connected && _commandFailures >= CommandFailureThreshold)
                {
                    transition = ConnectionState.Degraded;
                    status = "耳机无响应，正在重试…";
                }
            }
            // 状态字段在锁内应用（含合法性校验）；事件在锁外触发
            if (transition is ConnectionState target && !ApplyTransitionLocked(target, status))
            {
                transition = null;
            }
        }
        if (transition is ConnectionState applied)
        {
            FireTransition(applied, status, stateChanged: true);
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
        _connectTaskGeneration = generation;
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
    /// 完整连接流程：GATT 连接 → 握手 → 200ms → <see cref="ConnectionState.Syncing"/>（逐项容错读
    /// 电量/降噪模式/EQ/游戏模式）→ Connected（或健康降级则 Degraded）。
    /// 失败按类型分流：取消 / 蓝牙不可用（→ BluetoothOff，D4）/ 其它（→ 重连或 Disconnected）。
    /// 过期代数（generation）的流程静默退出，不覆盖新会话。
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

        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return false;   // 已被新会话取代：旧任务不得改动任何状态
            }
        }
        SetState(ConnectionState.Connecting, $"正在连接 {displayName}…");

        IBleTransport transport = _transportFactory.Create();
        var client = new H3Client(transport);
        client.NotificationReceived += OnDeviceNotification;
        transport.Disconnected += () => OnTransportDisconnected(transport);
        bool installed = false;
        try
        {
            await transport.ConnectAsync(address, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    throw new OperationCanceledException("连接流程已过期。");
                }
            }

            SetState(ConnectionState.Connecting, "已建立 BLE 链路，正在握手…");
            Packet? handshake = await client.HandshakeAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (handshake is null)
            {
                throw new TimeoutException("耳机握手无响应：可能不是 QCY H3，或正被其它设备占用。");
            }

            // Python 参考实现：握手后 sleep 0.2 再读状态
            await Task.Delay(PostHandshakeDelay, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    throw new OperationCanceledException("连接流程已过期。");
                }
                _transport = transport;
                _client = client;
                installed = true;
            }

            // D9：先发状态事件（Syncing）再发数据事件；初始化读逐项容错——
            // 单项失败只标 Error / 计命令健康，链路保持，绝不断开。
            SetState(ConnectionState.Syncing, "正在同步耳机数据…");
            MarkSyncStart();
            await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
            await RefreshNoiseModeAsync(cancellationToken).ConfigureAwait(false);
            await RefreshEqAsync(cancellationToken).ConfigureAwait(false);
            await RefreshGameModeAsync(cancellationToken).ConfigureAwait(false);
            await RefreshPowerManagerAsync(cancellationToken).ConfigureAwait(false);

            ConnectionState finalState;
            string finalStatus;
            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    throw new OperationCanceledException("连接流程已过期。");
                }
                bool degraded = _commandFailures >= CommandFailureThreshold;
                if (!degraded)
                {
                    _commandFailures = 0;            // Connected 转换强制 ResetHealth（D1）
                }
                _reconnectFailures = 0;              // 连接成功：重连退避归零
                _lastFailureWasAccessDenied = false; // 连接成功：AccessDenied 冷却标记清除
                _connectedName = displayName;
                finalState = degraded ? ConnectionState.Degraded : ConnectionState.Connected;
                finalStatus = degraded ? $"已连接 {displayName}，耳机响应异常" : $"已连接 {displayName}";
                StartBatteryLoopLocked();
                ApplyTransitionLocked(finalState, finalStatus);
            }
            FireTransition(finalState, finalStatus, stateChanged: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishSnapshot(TeardownAttempt(transport, client, installed));
            if (IsGenerationCurrent(generation))
            {
                SetState(ConnectionState.Disconnected, "连接已取消。");
            }
            return false;
        }
        catch (BluetoothUnavailableException exception)
        {
            PublishSnapshot(TeardownAttempt(transport, client, installed));
            if (IsGenerationCurrent(generation))
            {
                if (AutoReconnect && _targetAddress is not null)
                {
                    EnterBluetoothOff(exception.Message);    // D4：蓝牙关闭不再杀死重连
                }
                else
                {
                    SetState(ConnectionState.Disconnected, exception.Message);
                }
            }
            return false;
        }
        catch (Exception exception)
        {
            PublishSnapshot(TeardownAttempt(transport, client, installed));
            if (IsGenerationCurrent(generation))
            {
                _reconnectFailures++;
                bool accessDenied = IsAccessDenied(exception);
                string status = accessDenied
                    ? $"连接失败（系统拒绝访问）：{exception.Message}"
                    : $"连接失败：{exception.Message}";
                lock (_gate)
                {
                    // AccessDenied 标记覆盖式更新：非 AccessDenied 失败恢复正常退避
                    _lastFailureWasAccessDenied = accessDenied;
                }
                if (AutoReconnect && _targetAddress is not null)
                {
                    SetState(ConnectionState.Reconnecting, status);
                    ScheduleReconnect();
                }
                else
                {
                    SetState(ConnectionState.Disconnected, status);
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 判断异常链中是否为 Windows「拒绝访问」类失败（AccessDenied）：
    /// 真机实测刚断开的 BLE 链路立即重连时 WinRT 抛拒绝访问（UnauthorizedAccessException
    /// 或消息含 denied / 拒绝访问，随系统语言与 API 路径变化）——按环境冷却处理而非设备故障退避。
    /// 纯函数，诊断与测试可复用。
    /// </summary>
    public static bool IsAccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }
            if (current.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("拒绝访问", StringComparison.Ordinal)
                || current.Message.Contains("访问被拒绝", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsGenerationCurrent(int generation)
    {
        lock (_gate)
        {
            return generation == _generation && !_disposed;
        }
    }

    /// <summary>
    /// 连接尝试结束后的资源清理：若该尝试已安装为当前会话则拆除会话（含数据失效，
    /// 状态转换由调用方决定），否则仅释放本地实例。返回需要发布的失效快照（null=无变化）。
    /// </summary>
    private DeviceData? TeardownAttempt(IBleTransport transport, H3Client client, bool installed)
    {
        SessionTeardown teardown;
        lock (_gate)
        {
            teardown = installed && ReferenceEquals(_transport, transport)
                ? TeardownSessionLocked(ConnectionState.Disconnected, string.Empty, applyState: false)
                : default;
        }
        DisposeSession((teardown.Transport ?? transport, teardown.Client ?? client));
        return teardown.Snapshot;
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
            catch (Exception exception)
            {
                // 电量刷新失败不主动断开：真断线由 transport.Disconnected 统一处理
                LogDiagnostic($"电量轮询失败：{exception.Message}");
            }
        }
    }

    private Task<bool> RefreshBatteryAsync(CancellationToken cancellationToken)
    {
        if (CurrentClient() is null)
        {
            return Task.FromResult(false);
        }
        UpdateDeviceDataWhen(
            data => data.BatteryValidity == DataValidity.Unknown,
            data => data with { BatteryValidity = DataValidity.Loading });
        return WithCommandGateAsync("电量读取", async (client, token) =>
        {
            try
            {
                int? percent = await client.GetBatteryPercentAsync(CommandTimeout, token).ConfigureAwait(false);
                if (percent is int value)
                {
                    SetBattery(value, DataValidity.Available);
                    CompleteCommand(true);
                    return true;
                }
                MarkBatteryError();
                CompleteCommand(false);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDiagnostic($"电量读取失败：{exception.Message}");
                MarkBatteryError();
                CompleteCommand(false);
                return false;
            }
        }, cancellationToken);
    }

    private Task<bool> RefreshNoiseModeAsync(CancellationToken cancellationToken)
    {
        if (CurrentClient() is null)
        {
            return Task.FromResult(false);
        }
        UpdateDeviceDataWhen(
            data => data.NoiseModeValidity == DataValidity.Unknown,
            data => data with { NoiseModeValidity = DataValidity.Loading });
        return WithCommandGateAsync("降噪模式读取", async (client, token) =>
        {
            try
            {
                AncModeInfo? info = await client.GetAncModeInfoAsync(CommandTimeout, token).ConfigureAwait(false);
                if (info is not null)
                {
                    UpdateDeviceData(data => data with
                    {
                        NoiseMode = AncMode.ToIndex(info),
                        NoiseModeInfo = info,
                        NoiseModeValidity = DataValidity.Available,
                        NoiseModeUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                    CompleteCommand(true);
                    return true;
                }
                MarkNoiseModeError();
                CompleteCommand(false);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDiagnostic($"降噪模式读取失败：{exception.Message}");
                MarkNoiseModeError();
                CompleteCommand(false);
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// EQ 读取：v1（cmd 0x20）优先——本机固件对 v2 读回空载荷；v1 为空时回退 v2（旧固件兼容，
    /// 语义零改动；v2 数据 UI 不消费 → validity 标 Unsupported，见 plan.md D11）。
    /// </summary>
    private Task<bool> RefreshEqAsync(CancellationToken cancellationToken)
    {
        if (CurrentClient() is null)
        {
            return Task.FromResult(false);
        }
        UpdateDeviceDataWhen(
            data => data.EqValidity == DataValidity.Unknown,
            data => data with { EqValidity = DataValidity.Loading });
        return WithCommandGateAsync("EQ 读取", async (client, token) =>
        {
            try
            {
                EqV1Info? v1 = await client.GetEqV1Async(CommandTimeout, token).ConfigureAwait(false);
                if (v1 is not null)
                {
                    UpdateDeviceData(data => data with
                    {
                        EqV1 = v1,
                        EqValidity = DataValidity.Available,
                        EqUpdatedUtc = DateTimeOffset.UtcNow,
                    });
                    CompleteCommand(true);
                    return true;
                }
                EqInfo? v2 = await client.GetEqInfoAsync(CommandTimeout, token).ConfigureAwait(false);
                if (v2 is not null)
                {
                    UpdateDeviceData(data => data with
                    {
                        EqPresetId = v2.PresetId,
                        EqOffsets = v2.Offsets,
                        EqValidity = DataValidity.Unsupported,
                    });
                    CompleteCommand(true);
                    return true;
                }
                MarkEqError();
                CompleteCommand(false);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDiagnostic($"EQ 读取失败：{exception.Message}");
                MarkEqError();
                CompleteCommand(false);
                return false;
            }
        }, cancellationToken);
    }

    private Task<bool> RefreshGameModeAsync(CancellationToken cancellationToken)
    {
        if (CurrentClient() is null)
        {
            return Task.FromResult(false);
        }
        UpdateDeviceDataWhen(
            data => data.GameModeValidity == DataValidity.Unknown,
            data => data with { GameModeValidity = DataValidity.Loading });
        return WithCommandGateAsync("游戏模式读取", async (client, token) =>
        {
            try
            {
                int outcome = -1;   // -1 无响应；0 设备有应答但值未知；1 有效值
                var packet = await client
                    .GetAdvInfoAsync(1u << AdvType.WorkMode, CommandTimeout, token)
                    .ConfigureAwait(false);
                if (packet is not null)
                {
                    outcome = 0;
                    foreach (var entry in TlvParser.Parse(packet.Params))
                    {
                        if (entry.Type == AdvType.WorkMode && entry.Value.Length >= 1)
                        {
                            outcome = ApplyWorkModeValue(entry.Value[0]) ? 1 : 0;
                            break;
                        }
                    }
                }
                if (outcome < 0)
                {
                    MarkGameModeError();
                }
                CompleteCommand(outcome >= 0);   // 设备有应答即命令成功（值未知不算失败）
                return outcome > 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDiagnostic($"游戏模式读取失败：{exception.Message}");
                MarkGameModeError();
                CompleteCommand(false);
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 解析 WorkMode 字节并写入快照：0x02=开 / 0x01=关（Available）；
    /// 其它值 = 协议未知值 → Unknown（值清空，绝不显示为关）。返回是否得到有效值。
    /// </summary>
    private bool ApplyWorkModeValue(byte value)
    {
        if (value is 0x01 or 0x02)
        {
            bool enabled = value == 0x02;
            UpdateDeviceData(data => data with
            {
                GameMode = enabled,
                GameModeValidity = DataValidity.Available,
                GameModeUpdatedUtc = DateTimeOffset.UtcNow,
            });
            return true;
        }
        UpdateDeviceData(data => data with
        {
            GameMode = null,
            GameModeValidity = DataValidity.Unknown,
            GameModeUpdatedUtc = null,
        });
        return false;
    }

    /// <summary>
    /// 耳机主动推送（未匹配 pending sn 的通知帧）：
    /// ADV 推送里的电量/游戏模式 TLV、CUSTOM 17 03… 降噪模式负载。
    /// 推送只更新数据与有效性，不参与命令健康记账。
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
                    SetBattery(entry.Value[0], DataValidity.Available);
                }
                else if (entry.Type == AdvType.WorkMode && entry.Value.Length >= 1)
                {
                    ApplyWorkModeValue(entry.Value[0]);
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
                UpdateDeviceData(data => data with
                {
                    NoiseMode = AncMode.ToIndex(info),
                    NoiseModeInfo = info,
                    NoiseModeValidity = DataValidity.Available,
                    NoiseModeUpdatedUtc = DateTimeOffset.UtcNow,
                });
            }
        }
    }

    private void SetBattery(int percent, DataValidity validity)
    {
        UpdateDeviceData(data => data with
        {
            BatteryPercent = percent,
            BatteryValidity = validity,
            BatteryUpdatedUtc = DateTimeOffset.UtcNow,
        });
        RaiseBattery(percent);
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

    /// <summary>
    /// 自动关机读取（连接初始化 / RefreshAll 通道）：读失败只标 Error 并计命令健康，
    /// 不上抛（与其它初始化读同语义）。按键映射表不在初始化读之列——40 条整表由
    /// 按键编辑器按需读取（<see cref="GetKeyMappingsAsync"/>）。
    /// </summary>
    private async Task<bool> RefreshPowerManagerAsync(CancellationToken cancellationToken)
    {
        if (CurrentClient() is null)
        {
            return false;
        }
        UpdateDeviceDataWhen(
            data => data.AutoOffValidity == DataValidity.Unknown,
            data => data with { AutoOffValidity = DataValidity.Loading });
        return await GetPowerManagerAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>连接同步开始：所有 Unknown（从未读到）字段一次性转 Loading（单个快照事件）。</summary>
    private void MarkSyncStart()
    {
        UpdateDeviceData(data => data with
        {
            BatteryValidity = data.BatteryValidity == DataValidity.Unknown ? DataValidity.Loading : data.BatteryValidity,
            NoiseModeValidity = data.NoiseModeValidity == DataValidity.Unknown ? DataValidity.Loading : data.NoiseModeValidity,
            EqValidity = data.EqValidity == DataValidity.Unknown ? DataValidity.Loading : data.EqValidity,
            GameModeValidity = data.GameModeValidity == DataValidity.Unknown ? DataValidity.Loading : data.GameModeValidity,
            AutoOffValidity = data.AutoOffValidity == DataValidity.Unknown ? DataValidity.Loading : data.AutoOffValidity,
        });
    }

    private void MarkBatteryError()
        => UpdateDeviceData(data => data with { BatteryValidity = DataValidity.Error });

    private void MarkNoiseModeError()
        => UpdateDeviceData(data => data with { NoiseModeValidity = DataValidity.Error });

    private void MarkEqError()
        => UpdateDeviceData(data => data with { EqValidity = DataValidity.Error });

    private void MarkGameModeError()
        => UpdateDeviceData(data => data with { GameModeValidity = DataValidity.Error });

    private void MarkAutoOffError()
        => UpdateDeviceData(data => data with { AutoOffValidity = DataValidity.Error });

    private void MarkKeyMapError()
        => UpdateDeviceData(data => data with { KeyMapValidity = DataValidity.Error });

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

    /// <summary>条件快照更新（谓词不满足时不发布事件）。</summary>
    private void UpdateDeviceDataWhen(Func<DeviceData, bool> predicate, Func<DeviceData, DeviceData> update)
    {
        Action<DeviceData>? handler;
        DeviceData snapshot;
        lock (_gate)
        {
            if (!predicate(_deviceData))
            {
                return;
            }
            _deviceData = update(_deviceData);
            snapshot = _deviceData;
            handler = DeviceDataChanged;
        }
        handler?.Invoke(snapshot);
    }

    private void PublishSnapshot(DeviceData? snapshot)
    {
        if (snapshot is not null)
        {
            DeviceDataChanged?.Invoke(snapshot);
        }
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
                || IsSessionState(_state) || _state == ConnectionState.BluetoothOff)
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

    /// <summary>重连等待的种类（决定 Reconnecting 状态文案）。</summary>
    private enum ReconnectDelayKind
    {
        /// <summary>常规退避序列（连接失败重试）。</summary>
        Backoff,

        /// <summary>AccessDenied 类失败的专用冷却（Windows 拒绝访问，环境性等待）。</summary>
        AccessDenied,

        /// <summary>计划内断开后的首次重连冷却（<see cref="PostDisconnectReconnectDelay"/> 补足）。</summary>
        PostDisconnect,
    }

    /// <summary>
    /// 计算下一次重连等待与文案种类（调用方持有 _gate）：
    /// 最近一次失败为 AccessDenied → 专用冷却（<see cref="AccessDeniedReconnectDelay"/>，保持标记）；
    /// 失败计数 0 且存在会话结束史 → 补足计划内断开冷却（<see cref="PostDisconnectReconnectDelay"/>）；
    /// 其余 → 常规退避序列（按 <see cref="_reconnectFailures"/> 递增封顶）。
    /// </summary>
    private (TimeSpan Delay, ReconnectDelayKind Kind) ComputeReconnectDelayLocked()
    {
        if (_lastFailureWasAccessDenied)
        {
            return (AccessDeniedReconnectDelay, ReconnectDelayKind.AccessDenied);
        }
        if (_reconnectFailures == 0)
        {
            if (_sessionEndedUtc is DateTimeOffset ended)
            {
                TimeSpan sinceEnd = DateTimeOffset.UtcNow - ended;
                return sinceEnd < PostDisconnectReconnectDelay
                    ? (PostDisconnectReconnectDelay - sinceEnd, ReconnectDelayKind.PostDisconnect)
                    : (TimeSpan.Zero, ReconnectDelayKind.PostDisconnect);
            }
            return (TimeSpan.Zero, ReconnectDelayKind.Backoff);
        }
        return (
            ReconnectBackoff[Math.Min(_reconnectFailures - 1, ReconnectBackoff.Length - 1)],
            ReconnectDelayKind.Backoff);
    }

    /// <summary>
    /// 重连循环：按退避等待（Reconnecting）→ 搜索目标广播（Scanning，超时转 Unreachable 继续
    /// 监听）→ 看到即连接；失败继续循环（退避随 <see cref="_reconnectFailures"/> 递增到封顶）。
    /// 蓝牙不可用 → 进入 <see cref="ConnectionState.BluetoothOff"/> 等待 radio 恢复（D4，
    /// 不再让循环死亡）；radio 恢复后由 <see cref="OnRadioAvailabilityChanged"/> 重新拉起本循环。
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
                ReconnectDelayKind delayKind;
                lock (_gate)
                {
                    if (_disposed || !AutoReconnect || _targetAddress is null
                        || IsSessionState(_state) || _state == ConnectionState.BluetoothOff)
                    {
                        return;
                    }
                    target = _targetAddress.Value;
                    name = _targetName;
                    (delay, delayKind) = ComputeReconnectDelayLocked();
                }

                if (delay > TimeSpan.Zero)
                {
                    string reason = delayKind switch
                    {
                        ReconnectDelayKind.AccessDenied => "系统拒绝访问（Windows 对刚断开的链路有冷却），",
                        ReconnectDelayKind.PostDisconnect => "连接断开，",
                        _ => "连接失败，",
                    };
                    SetState(ConnectionState.Reconnecting, $"{reason}{delay.TotalSeconds:0} 秒后自动重连…");
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
                    if (_disposed || IsSessionState(_state) || _state == ConnectionState.BluetoothOff)
                    {
                        return;
                    }
                    searching = name.Length > 0 ? name : BleScanResult.FormatAddress(target);
                }

                // 先挂信号源再开扫描：任何时刻到达的目标广播都不会丢
                var signal = new TaskCompletionSource<BleScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    if (_disposed || IsSessionState(_state) || _state == ConnectionState.BluetoothOff)
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
                    lock (_gate)
                    {
                        _advertisementSignal = null;
                    }
                    EnterBluetoothOff(exception.Message);
                    return;   // radio 恢复后自动重新拉起循环
                }
                catch (Exception exception)
                {
                    LogDiagnostic($"启动广播扫描失败：{exception.Message}");
                    lock (_gate)
                    {
                        _advertisementSignal = null;
                        _reconnectFailures++;   // 计入退避，避免持续异常时零延迟热循环
                    }
                    continue;   // 下一轮循环按退避等待并刷新状态文本
                }

                SetState(ConnectionState.Scanning, $"正在搜索 {searching}…");

                BleScanResult? seen = await WaitForTargetAdvertisementAsync(signal, cancellationToken)
                    .ConfigureAwait(false);
                if (seen is null)
                {
                    return;   // 已取消
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

    /// <summary>
    /// 等待目标广播：<see cref="UnreachableAfter"/> 内出现 → 返回；超时 → 宣告一次
    /// <see cref="ConnectionState.Unreachable"/>（持续监听语义，不退出循环）；取消 → null。
    /// </summary>
    private async Task<BleScanResult?> WaitForTargetAdvertisementAsync(
        TaskCompletionSource<BleScanResult> signal, CancellationToken cancellationToken)
    {
        DateTimeOffset searchStart = DateTimeOffset.UtcNow;
        bool unreachableAnnounced = false;
        while (true)
        {
            if (signal.Task.IsCompleted)
            {
                return signal.Task.Result;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            TimeSpan wait = UnreachableAfter - (DateTimeOffset.UtcNow - searchStart);
            if (wait <= TimeSpan.Zero)
            {
                if (!unreachableAnnounced)
                {
                    unreachableAnnounced = true;
                    SetState(
                        ConnectionState.Unreachable,
                        "耳机不可达：可能已关机、入盒或超出范围，将持续监听广播…");
                }
                try
                {
                    return await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            await Task.WhenAny(signal.Task, Task.Delay(wait, cancellationToken)).ConfigureAwait(false);
        }
    }

    // ───────────────────────── 蓝牙 radio 监视（D4） ─────────────────────────

    /// <summary>
    /// 进入蓝牙不可用状态：停重连循环（环境故障不计退避，恢复后立即重试）、
    /// 启动 radio 监视（WinRT StateChanged；API 不可用时实现内部退化为 ≥10s 定时探测）。
    /// radio 恢复 → <see cref="OnRadioAvailabilityChanged"/> 自动回到重连流程。
    /// </summary>
    private void EnterBluetoothOff(string reason)
    {
        bool stateChanged;
        lock (_gate)
        {
            if (_disposed || _state == ConnectionState.BluetoothOff)
            {
                return;
            }
            CancelReconnectLoopLocked();
            _reconnectFailures = 0;
            stateChanged = ApplyTransitionLocked(
                ConnectionState.BluetoothOff, $"{reason}打开蓝牙后将自动恢复。");
            if (_radioMonitor is null)
            {
                _radioMonitor = new WindowsRadioMonitor();
                _radioMonitor.AvailabilityChanged += OnRadioAvailabilityChanged;
            }
            // 注入的监视已在构造时订阅；这里只负责启动（幂等）
            try
            {
                _radioMonitor.Start();
            }
            catch (Exception exception)
            {
                LogDiagnostic($"蓝牙 radio 监视启动失败：{exception.Message}");
            }
        }
        if (stateChanged)
        {
            FireTransition(ConnectionState.BluetoothOff, CurrentStatus, stateChanged: true);
        }
    }

    private void OnRadioAvailabilityChanged(bool available)
    {
        if (available)
        {
            bool recover;
            lock (_gate)
            {
                recover = !_disposed && _state == ConnectionState.BluetoothOff;
            }
            if (!recover)
            {
                return;
            }
            LogDiagnostic("蓝牙已恢复，自动重新连接。");
            SetState(ConnectionState.Reconnecting, "蓝牙已恢复，正在重新连接…");
            ScheduleReconnect();
            return;
        }

        bool enter;
        lock (_gate)
        {
            // 已连接时蓝牙关闭由链路断开事件上报（数据失效 + 重连路径）；
            // 这里只处理"等待/空闲"状态下 radio 消失的情形。
            enter = !_disposed
                && _state is ConnectionState.Disconnected or ConnectionState.Scanning
                    or ConnectionState.Reconnecting or ConnectionState.Unreachable;
        }
        if (enter)
        {
            EnterBluetoothOff("蓝牙已关闭或不可用。");
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

    /// <summary>当前会话的传输层意外断开（GATT 会话丢失）：拆会话 → 数据全部失效 → 自动重连。</summary>
    private void OnTransportDisconnected(IBleTransport transport)
    {
        SessionTeardown teardown;
        ConnectionState finalState;
        lock (_gate)
        {
            if (_transport != transport)
            {
                return;   // 陈旧尝试的断线通知，当前会话不是它
            }
            _generation++;
            _sessionEndedUtc = DateTimeOffset.UtcNow;   // 计划内断开冷却起点（首次重连 ≥ PostDisconnectReconnectDelay）
            finalState = AutoReconnect && _targetAddress is not null
                ? ConnectionState.Reconnecting
                : ConnectionState.Disconnected;
            teardown = TeardownSessionLocked(finalState, "连接已断开。");
        }
        DisposeSession((teardown.Transport, teardown.Client));
        FireTransition(finalState, "连接已断开。", teardown.StateChanged);
        PublishSnapshot(teardown.Snapshot);
        if (finalState == ConnectionState.Reconnecting)
        {
            ScheduleReconnect();
        }
    }

    // ───────────────────────── 状态与清理 ─────────────────────────

    private H3Client? CurrentClient()
    {
        lock (_gate)
        {
            return _client;
        }
    }

    private void LogDiagnostic(string message) => Diagnostics?.Invoke(message);

    private void SetState(ConnectionState state, string status)
    {
        bool stateChanged;
        lock (_gate)
        {
            stateChanged = ApplyTransitionLocked(state, status);
        }
        FireTransition(state, status, stateChanged);
    }

    /// <summary>更新状态字段（调用方持有 _gate），返回状态是否实际变化。非法转换拒绝并上报，不崩溃。</summary>
    private bool ApplyTransitionLocked(ConnectionState state, string status)
    {
        if (_state == state)
        {
            _statusText = status;
            return false;
        }
        if (!IsLegalTransition(_state, state))
        {
            TransitionRejected?.Invoke($"{_state} → {state}（{status}）");
            return false;
        }
        _state = state;
        _statusText = status;
        return true;
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

    private readonly record struct SessionTeardown(
        IBleTransport? Transport,
        H3Client? Client,
        DeviceData? Snapshot,
        bool StateChanged);

    /// <summary>
    /// 拆除当前会话（调用方持有 _gate）：停 15s 电量刷新、清 pending 广播信号、
    /// 设备数据全量转 Stale（单个快照事件）、状态置 <paramref name="finalState"/>
    /// （<paramref name="applyState"/>=false 时不改状态——连接失败路径由调用方决定最终状态）。
    /// </summary>
    private SessionTeardown TeardownSessionLocked(ConnectionState finalState, string status, bool applyState = true)
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

        DeviceData stale = _deviceData.MarkAllStale();
        DeviceData? snapshot = null;
        if (stale != _deviceData)
        {
            _deviceData = stale;
            snapshot = stale;
        }

        bool stateChanged = applyState && ApplyTransitionLocked(finalState, status);
        return new SessionTeardown(transport, client, snapshot, stateChanged);
    }

    private static void DisposeSession((IBleTransport? Transport, H3Client? Client) session)
    {
        session.Client?.Dispose();
        session.Transport?.Dispose();
    }
}
