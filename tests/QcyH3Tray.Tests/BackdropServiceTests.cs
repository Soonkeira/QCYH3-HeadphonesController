using QcyH3Tray.App.Services;
using Xunit;

namespace QcyH3Tray.Tests;

/// <summary>
/// 玻璃拟态背景板的诊断覆盖解析（BackdropService.ResolveOverride 纯函数）：
/// 环境变量 QCYH3TRAY_BACKDROP 未设置 / 空 / auto / 无法识别 → null（自动降级梯队）；
/// system|mica → SystemBackdrop；accent|acrylic → AccentBlur；opaque|off → Opaque。
/// 大小写不敏感、容忍首尾空白（真机降级对比用，键值宽松一些减少排查成本）。
/// 另锁定 ACCENT tint（2026-10-31 可见性调优）：中性浅灰低透明、禁纯白（浅色主题下纯白
/// tint 与 DWM Acrylic 一样隐形），ARGB 语义入参、ABGR（COLORREF 习惯）打包。
/// </summary>
public class BackdropServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("unknown-value")]
    public void ResolveOverride_UnsetOrUnknown_ReturnsNull(string? environmentValue)
    {
        Assert.Null(BackdropService.ResolveOverride(environmentValue));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("SYSTEM")]
    [InlineData(" System ")]
    [InlineData("mica")]
    public void ResolveOverride_SystemValues_ForceSystemBackdrop(string environmentValue)
    {
        Assert.Equal(BackdropKind.SystemBackdrop, BackdropService.ResolveOverride(environmentValue));
    }

    [Theory]
    [InlineData("accent")]
    [InlineData("Acrylic")]
    [InlineData(" ACRYLIC ")]
    public void ResolveOverride_AccentValues_ForceAccentBlur(string environmentValue)
    {
        Assert.Equal(BackdropKind.AccentBlur, BackdropService.ResolveOverride(environmentValue));
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("OFF")]
    [InlineData(" Off ")]
    public void ResolveOverride_OpaqueValues_ForceOpaque(string environmentValue)
    {
        Assert.Equal(BackdropKind.Opaque, BackdropService.ResolveOverride(environmentValue));
    }

    [Fact]
    public void PackAccentGradientColor_PacksAsColorrefAbgr()
    {
        // ARGB 语义入参 (a=0x12, r=0x34, g=0x56, b=0x78) → 0xAABBGGRR = 0x12785634
        //（COLORREF ABGR 字节序：α 最高字节，B/G/R 依次排下）
        Assert.Equal(0x12785634u, BackdropService.PackAccentGradientColor(0x12, 0x34, 0x56, 0x78));
    }

    [Fact]
    public void AccentTintGradientColor_IsPackedFromDesignedArgb()
    {
        // tint 锁定为设计值：中性浅灰 #EEF1F6 @ α=0x59（≈35%），
        // 打包方向由 PackAccentGradientColor（COLORREF ABGR）统一约定。
        Assert.Equal(
            BackdropService.PackAccentGradientColor(
                BackdropService.AccentTintAlpha,
                BackdropService.AccentTintR,
                BackdropService.AccentTintG,
                BackdropService.AccentTintB),
            BackdropService.AccentTintGradientColor);
    }

    [Fact]
    public void AccentTintGradientColor_IsLowAlphaTintedGray_NotPureWhite()
    {
        uint packed = BackdropService.AccentTintGradientColor;

        // α 压在约 25%–43%：磨砂可感知、又不糊死文字
        byte alpha = (byte)(packed >> 24);
        Assert.InRange(alpha, 0x40, 0x6E);

        // 禁纯白 tint（浅色主题下纯白 tint 落在白底内容前不可见，2026-10-31 真机实锤）
        Assert.NotEqual(0xFFFFFFu, packed & 0xFFFFFF);
    }
}
