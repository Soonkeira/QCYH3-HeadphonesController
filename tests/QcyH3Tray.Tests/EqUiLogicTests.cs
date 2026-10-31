using QcyH3Tray.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 均衡器 UI 语义纯逻辑（阶段 3b）：<see cref="EqUiLogic"/> 的预设匹配、预设写入表构建、
/// masterGain 展示格式。锁定"只有 7 段原生通道"的契约。
/// </summary>
public class EqUiLogicTests
{
    private static EqV1Info InfoFromSevenGains(int[] gains, byte eqType = 0x02, int masterGain = 0)
        => new(
            eqType,
            masterGain,
            [.. EqV1BandMap.Frequencies.Zip(gains, (frequency, gain) => new EqV1Band(frequency, gain, 120))]);

    // ───────────────────────── 预设匹配 ─────────────────────────

    [Fact]
    public void MatchPreset_NullInfo_ReturnsNull()
        => Assert.Null(EqUiLogic.MatchPreset(null));

    [Fact]
    public void MatchPreset_FlatLine_MatchesDefault()
        => Assert.Equal((byte)EqPresets.Default, EqUiLogic.MatchPreset(InfoFromSevenGains([0, 0, 0, 0, 0, 0, 0]))!);

    [Theory]
    [InlineData((int)EqPresets.Pop)]
    [InlineData((int)EqPresets.HeavyBass)]
    [InlineData((int)EqPresets.Rock)]
    [InlineData((int)EqPresets.Soft)]
    [InlineData((int)EqPresets.Classic)]
    public void MatchPreset_PresetCurve_MatchesThatPreset(int presetId)
    {
        int[] seven = EqV1BandMap.TenToSeven(EqPresets.GetOffsets((byte)presetId));
        Assert.Equal((byte)presetId, EqUiLogic.MatchPreset(InfoFromSevenGains(seven))!);
    }

    [Fact]
    public void MatchPreset_CustomCurve_ReturnsNull()
        => Assert.Null(EqUiLogic.MatchPreset(InfoFromSevenGains([100, -50, 0, 0, 0, 0, 30])));

    [Fact]
    public void MatchPreset_TooFewBands_ReturnsNull()
        => Assert.Null(EqUiLogic.MatchPreset(new EqV1Info(0x02, 0, [new EqV1Band(55, 0, 120)])));

    // ───────────────────────── 预设写入表 ─────────────────────────

    [Fact]
    public void BuildPresetBands_WithCurrent_KeepsDeviceEqTypeMasterGainAndQ()
    {
        var current = new EqV1Info(
            0x05,
            -550,
            [.. EqV1BandMap.Frequencies.Select((frequency, i) => new EqV1Band(frequency, 0, 118 + i))]);

        (byte eqType, int masterGain, List<EqV1Band> bands) = EqUiLogic.BuildPresetBands(EqPresets.Pop, current);

        Assert.Equal((byte)0x05, eqType);   // eqType 保持读到的值
        Assert.Equal(-550, masterGain);
        Assert.Equal(7, bands.Count);
        Assert.Equal(EqV1BandMap.Frequencies, bands.Select(band => band.Frequency));
        Assert.Equal(118, bands[0].Q);
        Assert.Equal(EqV1BandMap.TenToSeven(EqPresets.GetOffsets(EqPresets.Pop)), bands.Select(band => band.Gain));
    }

    [Fact]
    public void BuildPresetBands_WithoutCurrent_UsesCoreFallbacks()
    {
        (byte eqType, int masterGain, List<EqV1Band> bands) = EqUiLogic.BuildPresetBands(EqPresets.HeavyBass, null);

        Assert.Equal(EqDetailDefaults.EqType, eqType);
        Assert.Equal(0, masterGain);
        Assert.All(bands, band => Assert.Equal(EqDetailDefaults.Q, band.Q));
    }

    // ───────────────────────── 展示格式 ─────────────────────────

    [Theory]
    [InlineData(150, "+1.5 dB")]
    [InlineData(-550, "-5.5 dB")]
    [InlineData(0, "0.0 dB")]
    public void FormatMasterGain_ShowsSignedOneDecimal(int hundredths, string expected)
        => Assert.Equal(expected, EqUiLogic.FormatMasterGain(hundredths));

    [Fact]
    public void FormatMasterGain_Unknown_ShowsPlaceholder()
        => Assert.Equal("--", EqUiLogic.FormatMasterGain(null));
}
