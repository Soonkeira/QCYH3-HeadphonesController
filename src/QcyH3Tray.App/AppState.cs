using System.Windows;
using System.Windows.Threading;
using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Config;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// UI 层共享状态（属性只在 UI 线程读写）：持有 <see cref="ConnectionManager"/> 与
/// <see cref="AppConfig"/>，把管理器的工作线程事件 marshal 到 Dispatcher。
/// <para>
/// 阶段 2 收敛（plan.md §1.2）：
/// - 命令健康完全下沉 Core（<see cref="ConnectionManager"/> 的 Degraded 相是唯一权威），
///   本类不再持有失败计数（D1/D10 根治：横幅随状态机进入/离开 Degraded 出现/消失）；
/// - <see cref="RunWriteCommandAsync"/> 在未连接/连接中直接短路返回失败（中文原因走状态行），
///   不发起命令、不计失败、不触发横幅；
/// - 设备数据（含电量与 validity）整体来自 <see cref="Data"/> 快照；
/// - <see cref="StateUpdated"/> 是唯一总事件，所有属性可随时查询（阶段 3 双形态 UI 的订阅口子）。
/// </para>
/// </summary>
internal sealed class AppState : IDisposable
{
    private readonly Dispatcher _dispatcher;

    public ConnectionManager Manager { get; }
    public AppConfig Config { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusText { get; private set; } = "未连接";
    public DeviceData Data { get; private set; } = new();

    /// <summary>电量（快照字段；UI 须结合 <see cref="BatteryValidity"/> 决定展示方式）。</summary>
    public int? BatteryPercent => Data.BatteryPercent;

    /// <summary>电量有效性：Unknown/Error → 显示 "--"；Stale → 旧值置灰；Available → 现值。</summary>
    public DataValidity BatteryValidity => Data.BatteryValidity;

    /// <summary>自动关机配置时长（分钟，0xFFFF=不自动关机）；null=未知（阶段 4b 功能页）。</summary>
    public int? AutoOffMinutes => Data.AutoOffMinutes;

    /// <summary>自动关机有效性：≠Available 时功能页选择禁用。</summary>
    public DataValidity AutoOffValidity => Data.AutoOffValidity;

    /// <summary>按键映射原始整表（含出厂槽 action≥0x81）；null=未知（阶段 4b 按键编辑器）。</summary>
    public IReadOnlyList<KeyMappingEntry>? KeyMappings => Data.KeyMap;

    /// <summary>按键映射表有效性。</summary>
    public DataValidity KeyMapValidity => Data.KeyMapValidity;

    /// <summary>链路已建立（连接中/同步中/已连接/响应异常）——控制区可用的判据（UI 线程投影）。</summary>
    public bool IsLinkEstablished => IsSessionState(State);

    /// <summary>链路已建立且同步完成（Connected/Degraded）——旧语义的"已连接"。</summary>
    public bool IsConnected => State is ConnectionState.Connected or ConnectionState.Degraded;

    public bool HasTarget => Config.TargetAddress is not null;

    /// <summary>命令健康降级（Core 状态机 Degraded 相）：主面板显示"耳机无响应，正在重试"横幅。</summary>
    public bool Degraded => State == ConnectionState.Degraded;

    /// <summary>头部状态短标签（每相一个标准文案）。</summary>
    public string StateLabel => State switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Degraded => "响应异常",
        ConnectionState.Syncing => "同步中…",
        ConnectionState.Connecting => "连接中…",
        ConnectionState.Reconnecting or ConnectionState.Scanning => "重连中…",
        ConnectionState.Unreachable => "耳机不可达",
        ConnectionState.BluetoothOff => "蓝牙不可用",
        _ => "未连接",
    };

    /// <summary>状态 / 状态文本 / 设备数据快照任一更新（UI 线程触发；唯一总事件）。</summary>
    public event Action? StateUpdated;

