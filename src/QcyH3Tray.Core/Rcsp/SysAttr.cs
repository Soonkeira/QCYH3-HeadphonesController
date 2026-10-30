namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// System attribute ids (bit index in GetSysInfo/SetSysInfo masks), ported verbatim
/// from Python reference <c>rcsp.py</c> (class SysAttr).
/// </summary>
public static class SysAttr
{
    public const byte Battery = 0;
    public const byte Volume = 1;
    public const byte Eq = 4;
    public const byte HighAndBass = 11;
    public const byte EqPresetValue = 12;
    public const byte CurrentNoiseMode = 13;
    public const byte AllNoiseMode = 14;
    public const byte PhoneStatus = 15;
}
