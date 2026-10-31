using System.IO;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace QcyH3Tray.Tests;

/// <summary>
/// 阶段 5 产品视觉资源契约：
/// - Resources/HeadphoneArt.xaml 可独立解析（XamlReader，无需 Application 宿主），
///   且 AppImage/HeadphoneHero 与 .Dark 两键均为 ImageSource —— HeadphoneHeroController
///   的展示判定（is ImageSource）因此成立；
/// - App.xaml 把 HeadphoneArt.xaml 合并在 MergedDictionaries 尾部（3a 约定位置，
///   注册即显示）。
/// 注：测试宿主没有 Application 资源作用域，故按文件路径直接解析字典与 App.xaml
/// 文本（与运行时同一份文件，而非拷贝）。
/// </summary>
public class HeadphoneArtResourceTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QcyH3Tray.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根（QcyH3Tray.sln）");
    }

    private static string ArtPath => Path.Combine(
        RepoRoot(), "src", "QcyH3Tray.App", "Resources", "HeadphoneArt.xaml");

    private static ResourceDictionary LoadArt()
    {
        using FileStream stream = File.OpenRead(ArtPath);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }

    [Fact]
    public void HeadphoneArt_ShallowKey_IsImageSource()
    {
        var art = LoadArt();

        Assert.True(art.Contains("AppImage/HeadphoneHero"));
        Assert.IsAssignableFrom<ImageSource>(art["AppImage/HeadphoneHero"]);
    }

    [Fact]
    public void HeadphoneArt_DarkKey_IsImageSource()
    {
        var art = LoadArt();

        Assert.True(art.Contains("AppImage/HeadphoneHero.Dark"));
        Assert.IsAssignableFrom<ImageSource>(art["AppImage/HeadphoneHero.Dark"]);
    }

    [Fact]
    public void HeadphoneArt_BothDrawings_HaveContent()
    {
        var art = LoadArt();

        var light = Assert.IsType<DrawingImage>(art["AppImage/HeadphoneHero"]);
        var dark = Assert.IsType<DrawingImage>(art["AppImage/HeadphoneHero.Dark"]);
        Assert.NotNull(light.Drawing);
        Assert.NotNull(dark.Drawing);
    }

    [Fact]
    public void AppXaml_MergesHeadphoneArt_AtTailOfMergedDictionaries()
    {
        string appXaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "QcyH3Tray.App", "App.xaml"));

        int styles = appXaml.IndexOf("Resources/Styles.xaml", StringComparison.Ordinal);
        int art = appXaml.IndexOf("Resources/HeadphoneArt.xaml", StringComparison.Ordinal);

        Assert.True(styles >= 0, "App.xaml 应合并 Resources/Styles.xaml");
        Assert.True(art > styles, "HeadphoneArt.xaml 必须合并在 MergedDictionaries 尾部（Styles 之后）");
    }
}
