namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// EQ band configuration (10 bands matching the QCY app), ported byte-for-byte
/// from Python reference <c>h3_device.py</c> (EQ_BANDS_FREQS / EQ_BAND_LABELS /
/// EQ_BAND_DEFAULTS).
/// </summary>
public static class EqBands
{
    public const int BandCount = 10;

    /// <summary>Bytes per band record on the wire: freq u16LE + gain i16LE + refGain u16LE + Q u8.</summary>
    public const int BytesPerBand = 7;

    /// <summary>Standard band center frequencies in Hz (wire order, u16 little-endian on the wire).</summary>
    public static readonly ushort[] Frequencies =
    [
        0x0037, // 55
        0x00DC, // 220
        0x01F4, // 500
        0x03E8, // 1000
        0x0708, // 1800
        0x0AF0, // 2800
        0x1194, // 4500
        0x1D4C, // 7500
        0x2710, // 10000
        0x55F0, // 22000
    ];

    public static readonly string[] Labels =
    [
        "55 Hz",
        "220 Hz",
        "500 Hz",
        "1 kHz",
        "1.8 kHz",
        "2.8 kHz",
        "4.5 kHz",
        "7.5 kHz",
        "10 kHz",
        "22 kHz",
    ];

    /// <summary>
    /// (RefGain, Q) per band, sourced in the Python reference from device GET
    /// responses and an HCI capture (EQ_BAND_DEFAULTS). Index-aligned with
    /// <see cref="Frequencies"/>.
    /// </summary>
    public static readonly (ushort RefGain, byte Q)[] Defaults =
    [
        (40, 2),   // 55 Hz
        (120, 2),  // 220 Hz
        (150, 2),  // 500 Hz
        (150, 2),  // 1 kHz
        (120, 2),  // 1.8 kHz
        (80, 2),   // 2.8 kHz
        (130, 2),  // 4.5 kHz (Q=2 entry from GET)
        (120, 2),  // 7.5 kHz
        (120, 2),  // 10 kHz
        (80, 2),   // 22 kHz (extrapolated)
    ];
}
