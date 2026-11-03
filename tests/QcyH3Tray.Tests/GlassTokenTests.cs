using System.IO;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace QcyH3Tray.Tests;

/// <summary>
/// 玻璃拟态视觉 Token 契约（2026-10-31 改造 → 2026-11-02 悬浮玻璃升级）：
/// - CornerRadius/Chip = 17（2026-11-02 改值：MinHeight 34 的一半，即胶囊几何上限本身；
///   锁定 17 且不得超出 MinHeight/2——回归到 999 一类"真胶囊"值时两条断言同时失败）；
/// - 玻璃画刷齐全且透明度符合设计。Glass.Card 已从 60% 纯色升级为纵向渐变：
///   顶部 #E4FFFFFF（89% 白）→ 底部 #BCFFFFFF（74% 白），均为纯白 RGB——半透明
///   渐变负责"材质深度"，真实模糊由 BackdropService 的系统背景板负责；
/// - 纯色玻璃画刷保持原契约（Glass.Card.Strong 85% 白 / Glass.Border 40% 白 /
///   Glass.Hairline 10% 黑 / Glass.Fallback = #F2F3F6 不透明降级底色）。
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
        // 2026-11-03 修正锁定值：8（圆角方框，回归 11-02 之前的原契约；
        // "restore chip corner contract"）。双保险：锁定精确值 8 + 不得超出
        // MinHeight/2，回归到 999 一类"真胶囊"值时两条断言同时失败。
        Assert.Equal(8, radius.TopLeft);
        Assert.Equal(8, radius.TopRight);
        Assert.Equal(8, radius.BottomLeft);
        Assert.Equal(8, radius.BottomRight);

        var chipMinHeight = Assert.IsType<double>(tokens["Size/Chip.MinHeight"]);
        Assert.InRange(radius.TopLeft, 0, chipMinHeight / 2);
    }

    [Fact]
    public void GlassBrushes_ExistWithDesignedOpacity()
    {
        var tokens = LoadTokens();

        // 2026-11-03 修正：Glass.Card 回到 SolidColorBrush 纯白低透明（平光磨砂，
        // 取消 11-02 的渐变材质与蓝灰 tint）。锁定 0x99（60% 白）：下限守住
        // "磨砂纹理必须透出"（2026-10-31 可见性调优的结论），上限防止回归为
        // 0xFF 不透明实心（磨砂不可感知）。RGB 必须纯白——蓝灰染色由
        // BackdropServiceTests 的 tint 契约另行锁定。
        var card = Assert.IsType<SolidColorBrush>(tokens["Brush/Glass.Card"]);
        Assert.Equal(0x99, card.Color.A);
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
