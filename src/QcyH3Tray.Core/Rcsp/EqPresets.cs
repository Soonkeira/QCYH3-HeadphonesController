namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// EQ preset ids, names and default band offsets, ported byte-for-byte from
/// Python reference <c>h3_device.py</c> (EQ_PRESETS / EQ_PRESET_OFFSETS).
/// Offsets are 1/100-dB units, 10 bands each.
/// </summary>
public static class EqPresets
{
    public const byte Custom = 0x00;
    public const byte Default = 0x01;
    public const byte Pop = 0x02;
    public const byte HeavyBass = 0x03;
    public const byte Rock = 0x04;
    public const byte Soft = 0x05;
    public const byte Classic = 0x06;

    /// <summary>Preset display names, ported verbatim from Python EQ_PRESETS.</summary>
    public static readonly string[] Names = ["自定义", "默认", "流行", "重低音", "摇滚", "柔和", "古典"];

    /// <summary>Preset ids, ported verbatim from Python EQ_PRESET_IDS.</summary>
    public static readonly byte[] Ids = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

    /// <summary>
    /// Default band values per preset in 1/100-dB units (10 bands), ported
    /// verbatim from Python EQ_PRESET_OFFSETS (custom starts flat).
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, int[]> Offsets = new Dictionary<byte, int[]>
    {
        [0x00] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],                            // Custom
        [0x01] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],                            // Default
        [0x02] = [0, 150, 150, 100, 0, 100, 200, 200, 150, 100],            // Pop
        [0x03] = [500, 400, 200, 0, -100, 0, 0, 100, 200, 100],             // Heavy Bass
        [0x04] = [400, 300, 0, -200, -100, 0, 200, 300, 400, 200],          // Rock
        [0x05] = [100, 100, 100, 100, 100, 100, 100, 100, 100, 100],        // Soft
        [0x06] = [0, 0, 100, 300, 300, 300, 200, 100, 100, 0],              // Classic
    };

    /// <summary>Returns a defensive copy of the 10 band offsets for <paramref name="presetId"/>.</summary>
    /// <exception cref="KeyNotFoundException">Unknown preset id.</exception>
    public static int[] GetOffsets(byte presetId)
    {
        int[] offsets = Offsets[presetId];
        return (int[])offsets.Clone();
    }
}
