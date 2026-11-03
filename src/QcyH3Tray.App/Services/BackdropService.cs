using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QcyH3Tray.App.Services;

/// <summary>背景板请求模式：主窗口（Mica）或弹出类窗口（色调亚克力）。</summary>
internal enum BackdropMode
{
    /// <summary>主窗口（控制中心）：优先可调色 Acrylic，失败时使用 DWMSBT_MAINWINDOW（Mica）兜底。</summary>
    Main,

    /// <summary>弹出类窗口（快捷面板 / EQ 编辑器 / 按键编辑器 / DevicePicker / 对话框）。</summary>
    Transient,
}

/// <summary>实际生效的背景板实现（降级梯队的落点）。</summary>
internal enum BackdropKind
{
    /// <summary>
    /// DWM SystemBackdrop（DWMWA_SYSTEMBACKDROP_TYPE，Win11 22H2+，GPU 合成无输入延迟）。
    /// 主窗口首选（Mica）；弹出类降为回退——其浅色主题 Acrylic（DWMSBT_TRANSIENTWINDOW）
    /// 是近白磨砂、色调不可定制，落在白底内容前视觉上不可见（2026-10-31 真机实锤）。
    /// </summary>
    SystemBackdrop,

    /// <summary>
    /// SetWindowCompositionAttribute ACCENT_ENABLE_ACRYLICBLURBEHIND（未公开 API）。
    /// 弹出类首选（2026-10-31 可见性调优）：GradientColor 色调与 α 完全可控，中性浅灰低透明
    /// tint 在白底内容前仍可感知"磨砂灰"；主窗口与弹出窗口优先。已知代价见 <see cref="BackdropService"/>。
    /// </summary>
    AccentBlur,

    /// <summary>终级兜底：不透明底色 Brush/Glass.Fallback（#F2F3F6）。</summary>
    Opaque,
}

/// <summary>
/// 窗口级模糊玻璃拟态背景板（2026-10-31 玻璃拟态改造；同日可见性调优反转弹出类梯队）：
/// <para>
/// 弹出类窗口（<see cref="BackdropMode.Transient"/>）首选 <see cref="BackdropKind.AccentBlur"/>：
/// ACCENT 亚克力的 GradientColor 色调与 α 完全可控，中性浅灰低透明 tint 在白底网页等内容前
/// 仍可感知磨砂；DWM SystemBackdrop（<c>DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)</c>，
/// 浅色主题 DWMSBT_TRANSIENTWINDOW 是近白磨砂、色调不可定制）降为回退档。
/// 控制中心（<see cref="BackdropMode.Main"/>）也优先真实 Acrylic 模糊，Mica 降为更稳定的回退。两者最终失败均落到不透明底色。每窗口写一行信息日志。
/// </para>
/// <para>
/// 已知代价（照实记录）：ACCENT ACRYLICBLURBEHIND 属未公开 API，微软未文档化；Win10 时代
/// 社区普遍报告部分场景输入延迟（拖动/点击响应拖影）。本机 Win11 26200 实测流畅，以真机为准；
/// 如需对比 GPU 合成档，用诊断开关强制 <c>QCYH3TRAY_BACKDROP=system</c>。
/// </para>
/// <para>
/// 调用约定：在窗口 <c>SourceInitialized</c> 之后调用（句柄已创建）。成功路径要求窗口
/// Background=Transparent（XAML 已如此），WPF 透明区域经扩展帧透出系统背景板；
/// 配合 WindowChrome（GlassFrameThickness=-1）或本服务补发的
/// <c>DwmExtendFrameIntoClientArea(-1)</c> 使用。
/// </para>
/// <para>
/// 诊断开关：环境变量 <c>QCYH3TRAY_BACKDROP</c> = <c>system</c> / <c>accent</c> /
/// <c>opaque</c>（大小写不敏感）可强制走某一档，用于真机降级对比；未设置或无法识别
/// 时按自动梯队（两种窗口均 AccentBlur → SystemBackdrop → Opaque）。
/// </para>
/// </summary>
internal static class BackdropService
{
    /// <summary>诊断覆盖用环境变量名。</summary>
    internal const string OverrideEnvironmentVariable = "QCYH3TRAY_BACKDROP";

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    // 经验值：避免窗口边缘出现黑边伪影（社区 WPF Acrylic 兜底实现通用取值）
    private const int ACCENT_FLAG_EDGE_FIX = 0x2;

    // Flat frosted white: native Acrylic blurs the wallpaper; a low-opacity
    // pure white tint allows the blurred background to remain visible.
    // The WPF foreground shell provides independent text contrast.
    internal const byte AccentTintAlpha = 0x38;
    internal const byte AccentTintR = 0xFF;
    internal const byte AccentTintG = 0xFF;
    internal const byte AccentTintB = 0xFF;

    /// <summary>打包后的 ACCENT tint（字节序见 <see cref="PackAccentGradientColor"/>），测试锁定用。</summary>
    internal static readonly uint AccentTintGradientColor =
        PackAccentGradientColor(AccentTintAlpha, AccentTintR, AccentTintG, AccentTintB);

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

