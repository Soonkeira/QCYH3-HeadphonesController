using System.Windows;

namespace QcyH3Tray.App;

/// <summary>
/// 常驻窗口单例协调（阶段 3b）：控制中心与 EQ 编辑器在整个应用生命周期内各只有一个实例。
/// 复用判据沿用 D6 修复的模式（<c>IsLoaded</c>：窗口被 Alt+F4 等真正关闭后重建，绝不对已
/// Close 的窗口调用 Show）；关闭即置空引用。所有方法仅 UI 线程调用。
/// </summary>
internal sealed class WindowCoordinator
{
    private readonly AppState _app;
    private ControlCenterWindow? _controlCenter;
    private EqEditorWindow? _eqEditor;
    private KeyMapEditorWindow? _keyMapEditor;

    public WindowCoordinator(AppState app) => _app = app;

    /// <summary>打开（或激活已存在的）控制中心。关窗 = Hide 复用，不销毁。</summary>
    public void ShowControlCenter()
    {
        if (_controlCenter is not { IsLoaded: true })
        {
            var window = new ControlCenterWindow(_app, this);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_controlCenter, window))
                {
                    _controlCenter = null;
                }
            };
            _controlCenter = window;
        }
        _controlCenter.Present();
    }

    /// <summary>打开（或激活已存在的）EQ 编辑器；被真正关闭后下次重建。</summary>
    public void ShowEqEditor()
    {
        if (_eqEditor is not { IsLoaded: true })
        {
            var window = new EqEditorWindow(_app);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_eqEditor, window))
                {
                    _eqEditor = null;
                }
            };
            _eqEditor = window;
        }
        if (_eqEditor.WindowState == WindowState.Minimized)
        {
            _eqEditor.WindowState = WindowState.Normal;   // 最小化时 Show/Activate 不会还原（终验实测发现）
        }
        _eqEditor.Show();
        _eqEditor.Activate();
    }

    /// <summary>打开（或激活已存在的）按键自定义编辑器；被真正关闭后下次重建。</summary>
    public void ShowKeyMapEditor()
    {
        if (_keyMapEditor is not { IsLoaded: true })
        {
            var window = new KeyMapEditorWindow(_app);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_keyMapEditor, window))
                {
                    _keyMapEditor = null;
                }
            };
            _keyMapEditor = window;
        }
        if (_keyMapEditor.WindowState == WindowState.Minimized)
        {
            _keyMapEditor.WindowState = WindowState.Normal;   // 最小化时 Show/Activate 不会还原（终验实测发现）
        }
        _keyMapEditor.Show();
        _keyMapEditor.Activate();
    }
}
