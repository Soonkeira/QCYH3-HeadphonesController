using QcyH3Tray.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 降噪模式 UI 语义纯逻辑（阶段 3b）：<see cref="NoiseModeLogic"/> 的家族 → 分段映射、
/// 档位写入字节/名称（与 Python 参考实现逐字节一致）、档位家族解析、模式说明。
/// </summary>
public class NoiseModeLogicTests
{
    // ───────────────────────── 分段映射 ─────────────────────────

    [Theory]
    [InlineData(AncFamily.Off, 0)]
    [InlineData(AncFamily.Transparency, 1)]
    [InlineData(AncFamily.NoiseCancelling, 2)]
    [InlineData(AncFamily.Commuting, 2)]     // 场景族与降噪共用"降噪"段
    [InlineData(AncFamily.Indoor, 2)]
    [InlineData(AncFamily.AntiWind, 2)]
    [InlineData(AncFamily.Adaptive, 3)]
    public void SegmentFor_MapsAllFamilies(AncFamily family, int expected)
        => Assert.Equal(expected, NoiseModeLogic.SegmentFor(family));

    [Fact]
    public void SegmentFor_UnknownFamily_ReturnsMinusOne()
    {
        Assert.Equal(-1, NoiseModeLogic.SegmentFor(null));
        Assert.Equal(-1, NoiseModeLogic.SegmentFor((AncFamily)99));
    }

    // ───────────────────────── 写入字节 / 名称 ─────────────────────────

    [Fact]
    public void ModeBytes_MatchesReferencePayloads()
    {
        Assert.Equal(AncMode.NoiseCancelLow, NoiseModeLogic.ModeBytes(AncFamily.NoiseCancelling, 0));
        Assert.Equal(AncMode.NoiseCancelMedium, NoiseModeLogic.ModeBytes(AncFamily.NoiseCancelling, 1));
        Assert.Equal(AncMode.NoiseCancelHigh, NoiseModeLogic.ModeBytes(AncFamily.NoiseCancelling, 2));
        Assert.Equal(AncMode.CommutingMedium, NoiseModeLogic.ModeBytes(AncFamily.Commuting, 1));
        Assert.Equal(AncMode.IndoorHigh, NoiseModeLogic.ModeBytes(AncFamily.Indoor, 2));
    }

    [Fact]
    public void ModeBytes_OutOfRangeLevel_ClampsToHigh()
        => Assert.Equal(AncMode.NoiseCancelHigh, NoiseModeLogic.ModeBytes(AncFamily.NoiseCancelling, 9));

    [Fact]
    public void ModeName_UsesFamilyAndLevelLabels()
    {
        Assert.Equal("降噪·低", NoiseModeLogic.ModeName(AncFamily.NoiseCancelling, 0));
        Assert.Equal("通勤·中", NoiseModeLogic.ModeName(AncFamily.Commuting, 1));
        Assert.Equal("室内·高", NoiseModeLogic.ModeName(AncFamily.Indoor, 2));
    }

    [Fact]
    public void ResolveLevelFamily_KeepsSceneFamiliesOnly()
    {
        Assert.Equal(AncFamily.Commuting, NoiseModeLogic.ResolveLevelFamily(AncFamily.Commuting));
        Assert.Equal(AncFamily.Indoor, NoiseModeLogic.ResolveLevelFamily(AncFamily.Indoor));
        Assert.Equal(AncFamily.NoiseCancelling, NoiseModeLogic.ResolveLevelFamily(AncFamily.Transparency));
        Assert.Equal(AncFamily.NoiseCancelling, NoiseModeLogic.ResolveLevelFamily(null));
    }

    // ───────────────────────── 模式说明 ─────────────────────────

    [Theory]
    [InlineData(AncFamily.Off)]
    [InlineData(AncFamily.Transparency)]
    [InlineData(AncFamily.NoiseCancelling)]
    [InlineData(AncFamily.Commuting)]
    [InlineData(AncFamily.Indoor)]
    [InlineData(AncFamily.AntiWind)]
    [InlineData(AncFamily.Adaptive)]
    public void Describe_CoversAllFamilies(AncFamily family)
        => Assert.False(string.IsNullOrWhiteSpace(NoiseModeLogic.Describe(family)));

    [Fact]
    public void Describe_UnknownFamily_HasNeutralFallback()
        => Assert.False(string.IsNullOrWhiteSpace(NoiseModeLogic.Describe(null)));
}
