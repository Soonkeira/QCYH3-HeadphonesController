using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using QcyH3Tray.Core.App;
using Icon = System.Drawing.Icon;

namespace QcyH3Tray.App;

/// <summary>
/// 托盘宿主（H.NotifyIcon.Wpf）：
/// 图标 = 迷你耳机剪影 + 电量徽章（<see cref="TrayIconRenderer"/>），随电量/连接状态刷新；
/// 左键 = 切换主面板显示/隐藏；右键菜单：打开控制面板 / 打开控制中心 / 立即重连 / 开机自启 / 退出；
/// 气泡通知 = 低电量提醒（阶段 6，<see cref="TrayNotificationService"/>）。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    /// <summary>面板被 Deactivated 自动隐藏后，短时间内忽略托盘左键的"再打开"。</summary>
    private static readonly TimeSpan TrayClickHideGrace = TimeSpan.FromMilliseconds(400);

    private readonly AppState _app;
    private readonly WindowCoordinator _windows;
    private readonly TaskbarIcon _icon;
    private readonly TrayNotificationService _notifications;
    private Icon? _lastIcon;                   // 渲染结果不变时不重复赋值（缓存命中即同实例）
    private MenuItem _autostartItem = null!;
    private ControlPanelWindow? _panel;

    /// <summary>打开控制中心的请求（菜单项 / 快捷面板页脚）：由 App.xaml.cs 订阅统一处理。</summary>
    public event Action? RequestOpenControlCenter;

    /// <summary>托盘气泡通知（阶段 6：低电量提醒等；Win10+ 呈现为 toast）。</summary>
    public INotificationService Notifications => _notifications;

    public TrayHost(AppState app, WindowCoordinator windows)
    {
        _app = app;
        _windows = windows;
        _icon = new TaskbarIcon
        {
            ToolTipText = "QCY H3 — 未连接",
            MenuActivation = PopupActivationMode.RightClick,
            Icon = TrayIconRenderer.GetIcon(null),
        };
        _notifications = new TrayNotificationService(() => _icon.IsCreated ? _icon : null);
        _icon.TrayLeftMouseUp += OnTrayLeftMouseUp;
        _icon.ContextMenu = BuildContextMenu();

        _app.StateUpdated += OnStateChanged;
        _app.AutostartChanged += OnAutostartChanged;
        _app.LowBatteryAlert += OnLowBatteryAlert;
        UpdateVisual();
        CreateTrayIcon();
    }

    /// <summary>
    /// 显式创建托盘图标。纯代码创建的 <see cref="TaskbarIcon"/> 不在视觉树中，
    /// <c>Loaded</c> 永远不触发，H.NotifyIcon 2.3.2 必须手动 <c>ForceCreate()</c>
    /// 才会注册到系统托盘；关闭 Efficiency Mode（默认开启会把进程降优先级，
    /// 影响蓝牙指令响应）。
    /// </summary>
    private void CreateTrayIcon()
    {
        _icon.Visibility = Visibility.Visible;   // 继承自 UIElement，显式确保可见
        try
        {
            _icon.ForceCreate(enablesEfficiencyMode: false);
            if (_icon.IsCreated)
            {
                ErrorLog.Write("托盘图标已创建");
            }
            else
            {
                ErrorLog.Write("托盘图标创建失败: ForceCreate 后 IsCreated=false");
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("托盘图标创建失败", exception);
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var openItem = new MenuItem { Header = "打开控制面板" };
        openItem.Click += (_, _) => TogglePanel();

        var controlCenterItem = new MenuItem { Header = "打开控制中心" };
        controlCenterItem.Click += (_, _) => RequestOpenControlCenter?.Invoke();

        var reconnectItem = new MenuItem { Header = "立即重连" };
        reconnectItem.Click += (_, _) => _ = _app.ConnectAsync();

        _autostartItem = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = _app.IsAutostartEnabled,
        };
        _autostartItem.Click += (_, _) => SetAutostart(_autostartItem.IsChecked);

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(controlCenterItem);
        menu.Items.Add(reconnectItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        return menu;
    }

    /// <summary>左键：主面板可见则隐藏；刚被自动隐藏则保持隐藏；否则唤起。</summary>
    private void OnTrayLeftMouseUp(object sender, RoutedEventArgs eventArgs)
        => TogglePanel();

    private void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
            return;
        }
        if (_panel is not null && DateTime.UtcNow - _panel.HiddenAtUtc < TrayClickHideGrace)
        {
            return;   // Deactivated → Hide 刚发生（同一次托盘点击），不要立刻弹回
        }
        PresentPanel();
    }

    /// <summary>
    /// 外部唤起（第二实例激活信号）：不做切换，总是弹出主面板。仅 UI 线程调用。
    /// 面板被 Alt+F4 等关闭后（IsLoaded=false）重建新实例——对已 Close 的窗口
    /// 调 Show() 会抛 InvalidOperationException 且被全局钩子吞掉，导致托盘左键永久失效（D6）。
    /// </summary>
    public void PresentPanel()
    {
        if (_panel is not { IsLoaded: true })
        {
            var panel = new ControlPanelWindow(_app, _windows);
            panel.RequestOpenControlCenter += () => RequestOpenControlCenter?.Invoke();
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_panel, panel))
                {
                    _panel = null;   // 关闭后清引用，下次唤起重建
                }
            };
            _panel = panel;
        }
        _panel.Present();
    }

    private void OnStateChanged()
    {
        UpdateVisual();
        if (_panel is { IsVisible: true })
        {
            _panel.RefreshFromState();
        }
    }

    private void OnAutostartChanged()
        => _autostartItem.IsChecked = _app.IsAutostartEnabled;

    /// <summary>
    /// 低电量提醒（阶段 6）：触发链 = <see cref="AppState.LowBatteryAlert"/>（判定纯函数
    /// <see cref="LowBatteryAdvisor"/>）→ 本方法记录信息日志并经 <see cref="INotificationService"/>
    /// 弹气泡（标题/正文文案按产品文案，图标用应用图标）。
    /// </summary>
    private void OnLowBatteryAlert(int percent)
    {
        ErrorLog.Write($"低电量提醒：当前电量 {percent}%（阈值 {_app.Config.LowBatteryThresholdPercent}%）。");
        _notifications.Show(
            "QCY H3 电量低",
            $"当前电量 {percent}%，请及时放入充电",
            NotificationKind.Warning);
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            _app.SetAutostart(enabled);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("切换开机自启失败", exception);
            _autostartItem.IsChecked = _app.IsAutostartEnabled;
        }
    }

    /// <summary>
    /// 托盘图标与 Tooltip：已连接且电量有效 = 「QCY H3 — 已连接\n电量 80%」；
    /// 否则 = 「QCY H3 — {状态}」。图标 = 耳机剪影 + 电量徽章（<see cref="TrayIconRenderer"/>），
    /// 电量仅在 Available（读取成功且新鲜）时展示数字徽章，Unknown/Error/Stale/断连 =
    /// 灰剪影无徽章（不得把旧值/未知值当作现值；完整状态在 tooltip）。
    /// 渲染结果（缓存同实例）不变时不重复赋值 <c>_icon.Icon</c>（避免重建原生句柄，plan D13）。
    /// </summary>
    private void UpdateVisual()
    {
        bool connected = _app.IsConnected;
        int? percent = connected && _app.BatteryValidity == DataValidity.Available
            ? _app.BatteryPercent
            : null;
        Icon icon = TrayIconRenderer.GetIcon(percent);
        if (!ReferenceEquals(icon, _lastIcon))
        {
            _lastIcon = icon;
            _icon.Icon = icon;
        }
        _icon.ToolTipText = percent is int battery
            ? $"QCY H3 — 已连接\n电量 {battery}%"
            : $"QCY H3 — {_app.StateLabel}";
    }

    public void Dispose()
    {
        _app.StateUpdated -= OnStateChanged;
        _app.AutostartChanged -= OnAutostartChanged;
        _app.LowBatteryAlert -= OnLowBatteryAlert;
        _icon.TrayLeftMouseUp -= OnTrayLeftMouseUp;
        _icon.Dispose();
        TrayIconRenderer.ClearCache();
    }
}