    /// <summary>SetWindowCompositionAttribute WCA_ACCENT_POLICY 的未公开策略结构；GradientColor 为 ABGR 打包。</summary>
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
        RequestRoundedCorners(hwnd);
        // Respect Windows high-contrast accessibility settings over decorative blur.
        if (SystemParameters.HighContrast)
        {
            ApplyOpaque(window, label, forced, "Windows 高对比度模式");
            return;
        }

        // 浮动玻璃风格（2026-11-02）：两种窗口均优先真实可调色 Acrylic；
        // 若系统禁止该效果则回退到 DWM system backdrop，最后是不透明安全底色。
        // Both surfaces prioritize *actual backdrop blur* (the desired floating-glass
        // visual). Mica is kept as the documented, hardware-accelerated fallback.
        BackdropKind[] ladder =
            [BackdropKind.AccentBlur, BackdropKind.SystemBackdrop, BackdropKind.Opaque];

        foreach (BackdropKind kind in ladder)
        {
            if (forced is not null && forced != kind)
            {
                continue;   // 诊断覆盖只走被强制的档位
            }

            (bool applied, string detail) = TryRung(kind, hwnd, mode);
            if (applied)
            {
                ErrorLog.Write($"[Backdrop] 窗口「{label}」= {detail}{Suffix(forced)}");
                return;
            }
            if (forced == kind)
            {
                // 强制档失败（旧系统 / 调用失败）：直接落到不透明底，保证可读性
                ApplyOpaque(window, label, forced, $"强制 {kind} 失败（{detail}）");
                return;
            }
        }

        // 自动梯队全部失败：终级兜底不透明底色（可读性优先）
        string reason = forced == BackdropKind.Opaque
            ? "诊断覆盖强制 Opaque"
            : "SystemBackdrop 与 AccentBlur 均不可用";
        ApplyOpaque(window, label, forced, reason);
    }

    /// <summary>Ask the DWM for rounded outer window corners when supported (Windows 11).
    /// This is best-effort: older Windows releases ignore the attribute.</summary>
    private static void RequestRoundedCorners(IntPtr hwnd)
    {
        try
        {
            int preference = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // Decorative only; never block application startup.
        }
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

    /// <summary>
    /// 尝试一个背景板档位：成功返回 (true, 日志描述)；失败返回 (false, 失败原因摘要)。
    /// </summary>
    private static (bool Applied, string Detail) TryRung(BackdropKind kind, IntPtr hwnd, BackdropMode mode) => kind switch
    {
        BackdropKind.SystemBackdrop => TrySystemBackdropRung(hwnd, mode),
        BackdropKind.AccentBlur => TryAccentBlurRung(hwnd),
        _ => (false, "未知档位"),
    };

    private static (bool Applied, string Detail) TrySystemBackdropRung(IntPtr hwnd, BackdropMode mode)
    {
        int hr = TryApplySystemBackdrop(hwnd, mode);
        return hr == 0
            ? (true, $"SystemBackdrop({Describe(mode)})")
            : (false, $"SystemBackdrop 失败 hr=0x{hr:X8}");
    }

    private static (bool Applied, string Detail) TryAccentBlurRung(IntPtr hwnd)
    {
        return TryApplyAccentBlur(hwnd)
            ? (true, $"AccentBlur(ACRYLICBLURBEHIND tint=#{AccentTintR:X2}{AccentTintG:X2}{AccentTintB:X2} α=0x{AccentTintAlpha:X2})")
            : (false, "AccentBlur 调用失败");
    }

    /// <summary>
    /// AccentPolicy.GradientColor 按 COLORREF 习惯为 ABGR 打包（0xAABBGGRR：α 在最高字节，
    /// B/G/R 依次排下）。单独提出便于测试锁定 tint 值——调用点用 ARGB 语义传参，
    /// 字节序细节不散落。
    /// </summary>
    internal static uint PackAccentGradientColor(byte a, byte r, byte g, byte b) =>
        (uint)((a << 24) | (b << 16) | (g << 8) | r);

    /// <summary>应用 DWM SystemBackdrop，返回 HRESULT（0 = 成功）。</summary>
    private static int TryApplySystemBackdrop(IntPtr hwnd, BackdropMode mode)
    {
        try
        {
            // 先把帧扩展到整个客户区（-1）：SystemBackdrop 只在扩展帧区域透出
            var margins = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
            int hr = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (hr != 0)
            {
                return hr;
            }
            EnsureTransparentComposition(hwnd);

            int backdropType = mode == BackdropMode.Main ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
        }
        catch (Exception)
        {
            return unchecked((int)0xFFFFFFFF);
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

            // GradientColor 字节序见 PackAccentGradientColor（COLORREF ABGR 习惯）。
            // Use a low-alpha pure white tint, never a blue/gray or gradient tint.
            var policy = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = ACCENT_FLAG_EDGE_FIX,
                GradientColor = AccentTintGradientColor,
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
