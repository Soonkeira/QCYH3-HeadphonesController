using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QcyH3Tray.App.Services;

/// <summary>背景板请求模式：主窗口（Mica）或弹出类窗口（Acrylic）。</summary>
internal enum BackdropMode
{
    /// <summary>主窗口（控制中心）：DWMSBT_MAINWINDOW（Mica）。</summary>
    Main,

    /// <summary>弹出类窗口（快捷面板 / 对话框）：DWMSBT_TRANSIENTWINDOW（Acrylic）。</summary>
    Transient,
}

/// <summary>实际生效的背景板实现（降级梯队的落点）。</summary>
internal enum BackdropKind
{
    /// <summary>DWM SystemBackdrop（DWMWA_SYSTEMBACKDROP_TYPE，Win11 22H2+，GPU 合成无输入延迟）。</summary>
    SystemBackdrop,

    /// <summary>SetWindowCompositionAttribute ACCENT_ENABLE_ACRYLICBLURBEHIND 兜底（Win10 / 早期 Win11）。</summary>
    AccentBlur,

    /// <summary>终级兜底：不透明底色 Brush/Glass.Fallback（#F2F3F6）。</summary>
    Opaque,
}

/// <summary>
/// 窗口级模糊玻璃拟态背景板（2026-10-31 玻璃拟态改造）：
/// 首选 DWM SystemBackdrop（<c>DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)</c>，
/// 浅色主题、不动 immersive dark mode）；失败按梯队降级到 ACCENT ACRYLICBLURBEHIND，
/// 再失败落到不透明底色。每窗口写一行信息日志（<see cref="ErrorLog"/>）。
/// <para>
/// 调用约定：在窗口 <c>SourceInitialized</c> 之后调用（句柄已创建）。成功路径要求窗口
/// Background=Transparent（XAML 已如此），WPF 透明区域经扩展帧透出系统背景板；
/// 配合 WindowChrome（GlassFrameThickness=-1）或本服务补发的
/// <c>DwmExtendFrameIntoClientArea(-1)</c> 使用。
/// </para>
/// <para>
/// 诊断开关：环境变量 <c>QCYH3TRAY_BACKDROP</c> = <c>system</c> / <c>accent</c> /
/// <c>opaque</c>（大小写不敏感）可强制走某一档，用于真机降级对比；未设置或无法识别
/// 时按自动梯队（SystemBackdrop → AccentBlur → Opaque）。
/// </para>
/// </summary>
internal static class BackdropService
{
    /// <summary>诊断覆盖用环境变量名。</summary>
    internal const string OverrideEnvironmentVariable = "QCYH3TRAY_BACKDROP";

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    // 经验值：避免窗口边缘出现黑边伪影（社区 WPF Acrylic 兜底实现通用取值）
    private const int ACCENT_FLAG_EDGE_FIX = 0x2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int sizeOfValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    /// <summary>
    /// 应用背景板（降级梯队自动执行）。在 SourceInitialized 后调用；结果写一行日志。
    /// </summary>
    public static void Apply(Window window, BackdropMode mode)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;   // 句柄拿不到（理论上不发生）：静默放弃，窗口保持 XAML 底色
        }

        string label = window.Title;
        BackdropKind? forced = ResolveOverride(Environment.GetEnvironmentVariable(OverrideEnvironmentVariable));

        // 1) 首选：DWM SystemBackdrop（Win11 22H2+）
        if (forced is null or BackdropKind.SystemBackdrop)
        {
            if (TryApplySystemBackdrop(hwnd, mode, out int hr))
            {
                ErrorLog.Write($"[Backdrop] 窗口「{label}」= SystemBackdrop({Describe(mode)}){Suffix(forced)}");
                return;
            }
            if (forced == BackdropKind.SystemBackdrop)
            {
                // 强制 system 却失败（旧系统）：直接落到不透明底，保证可读性
                ApplyOpaque(window, label, forced, $"hr=0x{hr:X8}");
                return;
            }
        }

        // 2) 兜底一：SetWindowCompositionAttribute ACRYLICBLURBEHIND（Win10 / 旧 Win11）
        if (forced is null or BackdropKind.AccentBlur)
        {
            if (TryApplyAccentBlur(hwnd))
            {
                ErrorLog.Write($"[Backdrop] 窗口「{label}」= AccentBlur(ACRYLICBLURBEHIND) 兜底{Suffix(forced)}");
                return;
            }
            if (forced == BackdropKind.AccentBlur)
            {
                ApplyOpaque(window, label, forced, "AccentBlur 调用失败");
                return;
            }
        }

        // 3) 终级兜底：不透明底色（可读性优先）
        string reason = forced == BackdropKind.Opaque
            ? "诊断覆盖强制 Opaque"
            : "SystemBackdrop 与 AccentBlur 均不可用";
        ApplyOpaque(window, label, forced, reason);
    }

    /// <summary>解析诊断环境变量（未知值按自动梯队处理，返回 null）。</summary>
    internal static BackdropKind? ResolveOverride(string? environmentValue) => environmentValue?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => null,
        "system" or "mica" => BackdropKind.SystemBackdrop,
        "accent" or "acrylic" => BackdropKind.AccentBlur,
        "opaque" or "off" => BackdropKind.Opaque,
        _ => null,
    };

    private static bool TryApplySystemBackdrop(IntPtr hwnd, BackdropMode mode, out int hresult)
    {
        try
        {
            // 先把帧扩展到整个客户区（-1）：SystemBackdrop 只在扩展帧区域透出
            var margins = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
            hresult = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (hresult != 0)
            {
                return false;
            }
            EnsureTransparentComposition(hwnd);

            int backdropType = mode == BackdropMode.Main ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
            hresult = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            return hresult == 0;
        }
        catch (Exception)
        {
            hresult = unchecked((int)0xFFFFFFFF);
            return false;
        }
    }

    /// <summary>
    /// WPF 侧必要配套：标准 chrome 窗口的 HwndSource 默认把客户区清成不透明黑，
    /// 扩展帧区域因此渲染成黑色（快捷面板走 WindowChrome 已由 WPF 自动置透明，
    /// 普通窗口必须显式设置）——置为 Transparent 后 alpha=0 区域才真正透出背景板。
    /// </summary>
    private static void EnsureTransparentComposition(IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd) is HwndSource source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
    }

    private static bool TryApplyAccentBlur(IntPtr hwnd)
    {
        try
        {
            var margins = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0)
            {
                return false;
            }
            EnsureTransparentComposition(hwnd);

            // GradientColor 为 ABGR：淡白 40% 叠加，保证透明区域兜底可读
            var policy = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = ACCENT_FLAG_EDGE_FIX,
                GradientColor = 0x66FFFFFF,
            };
            IntPtr policyPointer = Marshal.AllocHGlobal(Marshal.SizeOf(policy));
            try
            {
                Marshal.StructureToPtr(policy, policyPointer, fDeleteOld: false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = policyPointer,
                    SizeOfData = Marshal.SizeOf(policy),
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(policyPointer);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ApplyOpaque(Window window, string label, BackdropKind? forced, string reason)
    {
        DisableAccent(window);
        window.Background = ResolveFallbackBrush();
        ErrorLog.Write($"[Backdrop] 窗口「{label}」= Opaque({DescribeBrush(window.Background)}) 兜底（{reason}）{Suffix(forced)}");
    }

    /// <summary>不透明兜底前先关闭可能残留的 ACCENT 模糊（失败忽略）。</summary>
    private static void DisableAccent(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            var policy = new AccentPolicy { AccentState = ACCENT_DISABLED, GradientColor = 0 };
            IntPtr policyPointer = Marshal.AllocHGlobal(Marshal.SizeOf(policy));
            try
            {
                Marshal.StructureToPtr(policy, policyPointer, fDeleteOld: false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = policyPointer,
                    SizeOfData = Marshal.SizeOf(policy),
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(policyPointer);
            }
        }
        catch (Exception)
        {
            // 兜底路径上的清理尽力而为
        }
    }

    private static Brush ResolveFallbackBrush()
    {
        if (Application.Current?.TryFindResource("Brush/Glass.Fallback") is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF6));   // 与 Token 同值的双保险
    }

    private static string Describe(BackdropMode mode) => mode == BackdropMode.Main ? "Mica" : "Acrylic";

    private static string DescribeBrush(Brush brush) => brush is SolidColorBrush solid
        ? $"#{solid.Color.ToString()[1..]}"
        : brush.GetType().Name;

    private static string Suffix(BackdropKind? forced) => forced is null ? "" : "（诊断覆盖）";
}
