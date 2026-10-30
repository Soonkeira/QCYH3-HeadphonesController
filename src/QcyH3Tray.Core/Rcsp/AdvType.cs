namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// ADV info attribute types, ported verbatim from Python reference <c>rcsp.py</c> (class AdvType).
/// Used as bit index in GetAdvInfo masks and as TLV type byte in ADV responses.
/// </summary>
public static class AdvType
{
    public const byte BatteryQuantity = 0;
    public const byte DeviceName = 1;
    public const byte KeySettings = 2;
    public const byte LedSettings = 3;
    public const byte MicChannelSettings = 4;
    public const byte WorkMode = 5;
    public const byte ProductMessage = 6;
    public const byte ConnectedTime = 7;
    public const byte InEarCheck = 8;
    public const byte Language = 9;
    public const byte AncModeList = 10;
    public const byte CurrentNoiseMode = 11;

    /// <summary>
    /// Ported verbatim from Python reference <c>rcsp.py</c> (dict ADV_TYPE_NAMES);
    /// index 12 exists there as a display-only name.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, string> Names = new Dictionary<byte, string>
    {
        [0] = "BATTERY_QUANTITY",
        [1] = "DEVICE_NAME",
        [2] = "KEY_SETTINGS",
        [3] = "LED_SETTINGS",
        [4] = "MIC_CHANNEL_SETTINGS",
        [5] = "WORK_MODE",
        [6] = "PRODUCT_MESSAGE",
        [7] = "CONNECTED_TIME",
        [8] = "IN_EAR_CHECK",
        [9] = "LANGUAGE",
        [10] = "ANC_MODE_LIST",
        [11] = "CURRENT_NOISE_MODE",
        [12] = "VOLUME_BALANCE_OR_UNK",
    };
}
