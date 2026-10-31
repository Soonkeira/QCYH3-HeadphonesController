using System.Windows.Media;
using QcyH3Tray.App;
using Xunit;

namespace QcyH3Tray.Tests;

/// <summary>
/// 阶段 5 托盘图标纯函数（<see cref="TrayIconRenderer"/>）：
/// 徽章文本（null → 空串=不画徽章）、徽章分级色（沿用电量分级）、
/// 剪影活跃/置灰判定。GDI 渲染与缓存本身不在此覆盖（需 STA + 句柄生命周期，
/// 与原 BatteryIconRenderer 同策略）。
/// </summary>
public class TrayIconRendererTests
{
    // ───────────────────────── 徽章文本 ─────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData(0, "0")]
    [InlineData(5, "5")]
    [InlineData(80, "80")]
    [InlineData(100, "100")]
    public void BadgeTextFor_MapsPercentToDigitsOrEmpty(int? percent, string expected)
        => Assert.Equal(expected, TrayIconRenderer.BadgeTextFor(percent));

    // ───────────────────────── 徽章分级色 ─────────────────────────

    [Fact]
    public void ColorFor_Null_IsGray()
    {
        Color color = TrayIconRenderer.ColorFor(null);

        Assert.Equal(TrayIconRenderer.Gray, color);
    }

    [Theory]
    [InlineData(0, "Red")]
    [InlineData(20, "Red")]
    [InlineData(21, "Orange")]
    [InlineData(50, "Orange")]
    [InlineData(51, "Green")]
    [InlineData(80, "Green")]
    [InlineData(100, "Green")]
    public void ColorFor_FollowsBatteryGrading(int percent, string expectedName)
    {
        Color expected = expectedName switch
        {
            "Red" => TrayIconRenderer.Red,
            "Orange" => TrayIconRenderer.Orange,
            _ => TrayIconRenderer.Green,
        };

        Assert.Equal(expected, TrayIconRenderer.ColorFor(percent));
    }

    // ───────────────────────── 剪影状态 ─────────────────────────

    [Fact]
    public void SilhouetteFillFor_Null_IsMutedGray()
        => Assert.Equal(TrayIconRenderer.SilhouetteFillMuted, TrayIconRenderer.SilhouetteFillFor(null));

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(80)]
    public void SilhouetteFillFor_WithReading_IsActiveWhite(int percent)
        => Assert.Equal(TrayIconRenderer.SilhouetteFillActive, TrayIconRenderer.SilhouetteFillFor(percent));

    [Fact]
    public void Silhouette_ActiveAndMuted_AreDistinct()
        => Assert.NotEqual(TrayIconRenderer.SilhouetteFillActive, TrayIconRenderer.SilhouetteFillMuted);
}
