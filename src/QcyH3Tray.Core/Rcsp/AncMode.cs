namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// ANC / noise-mode raw payloads sent with the CUSTOM op-code (0xFF), ported
/// byte-for-byte from Python reference <c>h3_device.py</c> (class ANCMode).
/// <para>
/// Naming follows the semantics documented in the Python source comments rather
/// than its (misleading) constant names: Python <c>TRANSPARENCY</c> is ANC-off
/// ("ANC desligado") and Python <c>NORMAL</c> is transparency ("Transparência").
/// </para>
/// <para>
/// 载荷语义：<c>17 03</c> 前缀后为 (sub, group, level) —— sub 0x02=关闭、0x03=通透；
/// sub 0x01 时 group 0x01=降噪 / 0x02=通勤 / 0x03=室内 / 0x04=抗风噪 / 0x05=自适应，
/// level 0/1/2 = 低/中/高。<see cref="Decode"/> 按此完整解码（含场景族）；
/// <see cref="FromParams"/> 保留旧 UI 六模式整数索引语义作薄适配。
/// </para>
/// </summary>
public static class AncMode
{
    public static readonly byte[] Off = [0x17, 0x03, 0x02, 0x00, 0x00];           // py TRANSPARENCY
    public static readonly byte[] Transparency = [0x17, 0x03, 0x03, 0x01, 0x04];  // py NORMAL

    public static readonly byte[] NoiseCancelLow = [0x17, 0x03, 0x01, 0x01, 0x00];      // py NOISY_LOW
    public static readonly byte[] NoiseCancelMedium = [0x17, 0x03, 0x01, 0x01, 0x01];   // py NOISY_MED
    public static readonly byte[] NoiseCancelHigh = [0x17, 0x03, 0x01, 0x01, 0x02];     // py NOISY_HIGH

    public static readonly byte[] CommutingLow = [0x17, 0x03, 0x01, 0x02, 0x00];        // py COMMUTING_LOW
    public static readonly byte[] CommutingMedium = [0x17, 0x03, 0x01, 0x02, 0x01];     // py COMMUTING_MED
    public static readonly byte[] CommutingHigh = [0x17, 0x03, 0x01, 0x02, 0x02];       // py COMMUTING_HIGH

    public static readonly byte[] IndoorLow = [0x17, 0x03, 0x01, 0x03, 0x00];           // py INDOOR_LOW
    public static readonly byte[] IndoorMedium = [0x17, 0x03, 0x01, 0x03, 0x01];        // py INDOOR_MED
    public static readonly byte[] IndoorHigh = [0x17, 0x03, 0x01, 0x03, 0x02];          // py INDOOR_HIGH

    public static readonly byte[] AntiWind = [0x17, 0x03, 0x01, 0x04, 0x00];            // py ANTIWIND
    public static readonly byte[] Adaptive = [0x17, 0x03, 0x01, 0x05, 0x00];            // py ADAPTIVE

    /// <summary>UI mode cycle, ported verbatim from Python <c>ANCMode.ALL</c>.</summary>
    public static readonly byte[][] All =
    [
        Off,
        Transparency,
        NoiseCancelLow,
        NoiseCancelMedium,
        NoiseCancelHigh,
        Adaptive,
    ];

    private static readonly string[] LevelLabels = ["低", "中", "高"];

    /// <summary>
    /// Full decode of a GET 0x17 / 17 03 push payload into
    /// <see cref="AncModeInfo"/>（家族 + 档位 + 中文显示名）。
    /// <para>
    /// 真机抓包依据（2026-10）：本机 H3 固件对 GET 0x17 实测返回
    /// <c>17 03 01 03 02</c>（室内场景·高）——场景组是真实固件行为而非推测，
    /// Python 参考实现 <c>ANCMode.from_params</c> 只是不解码它们（返回 None）。
    /// 未知 sub/group 或长度不足 5B 返回 <see langword="null"/>。
    /// </para>
    /// </summary>
    public static AncModeInfo? Decode(ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length < 5)
        {
            return null;
        }

        byte sub = parameters[2];
        byte group = parameters[3];
        byte level = parameters[4];

        return sub switch
        {
            0x02 => new AncModeInfo(AncFamily.Off, null, "关闭"),
            0x03 => new AncModeInfo(AncFamily.Transparency, null, "通透"),
            0x01 => group switch
            {
                0x01 => LeveledInfo(AncFamily.NoiseCancelling, "降噪", level),
                0x02 => LeveledInfo(AncFamily.Commuting, "通勤", level),
                0x03 => LeveledInfo(AncFamily.Indoor, "室内", level),
                0x04 => new AncModeInfo(AncFamily.AntiWind, null, "抗风噪"),
                0x05 => new AncModeInfo(AncFamily.Adaptive, null, "自适应"),
                _ => null,
            },
            _ => null,
        };
    }

    /// <inheritdoc cref="Decode(ReadOnlySpan{byte})"/>
    public static AncModeInfo? Decode(byte[] parameters) => Decode(parameters.AsSpan());

    /// <summary>降噪/通勤/室内三族带档位显示名的解码（0=低 1=中 2=高，越界档位显示未知）。</summary>
    private static AncModeInfo LeveledInfo(AncFamily family, string label, byte level)
        => new(family, level,
            level < LevelLabels.Length ? $"{label}·{LevelLabels[level]}" : $"{label}·未知({level})");

    /// <summary>
    /// 完整解码结果 → 旧 UI 六模式整数索引（0=关闭 1=通透 2..4=降噪低/中/高 5=自适应）；
    /// 场景族（通勤/室内/抗风噪）与 <see langword="null"/> 返回 <see langword="null"/>。
    /// </summary>
    public static int? ToIndex(AncModeInfo? info)
    {
        if (info is null)
        {
            return null;
        }
        return info.Family switch
        {
            AncFamily.Off => 0,
            AncFamily.Transparency => 1,
            AncFamily.NoiseCancelling when info.Level is >= 0 and <= 2 => 2 + info.Level,
            AncFamily.Adaptive => 5,
            _ => null,
        };
    }

    /// <summary>
    /// Legacy UI mode index (0-5) adapter over <see cref="Decode"/>：sub 0x02 → 0 (off),
    /// sub 0x03 → 1 (transparency), sub 0x01 + group 0x01 + level 0..2 → 2 + level
    /// (low/medium/high), sub 0x01 + group 0x05 → 5 (adaptive)。场景组（通勤/室内/抗风噪）
    /// 及其余负载返回 <see langword="null"/>（与 Python <c>ANCMode.from_params</c> 一致）。
    /// 新代码请改用 <see cref="Decode"/>。
    /// </summary>
    public static int? FromParams(ReadOnlySpan<byte> parameters) => ToIndex(Decode(parameters));

    /// <inheritdoc cref="FromParams(ReadOnlySpan{byte})"/>
    public static int? FromParams(byte[] parameters) => FromParams(parameters.AsSpan());
}
