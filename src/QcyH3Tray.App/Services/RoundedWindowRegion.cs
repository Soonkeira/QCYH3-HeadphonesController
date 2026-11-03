using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace QcyH3Tray.App.Services;

/// <summary>
/// Clips the HWND itself to a rounded rectangle. WPF Border.CornerRadius only
/// rounds WPF content; DWM Acrylic otherwise still paints a rectangular HWND.
/// Keep this limited to the borderless tray popover, not standard chrome windows.
/// </summary>
internal static class RoundedWindowRegion
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>
    /// Applies a native region in physical pixels. Reapply after a window size/DPI
    /// change: Region coordinates do not automatically resize with the HWND.
    /// Windows takes ownership of region only when SetWindowRgn succeeds.
    /// </summary>
    public static void Apply(Window window, double cornerRadiusDip)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect))
            {
                return;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < 1 || height < 1)
            {
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            int diameterX = Math.Max(2, (int)Math.Round(cornerRadiusDip * dpi.DpiScaleX * 2));
            int diameterY = Math.Max(2, (int)Math.Round(cornerRadiusDip * dpi.DpiScaleY * 2));
            IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1,
                diameterX, diameterY);
            if (region == IntPtr.Zero)
            {
                return;
            }

            if (SetWindowRgn(hwnd, region, redraw: true) == 0)
            {
                _ = DeleteObject(region);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("快捷面板窗口圆角裁剪失败", ex);
        }
    }
}
