using System.Windows;
using System.Windows.Media;
using QcyH3Tray.App;
using QcyH3Tray.Core.App;
using Xunit;

namespace QcyH3Tray.Tests;

/// <summary>
/// 阶段 3a 面板展示层纯逻辑：
/// - <see cref="HeadphoneHeroController"/>：资源存在（ImageSource）→ 显示自然高度，不存在 → 折叠 0 高；
/// - <see cref="PanelPresentations"/>：九态 → 状态色调与画刷键、电量 validity → 文字/色调映射。
/// XAML 模板本身不可单测（真机 UI 自动化覆盖），此处锁定映射契约。
/// </summary>
public class PanelPresentationTests
{
    // ───────────────────────── HeadphoneHero ─────────────────────────

    [Fact]
    public void Hero_ResourceKey_UsesDocumentedAppImageContract()
    {
        // 与 App.xaml 注释约定一致；阶段 5 的矢量渲染图必须以此键注册
        Assert.Equal("AppImage/HeadphoneHero", HeadphoneHeroController.ResourceKey);
    }

    [Fact]
    public void Resolve_ResourceMissing_CollapsesToZeroHeight()
    {
        var layout = HeadphoneHeroController.Resolve(null);

        Assert.False(layout.Visible);
        Assert.Equal(0, layout.Height);
        Assert.Equal(Visibility.Collapsed, layout.WpfVisibility);
    }

    [Fact]
    public void Resolve_ResourceNotAnImage_CollapsesToZeroHeight()
    {
        // 占位字符串等非 ImageSource 资源视同缺失（绝不显示占位位图）
        var layout = HeadphoneHeroController.Resolve("placeholder");

        Assert.False(layout.Visible);
        Assert.Equal(0, layout.Height);
        Assert.Equal(Visibility.Collapsed, layout.WpfVisibility);
    }

    [Fact]
    public void Resolve_ImageSourcePresent_ShowsAtNaturalHeight()
    {
        // DrawingImage：公共无参构造的 ImageSource（Freezable，任意线程可构造；不参与渲染）
        var layout = HeadphoneHeroController.Resolve(new DrawingImage());

        Assert.True(layout.Visible);
        Assert.Equal(HeadphoneHeroController.NaturalHeight, layout.Height);
        Assert.Equal(Visibility.Visible, layout.WpfVisibility);
    }

    // ───────────────────────── 状态色调 ─────────────────────────

    [Theory]
    [InlineData(ConnectionState.Connected, StatusTone.Success)]
    [InlineData(ConnectionState.Degraded, StatusTone.Alert)]
    [InlineData(ConnectionState.Syncing, StatusTone.Info)]
    [InlineData(ConnectionState.Connecting, StatusTone.Info)]
    [InlineData(ConnectionState.Reconnecting, StatusTone.Info)]
    [InlineData(ConnectionState.Scanning, StatusTone.Info)]
    [InlineData(ConnectionState.Disconnected, StatusTone.Muted)]
    [InlineData(ConnectionState.Unreachable, StatusTone.Muted)]
    [InlineData(ConnectionState.BluetoothOff, StatusTone.Muted)]
    public void ToneFor_CoversAllNineStates(ConnectionState state, StatusTone expected)
        => Assert.Equal(expected, PanelPresentations.ToneFor(state));

    [Theory]
    [InlineData(StatusTone.Success, "Brush/Success")]
    [InlineData(StatusTone.Info, "Brush/Accent")]
    [InlineData(StatusTone.Alert, "Brush/Danger")]
    [InlineData(StatusTone.Muted, "Brush/Text.Tertiary")]
    public void BrushKeyFor_StatusTone_UsesTokenKeys(StatusTone tone, string expectedKey)
        => Assert.Equal(expectedKey, PanelPresentations.BrushKeyFor(tone));

    // ───────────────────────── 电量映射 ─────────────────────────

    [Fact]
    public void FormatBattery_Available_ShowsCurrentValueInNormalTone()
    {
        var (text, tone) = PanelPresentations.FormatBattery(DataValidity.Available, 85);

        Assert.Equal("85%", text);
        Assert.Equal(BatteryTone.Normal, tone);
        Assert.Equal("Brush/Text.Primary", PanelPresentations.BrushKeyFor(tone));
    }

    [Fact]
    public void FormatBattery_AvailableWithoutValue_FallsBackToPlaceholder()
    {
        var (text, tone) = PanelPresentations.FormatBattery(DataValidity.Available, null);

        Assert.Equal("--", text);
        Assert.Equal(BatteryTone.Muted, tone);
    }

    [Fact]
    public void FormatBattery_Loading_ShowsEllipsis()
    {
        var (text, tone) = PanelPresentations.FormatBattery(DataValidity.Loading, 85);

        Assert.Equal("…", text);
        Assert.Equal(BatteryTone.Muted, tone);
    }

    [Fact]
    public void FormatBattery_Stale_ShowsOldValueMuted()
    {
        var (text, tone) = PanelPresentations.FormatBattery(DataValidity.Stale, 70);

        Assert.Equal("70%", text);
        Assert.Equal(BatteryTone.Muted, tone);
        Assert.Equal("Brush/Text.Tertiary", PanelPresentations.BrushKeyFor(tone));
    }

    [Theory]
    [InlineData(DataValidity.Unknown)]
    [InlineData(DataValidity.Error)]
    [InlineData(DataValidity.Unsupported)]
    public void FormatBattery_UnknownOrError_ShowsPlaceholderMuted(DataValidity validity)
    {
        var (text, tone) = PanelPresentations.FormatBattery(validity, 42);

        Assert.Equal("--", text);
        Assert.Equal(BatteryTone.Muted, tone);
    }
}