    /// <summary>开机自启开关变化（UI 线程触发）。</summary>
    public event Action? AutostartChanged;

    /// <summary>
    /// 低电量提醒触发（UI 线程触发；参数=当前电量百分比）。判定见 <see cref="LowBatteryAdvisor"/>，
    /// 通知开关与展示由订阅方（TrayHost → <see cref="INotificationService"/>）决定。
    /// </summary>
    public event Action<int>? LowBatteryAlert;

    public bool IsAutostartEnabled => AutostartManager.IsEnabled();

    /// <summary>低电量提醒判定相位（同一连接会话内不重复触发；<see cref="LowBatteryAdvisor"/>）。</summary>
    private LowBatteryPhase _lowBatteryPhase = LowBatteryPhase.Armed;

    public AppState()
    {
        _dispatcher = Application.Current.Dispatcher;
        Config = AppConfig.Load();
        Manager = new ConnectionManager();
        Manager.StateChanged += OnManagerStateChanged;
        Manager.StatusText += OnManagerStatusText;
        Manager.DeviceDataChanged += OnManagerDeviceDataChanged;
        Manager.Diagnostics += message => ErrorLog.Write(message);
        Manager.TransitionRejected += message => ErrorLog.Write($"非法状态转换被拒绝：{message}");
    }

    private static bool IsSessionState(ConnectionState state)
        => state is ConnectionState.Connecting or ConnectionState.Syncing
            or ConnectionState.Connected or ConnectionState.Degraded;

    /// <summary>设置目标设备并持久化到配置（首启选择设备后调用）。</summary>
    public void SetTarget(ulong address, string name)
    {
        Config.TargetAddress = address;
        Config.TargetName = name;
        Config.Save();
        Manager.SetTarget(address, name);
    }

    /// <summary>应用配置中的目标设备到连接管理器（启动时调用）。</summary>
    public void ApplyTargetFromConfig()
    {
        if (Config.TargetAddress is ulong address)
        {
            Manager.SetTarget(address, Config.TargetName);
        }
    }

