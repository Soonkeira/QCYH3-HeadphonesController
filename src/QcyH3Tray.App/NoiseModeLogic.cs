using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// 降噪模式 UI 语义（阶段 3b 抽出，控制中心与快捷面板共用的纯逻辑部分）：
/// 家族 → 四段索引、家族+档位 → 写入字节与名称、模式一句话说明。全部无控件依赖，可单测。
/// </summary>
internal static class NoiseModeLogic
{
    public static readonly string[] LevelNames = ["低", "中", "高"];

    /// <summary>家族 → 四段分段索引：0=关闭 1=通透 2=降噪（含场景族）3=自适应；未知 → -1。</summary>
    public static int SegmentFor(AncFamily? family) => family switch
    {
        AncFamily.Off => 0,
        AncFamily.Transparency => 1,
        AncFamily.NoiseCancelling or AncFamily.Commuting or AncFamily.Indoor or AncFamily.AntiWind => 2,
        AncFamily.Adaptive => 3,
        _ => -1,
    };

    /// <summary>场景族（通勤/室内/抗风）与纯降噪共用"降噪"段。</summary>
    public static bool SharesNoiseCancellingSegment(AncFamily? family)
        => SegmentFor(family) == 2;

    /// <summary>在降噪/通勤/室内家族内调档位；其它家族点档位 → 降噪·档位。</summary>
    public static AncFamily ResolveLevelFamily(AncFamily? currentFamily)
        => currentFamily is AncFamily.Commuting or AncFamily.Indoor
            ? currentFamily.Value
            : AncFamily.NoiseCancelling;

    /// <summary>家族 + 档位 → RCSP 写入字节（与 Python 参考实现逐字节一致）。</summary>
    public static byte[] ModeBytes(AncFamily family, int level) => (family, level) switch
    {
        (AncFamily.NoiseCancelling, 0) => AncMode.NoiseCancelLow,
        (AncFamily.NoiseCancelling, 1) => AncMode.NoiseCancelMedium,
        (AncFamily.NoiseCancelling, _) => AncMode.NoiseCancelHigh,
        (AncFamily.Commuting, 0) => AncMode.CommutingLow,
        (AncFamily.Commuting, 1) => AncMode.CommutingMedium,
        (AncFamily.Commuting, _) => AncMode.CommutingHigh,
        (AncFamily.Indoor, 0) => AncMode.IndoorLow,
        (AncFamily.Indoor, 1) => AncMode.IndoorMedium,
        (AncFamily.Indoor, _) => AncMode.IndoorHigh,
        _ => AncMode.NoiseCancelMedium,
    };

    /// <summary>家族 + 档位 → 命令名（写命令日志/状态行用）。</summary>
    public static string ModeName(AncFamily family, int level) => family switch
    {
        AncFamily.NoiseCancelling => $"降噪·{LevelNames[level]}",
        AncFamily.Commuting => $"通勤·{LevelNames[level]}",
        AncFamily.Indoor => $"室内·{LevelNames[level]}",
        _ => "降噪",
    };

    /// <summary>模式一句话说明（降噪页文案；未知家族给中性提示）。</summary>
    public static string Describe(AncFamily? family) => family switch
    {
        AncFamily.Off => "关闭：不处理环境音，功耗最低。",
        AncFamily.Transparency => "通透：听见环境音，适合对话与通勤广播。",
        AncFamily.NoiseCancelling => "降噪：过滤持续性低频噪声，可调低/中/高三档。",
        AncFamily.Commuting => "通勤：针对地铁、公交等低频轰鸣优化。",
        AncFamily.Indoor => "室内：针对人声、键盘等中频噪声优化。",
        AncFamily.AntiWind => "抗风噪：抑制风噪，适合户外骑行。",
        AncFamily.Adaptive => "自适应：根据环境噪声自动调节降噪强度。",
        _ => "尚未读取到当前降噪模式。",
    };
}
