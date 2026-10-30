namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// System function ids for GetSysInfo/SetSysInfo, ported verbatim from Python
/// reference <c>rcsp.py</c> (class SysFunc).
/// </summary>
public static class SysFunc
{
    public const byte Public = 0xFF;
    public const byte Bt = 0;
    public const byte Music = 1;
    public const byte Rtc = 2;
    public const byte Aux = 3;
    public const byte Fm = 4;
    public const byte Light = 5;
    public const byte Eq = 7;
    public const byte LowPower = 22;
}
