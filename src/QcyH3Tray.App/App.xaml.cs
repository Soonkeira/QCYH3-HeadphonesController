using System.Threading;
using System.Windows;
using QcyH3Tray.Core.Config;

namespace QcyH3Tray.App;

/// <summary>
/// 应用入口：
/// 单实例（命名 Mutex；二次启动 → 置激活信号唤起第一实例主面板后退出）→
/// 全局异常钩子（全部记 <c>%LOCALAPPDATA%\QcyH3Tray\error.log</c>，不崩溃）→
/// 读配置 → 建托盘 → 有目标设备则启动连接，无目标弹首启设备选择窗（取消即退出）。
/// 退出时干净释放 ConnectionManager 与托盘资源。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "QcyH3Tray_SingleInstance";
    private const string ActivationEventName = @"Local\QcyH3Tray_Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWaitRegistration;
    private AppState? _appState;
    private TrayHost? _trayHost;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            SignalFirstInstance();
            Shutdown(0);
            return;
        }

        InstallExceptionHooks();
        base.OnStartup(eventArgs);

        _appState = new AppState();
        _trayHost = new TrayHost(_appState);
        StartActivationListener();

        if (_appState.HasTarget)
        {
            _appState.ApplyTargetFromConfig();
        }
        else
        {
            // 首启：模态选择设备；取消 = 退出应用
            var picker = new DevicePickerWindow(_appState);
            if (picker.ShowDialog() != true)
            {
                Shutdown(0);
                return;
            }
        }

        _ = _appState.ConnectAsync();
    }

    /// <summary>第二实例：置激活信号让第一实例弹出主面板，然后自己退出。</summary>
    private void SignalFirstInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (Exception exception)
        {
            // 第一实例尚未创建事件（极小概率竞态）：记日志，仍按原行为静默退出
            ErrorLog.Write("激活第一实例失败", exception);
        }
    }

    /// <summary>
    /// 第一实例：持有命名激活事件，收到信号即在 UI 线程弹出主面板。
    /// 监听注册失败不影响主流程（仅失去"二次启动唤起"能力）。
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activationEvent = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, ActivationEventName, out _);
            _activationWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                callBack: (_, timedOut) =>
                {
                    if (timedOut)
                    {
                        return;
                    }
                    try
                    {
                        Dispatcher.InvokeAsync(ShowPanel);
                    }
                    catch (Exception exception)
                    {
                        ErrorLog.Write("激活信号处理失败", exception);
                    }
                },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("激活监听初始化失败", exception);
        }
    }

    /// <summary>激活信号回调（已 marshal 到 UI 线程）：弹出主面板。</summary>
    private void ShowPanel() => _trayHost?.PresentPanel();

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        _activationWaitRegistration?.Unregister(null);
        _activationWaitRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;

        _trayHost?.Dispose();
        _trayHost = null;
        _appState?.Dispose();
        _appState = null;

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有互斥体（异常路径提前 Shutdown）时忽略
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(eventArgs);
    }

    /// <summary>三类全局异常钩子：UI Dispatcher / AppDomain / TaskScheduler。</summary>
    private void InstallExceptionHooks()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("UI 线程未处理异常", args.Exception);
            args.Handled = true;   // 吞掉保持运行
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write(
                "非 UI 线程未处理异常（进程将终止）",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }
}
