namespace QcyH3Tray.Core.Rcsp;

/// <summary>
/// 降噪模式家族。Off / Transparency / NoiseCancelling / Adaptive 是 UI 六模式的基础族；
/// Commuting（通勤）/ Indoor（室内）/ AntiWind（抗风噪）为场景族——真机抓包（2026-10）
/// 证实 H3 固件（至少本机）会在 GET 0x17 响应里返回场景组负载，故作为一等家族解码。
/// </summary>
public enum AncFamily
{
    Off,
    Transparency,
    NoiseCancelling,
    Commuting,
    Indoor,
    AntiWind,
    Adaptive,
}

/// <summary>
/// 降噪模式完整解码结果（<see cref="AncMode.Decode"/> 的返回值）。
/// </summary>
/// <param name="Family">模式家族（关闭/通透/降噪/通勤/室内/抗风噪/自适应）。</param>
/// <param name="Level">档位，0=低 1=中 2=高；无档位的模式（关闭/通透/抗风噪/自适应）为 null。</param>
/// <param name="DisplayName">中文显示名，如「降噪·中」「室内·高」「抗风噪」。</param>
public sealed record AncModeInfo(AncFamily Family, int? Level, string DisplayName);
