using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// 均衡器 UI 语义纯逻辑（阶段 3b 抽出，控制中心与 EQ 编辑器共用）：
/// v1 曲线 → 预设匹配、预设 → 7 段写入表、masterGain 展示格式。无控件依赖，可单测。
/// 铁律：只有 7 段原生通道（<see cref="EqV1BandMap"/>），UI 不做十段伪装。
/// </summary>
internal static class EqUiLogic
{
    /// <summary>预设 chip 对应的预设 ID 范围（0x01=默认 … 0x06=古典）。</summary>
    public const byte PresetChipMin = EqPresets.Default;

    public const byte PresetChipMax = EqPresets.Classic;

    /// <summary>
    /// 当前 7 段曲线与哪个预设的 10→7 映射一致；不一致返回 null（→"自定义"）。
    /// 全零平线会匹配到"默认"。未知曲线（null / 段数不足）→ null。
    /// </summary>
    public static byte? MatchPreset(EqV1Info? info)
    {
        if (info is null || info.Bands.Count < EqV1BandMap.SevenBandCount)
        {
            return null;
        }
        int[] current = [.. info.Bands.Take(EqV1BandMap.SevenBandCount).Select(band => band.Gain)];
        for (byte presetId = PresetChipMin; presetId <= PresetChipMax; presetId++)
        {
            if (EqV1BandMap.TenToSeven(EqPresets.GetOffsets(presetId)).SequenceEqual(current))
            {
                return presetId;
            }
        }
        return null;
    }

    /// <summary>
    /// 写预设 = 10 段偏移经 <see cref="EqV1BandMap.TenToSeven"/> 映射成 7 段曲线。
    /// eqType/masterGain/每段 Q 保持读到的值；无读取时用 Core 既定回退
    /// （eqType=0x02 真机基线、masterGain=0、Q=120）。
    /// </summary>
    public static (byte EqType, int MasterGain, List<EqV1Band> Bands) BuildPresetBands(
        byte presetId, EqV1Info? current)
    {
        byte eqType = current?.EqType ?? EqDetailDefaults.EqType;
        int masterGain = current?.MasterGain ?? 0;
        int[] gains = EqV1BandMap.TenToSeven(EqPresets.GetOffsets(presetId));
        var bands = new List<EqV1Band>(EqV1BandMap.SevenBandCount);
        for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
        {
            int q = current is not null && i < current.Bands.Count ? current.Bands[i].Q : EqDetailDefaults.Q;
            bands.Add(new EqV1Band(EqV1BandMap.Frequencies[i], gains[i], q));
        }
        return (eqType, masterGain, bands);
    }

    /// <summary>masterGain（1/100-dB）→ 展示文本；null → "--"（未知不得当作 0）。</summary>
    public static string FormatMasterGain(int? masterGainHundredths)
        => masterGainHundredths is int value
            ? FormatDb(value / 100.0)
            : "--";

    /// <summary>dB 值 → 带符号一位小数（"+1.5 dB" / "-3.0 dB" / "0.0 dB"）。</summary>
    public static string FormatDb(double value)
        => value.ToString("+0.0;-0.0;0.0") + " dB";
}
