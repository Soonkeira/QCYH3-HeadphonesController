namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// 电源管理（自动关机）读响应的解析结果（CUSTOM <c>FE 01 14</c> →
/// <c>[0x14][声明长度][powerOffTime u16LE][currentTime u16LE]</c>）。
/// </summary>
/// <param name="PowerOffMinutes">配置的自动关机时长，分钟（u16LE；真机基线 0xFFFF = 65535 分钟）。</param>
/// <param name="CurrentMinutes">当前已计时/剩余计时，分钟（u16LE；真机基线 0x0000）。</param>
public sealed record PowerManagerInfo(int PowerOffMinutes, int CurrentMinutes)
{
    /// <summary>
    /// 0xFFFF = 65535 分钟（约 45.5 天），按 QCY 手机 App 的语义应为「不自动关机」。
    /// </summary>
    public const int NeverOffMinutes = 0xFFFF;

    /// <summary>是否为「不自动关机」（<see cref="PowerOffMinutes"/> = 0xFFFF）。</summary>
    public bool IsNeverOff => PowerOffMinutes == NeverOffMinutes;
}

/// <summary>
/// 自动关机（电源管理）编解码（AutoOffCodec，cmd 0x14 = APK <c>CMDID_POWERMANAGER</c>），
/// 出处于三个参考仓库交叉印证，读写双向同 command ID：
/// <list type="bullet">
/// <item>读（Request Data）：<c>FE 01 14</c> → <c>[0x14][04][powerOff u16LE][current u16LE]</c>。
/// Quicky <c>internal/response/dispatch.go</c>（EventPowerManager=0x14）与
/// <c>internal/response/response.go</c> ParsePowerManager（两段 u16LE）；
/// qcy-h3s-windows <c>h3s_device.py</c> get_power_manager 及
/// <c>docs/PROTOCOLO-LEITURAS.md</c>（H3S 真机实测 <c>14 04 ff ff 00 00</c>）。
/// 本机 H3 真机（AA:BB:CC:DD:EE:FF）对 <c>FE 01 14</c> 回同一字节序列，逐字节吻合。</item>
/// <item>写：<c>14 04 [timeLo][timeHi][00 00]</c>，时长 u16LE、单位分钟。
/// Quicky <c>internal/command/BookingReboot.go</c> NewBookingRebootCommand（0x14 + 两段 u16LE）、
/// <c>docs/protocol.md</c>「0x14 — Power Manager (Auto Power-Off)」Send 格式（末 2 字节保留 0x00）；
/// qcy-h3s-windows <c>h3s_device.py</c> set_power_manager（<c>14 04 low high 00 00</c>）。</item>
/// </list>
/// </summary>
public static class PowerManager
{
    /// <summary>电源管理 command ID（读 = FE 01 14，写 = 14 04 …，同 ID）。</summary>
    public const byte Cmd = 0x14;

    /// <summary>数据区长度：powerOff u16LE + current u16LE。</summary>
    public const int DataLength = 4;

    /// <summary>
    /// 解析 CUSTOM 响应 params（含 <c>0x14</c> 回声与声明长度字节）。
    /// 守卫：回声必须为 0x14，且 <c>min(声明长度, 实际长度) ≥ 4</c>，
    /// 否则返回 <see langword="null"/>（不抛异常）。
    /// </summary>
    public static PowerManagerInfo? ParseResponse(byte[] parameters)
    {
        if (parameters.Length < 2 || parameters[0] != Cmd)
        {
            return null;
        }

        int declaredLength = parameters[1];
        int available = Math.Min(declaredLength, parameters.Length - 2);
        if (available < DataLength)
        {
            return null;
        }

        int powerOff = (ushort)(parameters[2] | (parameters[3] << 8));
        int current = (ushort)(parameters[4] | (parameters[5] << 8));
        return new PowerManagerInfo(powerOff, current);
    }

    /// <summary>
    /// 构建 0x14 写入载荷（<c>14 04 [timeLo][timeHi][curLo][curHi]</c>），与 Quicky
    /// NewBookingRebootCommand / qcy-h3s set_power_manager 逐字节一致。
    /// 原样回写用：把 <see cref="ParseResponse"/> 读到的两个字段原样传入即得与读响应逐字节相同的载荷。
    /// </summary>
    public static byte[] BuildWritePayload(int powerOffMinutes, int currentMinutes)
    {
        if (powerOffMinutes is < 0 or > PowerManagerInfo.NeverOffMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(powerOffMinutes), powerOffMinutes, "auto-off minutes out of u16 range.");
        }
        if (currentMinutes is < 0 or > PowerManagerInfo.NeverOffMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(currentMinutes), currentMinutes, "current minutes out of u16 range.");
        }

        return
        [
            Cmd,
            DataLength,
            (byte)(powerOffMinutes & 0xFF),
            (byte)((powerOffMinutes >> 8) & 0xFF),
            (byte)(currentMinutes & 0xFF),
            (byte)((currentMinutes >> 8) & 0xFF),
        ];
    }

    /// <summary>一条自动关机配置的中文解读（探针输出用）。</summary>
    public static string Describe(PowerManagerInfo info)
    {
        string powerOff = info.IsNeverOff
            ? $"0xFFFF（65535 分钟 ≈ 45.5 天，App 语义应为「不自动关机」）"
            : $"{info.PowerOffMinutes} 分钟";
        return $"自动关机时长 = {powerOff}；当前计时 = {info.CurrentMinutes} 分钟";
    }
}
