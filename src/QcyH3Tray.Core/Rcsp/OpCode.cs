namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// RCSP op-codes, ported verbatim from Python reference <c>rcsp.py</c> (class OpCode).
/// </summary>
public static class OpCode
{
    public const byte Data = 1;
    public const byte GetTargetFeatureMap = 2;
    public const byte GetTargetInfo = 3;
    public const byte StartSpeech = 4;
    public const byte StopSpeech = 5;
    public const byte DisconnectClassicBt = 6;
    public const byte GetSysInfo = 7;
    public const byte SetSysInfo = 8;
    public const byte UpdateSysInfo = 9;
    public const byte PhoneCallRequest = 10;
    public const byte NotifyCommunicationWay = 11;
    public const byte Function = 14;
    public const byte Batch = 38;
    public const byte SetAdvInfo = 192;      // 0xC0
    public const byte GetAdvInfo = 193;      // 0xC1
    public const byte NotifyAdvInfo = 194;   // 0xC2
    public const byte SettingsMtu = 209;     // 0xD1
    public const byte RebootDevice = 231;    // 0xE7
    public const byte CustomBase = 240;      // 0xF0
    public const byte Custom = 255;          // 0xFF
}
