using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QcyH3Tray.App;

/// <summary>
/// 3D 耳机展示位（HeadphoneHero）的接入结构（阶段 3a，plan.md P1-1 #2）：
/// 资源字典键 <see cref="ResourceKey"/> 存在 <see cref="ImageSource"/>（阶段 5 放入
/// 真实矢量渲染图）时按自然高度显示，否则整块折叠为 0 高度——绝不放占位位图。
/// 判定逻辑抽为纯函数（无控件依赖），布局结果可单测；窗口侧只做赋值。
/// </summary>
internal static class HeadphoneHeroController
{
    /// <summary>App.xaml 资源字典中的耳机渲染图键（阶段 5 定义；未定义时展示位折叠）。</summary>
    public const string ResourceKey = "AppImage/HeadphoneHero";

    /// <summary>展示位自然高度（DIP）。</summary>
    public const double NaturalHeight = 96;

    /// <summary>
    /// 资源 → 展示位布局：键存在且值是 <see cref="ImageSource"/> → 可见 + 自然高度；
    /// 键不存在 / 值类型不符（占位字符串等）→ 折叠为 0 高。
    /// </summary>
    public static HeadphoneHeroLayout Resolve(object? resource)
        => Resolve(resource, NaturalHeight);

    /// <summary>
    /// 同 <see cref="Resolve(object?)"/>，但自然高度由调用方指定
    /// （阶段 3b：控制中心展示位更高，资源键仍唯一）。
    /// </summary>
    public static HeadphoneHeroLayout Resolve(object? resource, double naturalHeight)
        => resource is ImageSource
            ? new HeadphoneHeroLayout(Visible: true, Height: naturalHeight)
            : HeadphoneHeroLayout.Collapsed;

    /// <summary>把布局结果落到窗口控件（UI 线程调用）：折叠为 0 高并清空图源，可见则赋图。</summary>
    public static void Apply(Border heroArea, Image heroImage, object? resource)
        => Apply(heroArea, heroImage, resource, NaturalHeight);

    /// <summary>同 <see cref="Apply(Border, Image, object?)"/>，但自然高度由调用方指定。</summary>
    public static void Apply(Border heroArea, Image heroImage, object? resource, double naturalHeight)
    {
        HeadphoneHeroLayout layout = Resolve(resource, naturalHeight);
        heroArea.Visibility = layout.WpfVisibility;
        heroArea.Height = layout.Height;
        heroImage.Source = layout.Visible && resource is ImageSource image ? image : null;
    }
}

/// <summary>展示位布局结果：可见性 + 高度（纯数据，供测试断言）。</summary>
internal readonly record struct HeadphoneHeroLayout(bool Visible, double Height)
{
    /// <summary>资源缺失时的折叠布局（0 高）。</summary>
    public static readonly HeadphoneHeroLayout Collapsed = new(Visible: false, Height: 0);

    /// <summary>WPF 可见性投影。</summary>
    public Visibility WpfVisibility => Visible ? Visibility.Visible : Visibility.Collapsed;
}
