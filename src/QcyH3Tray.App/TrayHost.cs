using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Icon = System.Drawing.Icon;

namespace QcyH3Tray.App;

/// <summary>
/// 托盘宿主（H.NotifyIcon.Wpf）：
/// 图标 = 电量数字徽章（<see cref="BatteryIconRenderer"/>），随电量/连接状态刷新；
/// 左键 = 切换主面板显示/隐藏；右键菜单：打开控制面板 / 立即重连 / 开机自启 / 退出。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    /// <summary>面板被 Deactivated 自动隐藏后，短时间内忽略托盘左键的"再打开"。</summary>
    private static readonly TimeSpan TrayClickHideGrace = TimeSpan.FromMilliseconds(400);

    private readonly AppState _app;
    private readonly TaskbarIcon _icon;
    private MenuItem _autostartItem = null!;
    private ControlPanelWindow? _panel;

    public TrayHost(AppState app)
    {
        _app = app;
        _icon = new TaskbarIcon
        {
            ToolTipText = "QCY H3 — 未连接",
            MenuActivation = PopupActivationMode.RightClick,
            Icon = BatteryIconRenderer.GetIcon("?", BatteryIconRenderer.Gray),
        };
        _icon.TrayLeftMouseUp += OnTrayLeftMouseUp;
        _icon.ContextMenu = BuildContextMenu();

        _app.StateUpdated += OnStateChanged;
        _app.AutostartChanged += OnAutostartChanged;
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

    /// <summary>外部唤起（第二实例激活信号）：不做切换，总是弹出主面板。仅 UI 线程调用。</summary>
    public void PresentPanel()
    {
        _panel ??= new ControlPanelWindow(_app);
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

    /// <summary>托盘图标与 Tooltip：已连接 = 「QCY H3 — 已连接\n电量 80%」；否则 = 「QCY H3 — {状态}」。</summary>
    private void UpdateVisual()
    {
        int? percent = _app.IsConnected ? _app.BatteryPercent : null;
        string badgeText = percent is int value ? value.ToString() : "?";
        Icon icon = BatteryIconRenderer.GetIcon(badgeText, BatteryIconRenderer.ColorFor(percent));
        _icon.Icon = icon;
        _icon.ToolTipText = percent is int battery
            ? $"QCY H3 — 已连接\n电量 {battery}%"
            : $"QCY H3 — {_app.StateLabel}";
    }

    public void Dispose()
    {
        _app.StateUpdated -= OnStateChanged;
        _app.AutostartChanged -= OnAutostartChanged;
        _icon.TrayLeftMouseUp -= OnTrayLeftMouseUp;
        _icon.Dispose();
        BatteryIconRenderer.ClearCache();
    }
}
