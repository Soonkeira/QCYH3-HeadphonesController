using System.Windows;
using System.Windows.Threading;
using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Config;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// UI 层共享状态（属性只在 UI 线程读写）：持有 <see cref="ConnectionManager"/> 与
/// <see cref="AppConfig"/>，把管理器的工作线程事件 marshal 到 Dispatcher；
/// 写命令统一走 <see cref="RunWriteCommandAsync"/>（失败自动重试一次，
/// 连续 2 次失败触发健康告警/横幅，任一次成功即清除）。
/// </summary>
internal sealed class AppState : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private int _consecutiveFailures;

    public ConnectionManager Manager { get; }
    public AppConfig Config { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusText { get; private set; } = "未连接";
    public int? BatteryPercent { get; private set; }
    public DeviceData Data { get; private set; } = new();

    public bool IsConnected => State == ConnectionState.Connected;
    public bool HasTarget => Config.TargetAddress is not null;

    /// <summary>连续 2 次写命令失败 → true（主面板显示"耳机无响应"横幅）。</summary>
    public bool Degraded => Volatile.Read(ref _consecutiveFailures) >= 2;

    /// <summary>头部状态短标签（已连接=绿，连接中…/重连中…/未连接=灰）。</summary>
    public string StateLabel => State switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Connecting => "连接中…",
        ConnectionState.Scanning => "重连中…",
        _ => "未连接",
    };

    /// <summary>状态 / 状态文本 / 电量 / 设备数据任一更新（UI 线程触发）。</summary>
    public event Action? StateUpdated;

    /// <summary>命令健康变化：true=进入"耳机无响应"，false=恢复正常。</summary>
    public event Action<bool>? CommandHealthChanged;

    /// <summary>开机自启开关变化（UI 线程触发）。</summary>
    public event Action? AutostartChanged;

    public bool IsAutostartEnabled => AutostartManager.IsEnabled();

    public AppState()
    {
        _dispatcher = Application.Current.Dispatcher;
        Config = AppConfig.Load();
        Manager = new ConnectionManager();
        Manager.StateChanged += OnManagerStateChanged;
        Manager.StatusText += OnManagerStatusText;
        Manager.BatteryChanged += OnManagerBatteryChanged;
        Manager.DeviceDataChanged += OnManagerDeviceDataChanged;
    }

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

    /// <summary>全量刷新（电量/降噪/EQ/游戏模式）；未连接时为安全空操作。</summary>
    public Task RefreshAllAsync() => Manager.RefreshAllAsync();

    /// <summary>
    /// 写命令统一入口：<paramref name="command"/> 返回 false = 耳机无响应/异常，
    /// 自动重试一次；两次都失败计入连续失败（达 2 次触发
    /// <see cref="CommandHealthChanged"/>(true)）；任一次成功即清零并解除横幅。
    /// </summary>
    public async Task<bool> RunWriteCommandAsync(string name, Func<Task<bool>> command)
    {
        if (await TryCommand(name, command).ConfigureAwait(false)
            || await TryCommand(name, command).ConfigureAwait(false))
        {
            ResetHealth();
            return true;
        }
        RecordFailure(name);
        return false;
    }

    private async Task<bool> TryCommand(string name, Func<Task<bool>> command)
    {
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

    private void ResetHealth()
    {
        if (Interlocked.Exchange(ref _consecutiveFailures, 0) >= 2)
        {
            BeginInvoke(() => CommandHealthChanged?.Invoke(false));
        }
    }

    private void RecordFailure(string name)
    {
        if (Interlocked.Increment(ref _consecutiveFailures) == 2)
        {
            ErrorLog.Write($"耳机无响应：写命令连续 2 次失败（最后一条：{name}）。");
            BeginInvoke(() => CommandHealthChanged?.Invoke(true));
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
        Manager.BatteryChanged -= OnManagerBatteryChanged;
        Manager.DeviceDataChanged -= OnManagerDeviceDataChanged;
        Manager.Dispose();
    }

    // ───────────────────────── 管理器事件 → UI 线程 ─────────────────────────

    private void OnManagerStateChanged(ConnectionState state)
        => BeginInvoke(() =>
        {
            State = state;
            StateUpdated?.Invoke();
        });

    private void OnManagerStatusText(string text)
        => BeginInvoke(() =>
        {
            StatusText = text;
            StateUpdated?.Invoke();
        });

    private void OnManagerBatteryChanged(int percent)
        => BeginInvoke(() =>
        {
            BatteryPercent = percent;
            StateUpdated?.Invoke();
        });

    private void OnManagerDeviceDataChanged(DeviceData data)
        => BeginInvoke(() =>
        {
            Data = data;
            StateUpdated?.Invoke();
        });

    private void BeginInvoke(Action action) => _dispatcher.BeginInvoke(action);
}
