using System.Windows;
using System.Windows.Media.Imaging;
using Icon = System.Drawing.Icon;

namespace QcyH3Tray.App;

/// <summary>
/// 应用窗口图标（阶段 5）：统一从打包资源 <c>Assets/app.ico</c>（tools/IconGen 生成的
/// 多分辨率 ICO）加载，供各窗口 <c>Icon</c> 共用（控制中心 / EQ 编辑器 / 按键编辑器）。
/// 首次访问加载并缓存（BitmapFrame 取 ICO 首帧 256px，系统自行缩放）；
/// 资源缺失只记日志返回 null（窗口保持系统默认图标，不影响功能）。
/// </summary>
internal static class AppIconSource
{
    private const string PackUri = "pack://application:,,,/Assets/app.ico";

    private static BitmapFrame? _source;

    public static BitmapFrame? TryGetSource()
    {
        if (_source is not null)
        {
            return _source;
        }
        try
        {
            _source = BitmapFrame.Create(new Uri(PackUri, UriKind.Absolute));
            return _source;
        }
        catch (Exception exception)
        {
            ErrorLog.Write("加载应用图标失败", exception);
            return null;
        }
    }

    /// <summary>
    /// 原生 ICO 实例（气泡通知 customIconHandle 用，阶段 6）：按指定尺寸就近选帧并
    /// 缩放到精确大小（H.NotifyIcon 校验自定义图标必须等于系统大图标尺寸，随 DPI 变化）。
    /// 每次调用返回新实例（GDI HICON 与调用方生命周期绑定，调用方负责 Dispose）；
    /// 失败返回 null（调用方回退标准严重级别图标）。
    /// </summary>
    public static Icon? TryGetNativeIcon(int width, int height)
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri(PackUri, UriKind.Absolute));
            if (resource is null)
            {
                return null;
            }
            using var stream = resource.Stream;
            return new Icon(stream, width, height);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("加载原生应用图标失败", exception);
            return null;
        }
    }
}