    /// <summary>发起连接（失败自动进入重连循环）；异常只记日志不上抛。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Manager.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("连接失败", exception);
        }
    }

    /// <summary>读一次 EQ v1（预设写入前保留 eqType/masterGain、EQ 编辑器读自耳机）。</summary>
    public Task<EqV1Info?> GetEqV1Async() => Manager.GetEqV1Async();

    /// <summary>读一次自动关机配置（阶段 4b 功能页；未连接返回 null）。</summary>
    public Task<PowerManagerInfo?> GetPowerManagerAsync() => Manager.GetPowerManagerAsync();

    /// <summary>写入自动关机时长（分钟；Core 写后回读确认 + 乐观快照）。</summary>
    public Task<PowerManagerInfo?> SetPowerManagerAsync(int minutes) => Manager.SetPowerManagerAsync(minutes);

    /// <summary>读一次按键映射整表（按键编辑器；未连接返回 null）。</summary>
    public Task<List<KeyMappingEntry>?> GetKeyMappingsAsync() => Manager.GetKeyMappingsAsync();

    /// <summary>整表写入按键映射（Core 写后回读确认 + 乐观快照；调用方必须传整表）。</summary>
    public Task<IReadOnlyList<KeyMappingEntry>?> SetKeyMappingsAsync(IReadOnlyList<KeyMappingEntry> entries)
        => Manager.SetKeyMappingsAsync(entries);

    /// <summary>播放提示音（R2 级外放音效；调用方须先经 UI 确认弹窗）。</summary>
    public Task<bool> PlayToneAsync(byte toneId) => Manager.PlayToneAsync(toneId);

    /// <summary>查找耳机响铃（R2 级外放音效；调用方须先经 UI 确认弹窗）。</summary>
    public Task<bool> FindEarbudsAsync() => Manager.FindEarbudsAsync();

    /// <summary>全量刷新（电量/降噪/EQ/游戏模式）；未连接时为安全空操作。</summary>
    public Task RefreshAllAsync() => Manager.RefreshAllAsync();

    /// <summary>
    /// 写命令统一入口：
    /// - 未连接/连接中：直接短路返回 false（不计命令失败、不触发横幅——D1 触发面根治），
    ///   失败原因由状态行文案承载（如"未连接"/"连接中…"）；
    /// - 链路建立后：执行一次命令；成败记账在 Core（<see cref="ConnectionManager.CompleteCommand"/>
    ///   语义），连续 2 次失败 → Degraded → 横幅，任一成功 → 恢复 → 横幅消失。
    /// </summary>
    public async Task<bool> RunWriteCommandAsync(string name, Func<Task<bool>> command)
    {
        if (!IsLinkEstablished)
        {
            ErrorLog.Write($"写命令「{name}」跳过：耳机未连接（{StateLabel}），不计入命令失败。");
            return false;
        }
        try
        {
            return await command().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ErrorLog.Write($"写命令「{name}」异常", exception);
            return false;
        }
    }

    /// <summary>开机自启开关：写注册表 + 同步配置字段 + 广播变化。</summary>
    public void SetAutostart(bool enabled)
    {
        AutostartManager.SetEnabled(enabled);
        Config.AutoStart = enabled;
        Config.Save();
        AutostartChanged?.Invoke();
    }

    public void Dispose()
    {
        Manager.StateChanged -= OnManagerStateChanged;
        Manager.StatusText -= OnManagerStatusText;
        Manager.DeviceDataChanged -= OnManagerDeviceDataChanged;
        Manager.Dispose();
    }

    // ───────────────────────── 管理器事件 → UI 线程 ─────────────────────────

    private void OnManagerStateChanged(ConnectionState state)
        => BeginInvoke(() =>
        {
            State = state;
            RecordFirstConnected(state);
            EvaluateLowBattery();
            StateUpdated?.Invoke();
        });

    /// <summary>首次连接成功记入配置（阶段 3b 控制中心"连接"页展示；只记第一次，之后零开销）。</summary>
    private void RecordFirstConnected(ConnectionState state)
    {
        if (state is ConnectionState.Connected or ConnectionState.Degraded && Config.FirstConnectedAt is null)
        {
            Config.FirstConnectedAt = DateTimeOffset.Now;
            Config.Save();
        }
    }

    private void OnManagerStatusText(string text)
        => BeginInvoke(() =>
        {
            StatusText = text;
            StateUpdated?.Invoke();
        });

    private void OnManagerDeviceDataChanged(DeviceData data)
        => BeginInvoke(() =>
        {
            Data = data;
            EvaluateLowBattery();
            StateUpdated?.Invoke();
        });

    /// <summary>
    /// 低电量判定（阶段 6）：状态与电量快照每次更新都判定一次——会话边界（链路断开）重置相位、
    /// 阈值/迟滞/会话去重在 <see cref="LowBatteryAdvisor.Evaluate"/> 纯函数内；
    /// 通知总开关关闭时照常推进相位（开关重新打开后不会对已提醒过的电量重复轰炸），
    /// 只抑制事件。
    /// </summary>
    private void EvaluateLowBattery()
    {
        try
        {
            (bool notify, LowBatteryPhase phase) = LowBatteryAdvisor.Evaluate(
                _lowBatteryPhase,
                LowBatteryAdvisor.ClampThreshold(Config.LowBatteryThresholdPercent),
                Data.BatteryValidity,
                Data.BatteryPercent,
                sessionActive: IsLinkEstablished);
            _lowBatteryPhase = phase;
            if (notify && Config.NotificationsEnabled && Data.BatteryPercent is int percent)
            {
                LowBatteryAlert?.Invoke(percent);
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("低电量提醒判定失败", exception);
        }
    }

    private void BeginInvoke(Action action) => _dispatcher.BeginInvoke(action);
}
