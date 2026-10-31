using System.IO;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace QcyH3Tray.Tests;

/// <summary>
/// 玻璃拟态视觉 Token 契约（2026-10-31 改造）：
/// - CornerRadius/Chip = 8（圆角方框，与 CornerRadius/Control=9 同族；交付 1 锁定值，
///   防止回归到 999 胶囊）；
/// - 玻璃画刷齐全且透明度符合设计（Glass.Card 72% 白 / Glass.Card.Strong 85% 白 /
///   Glass.Border 40% 白 / Glass.Fallback = #F2F3F6 不透明降级底色）。
/// 注：测试宿主没有 Application 资源作用域，按文件路径直接解析 Tokens.xaml
/// （与运行时同一份文件，而非拷贝），模式与 HeadphoneArtResourceTests 一致。
/// </summary>
public class GlassTokenTests
{
    private static ResourceDictionary LoadTokens()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "QcyH3Tray.App", "Resources", "Tokens.xaml");
        using FileStream stream = File.OpenRead(path);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QcyH3Tray.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根（QcyH3Tray.sln）");
    }

    [Fact]
    public void ChipCornerRadius_IsRoundedSquare_NotPill()
    {
        var tokens = LoadTokens();

        var radius = Assert.IsType<CornerRadius>(tokens["CornerRadius/Chip"]);
        Assert.Equal(8, radius.TopLeft);
        Assert.Equal(8, radius.TopRight);
        Assert.Equal(8, radius.BottomLeft);
        Assert.Equal(8, radius.BottomRight);
    }

    [Fact]
    public void GlassBrushes_ExistWithDesignedOpacity()
    {
        var tokens = LoadTokens();

        var card = Assert.IsType<SolidColorBrush>(tokens["Brush/Glass.Card"]);
        Assert.Equal(0xB8, card.Color.A);
        Assert.Equal(0xFF, card.Color.R);
        Assert.Equal(0xFF, card.Color.G);
        Assert.Equal(0xFF, card.Color.B);

        var strong = Assert.IsType<SolidColorBrush>(tokens["Brush/Glass.Card.Strong"]);
        Assert.Equal(0xD9, strong.Color.A);

        var border = Assert.IsType<SolidColorBrush>(tokens["Brush/Glass.Border"]);
        Assert.Equal(0x66, border.Color.A);

        var hairline = Assert.IsType<SolidColorBrush>(tokens["Brush/Glass.Hairline"]);
        Assert.Equal(0x1A, hairline.Color.A);

        var fallback = Assert.IsType<SolidColorBrush>(tokens["Brush/Glass.Fallback"]);
        Assert.Equal(0xFF, fallback.Color.A);
        Assert.Equal(0xF2, fallback.Color.R);
        Assert.Equal(0xF3, fallback.Color.G);
        Assert.Equal(0xF6, fallback.Color.B);
    }

    [Fact]
    public void OpaqueControlLayerTokens_Unchanged()
    {
        // 玻璃只作用于窗口背景与页面级卡片；控件层（chips/分段/开关底）保持不透明浅灰
        var tokens = LoadTokens();

        var chip = Assert.IsType<SolidColorBrush>(tokens["Brush/Chip.Background"]);
        Assert.Equal(0xFF, chip.Color.A);
        var track = Assert.IsType<SolidColorBrush>(tokens["Brush/Segment.Track"]);
        Assert.Equal(0xFF, track.Color.A);
    }
}
