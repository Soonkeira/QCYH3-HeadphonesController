using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Icon = System.Drawing.Icon;

namespace QcyH3Tray.App;

/// <summary>通知严重级别（App 层枚举，避免抽象接口依赖 H.NotifyIcon 类型）。</summary>
internal enum NotificationKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 桌面通知抽象（可注入；单测以假实现断言调用参数，不触碰真实 UI）。
/// </summary>
internal interface INotificationService
{
    /// <summary>显示一条通知（标题/正文/严重级别；图标由实现自取）。</summary>
    void Show(string title, string message, NotificationKind kind);
}

/// <summary>
/// 托盘气泡通知（阶段 6）：H.NotifyIcon <c>TaskbarIcon.ShowNotification</c>，
/// Windows 10+ 自动呈现为 toast。不引 Microsoft.Toolkit.Uwp.Notifications——
/// 免去 AUMID / 开始菜单快捷方式依赖，便携版解压即可用。
/// <para>
/// 自定义图标用应用图标（<see cref="AppIconSource"/>），且必须精确等于系统大图标尺寸
/// （H.NotifyIcon 按 <c>GetSystemMetrics(SM_CXICON/SM_CYICON)</c> 校验，随 DPI 缩放，
/// 尺寸不符直接抛 InvalidOperationException）——因此按系统度量就近选帧并缩放到精确
/// 尺寸；不符时回退系统标准严重级别图标（customIconHandle=null），通知本身不受影响。
/// </para>
/// </summary>
internal sealed class TrayNotificationService : INotificationService
{
    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;

    private readonly Func<TaskbarIcon?> _iconProvider;

    public TrayNotificationService(Func<TaskbarIcon?> iconProvider)
    {
        _iconProvider = iconProvider;
    }

    public void Show(string title, string message, NotificationKind kind)
    {
        TaskbarIcon? icon = _iconProvider();
        if (icon is null)
        {
            ErrorLog.Write($"气泡通知跳过（托盘图标不可用）：{title}");
            return;
        }
        try
        {
            using Icon? customIcon = TryCreateCustomIcon();
            icon.ShowNotification(
                title,
                message,
                ToSystemIcon(kind),
                customIconHandle: customIcon?.Handle,
                largeIcon: true,
                sound: true,
                respectQuietTime: false,
                realtime: false,
                timeout: null);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("气泡通知显示失败", exception);
        }
    }

    /// <summary>
    /// 应用图标缩放到系统大图标精确尺寸（SM_CXICON×SM_CYICON，DPI 相关；125% = 40×40）。
    /// 尺寸校验不符或创建失败返回 null（回退标准图标，不阻断通知）。
    /// </summary>
    private static Icon? TryCreateCustomIcon()
    {
        int width = GetSystemMetrics(SM_CXICON);
        int height = GetSystemMetrics(SM_CYICON);
        Icon? customIcon;
        try
        {
            customIcon = AppIconSource.TryGetNativeIcon(width, height);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("创建通知自定义图标失败（回退标准图标）", exception);
            return null;
        }
        if (customIcon is null || customIcon.Width != width || customIcon.Height != height)
        {
            customIcon?.Dispose();
            return null;
        }
        return customIcon;   // 调用方 using 释放（ShowNotification 同步消费句柄）
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static NotificationIcon ToSystemIcon(NotificationKind kind) => kind switch
    {
        NotificationKind.Warning => NotificationIcon.Warning,
        NotificationKind.Error => NotificationIcon.Error,
        _ => NotificationIcon.Info,
    };
}
