using QcyH3Tray.Core.App;

namespace QcyH3Tray.App;

/// <summary>低电量提醒判定相位：Armed=可触发；Fired=本会话已触发（等待恢复或重连重置）。</summary>
internal enum LowBatteryPhase
{
    Armed,
    Fired,
}

/// <summary>
/// 低电量提醒判定（纯函数，阶段 6）：输入当前相位 + 电量快照 + 会话状态，输出是否提醒与新相位。
/// 规则（同一连接会话内不重复触发）：
/// - 电量 <see cref="DataValidity.Available"/> 且 ≤ 阈值 → Armed 触发一次（转 Fired）；
/// - 恢复到 阈值+<see cref="HysteresisMarginPercent"/> 以上 → 重新武装（可再次触发）；
///   阈值之上但未到迟滞线 → 相位保持（避免临界值来回抖动导致反复提醒）；
/// - 会话不活跃（链路断开）→ 重置为 Armed（重新连接后可再次提醒）；
/// - 电量无效（Unknown/Stale/Error/Loading 或 null）→ 状态保持、不提醒。
/// </summary>
internal static class LowBatteryAdvisor
{
    /// <summary>迟滞余量：恢复到「阈值 + 该值」以上才重新武装（plan 派发：恢复到阈值+10 以上重置）。</summary>
    public const int HysteresisMarginPercent = 10;

    /// <summary>默认阈值（%）：20。</summary>
    public const int DefaultThresholdPercent = 20;

    /// <summary>配置可接受的最小/最大阈值（%）：设置页 ComboBox 只提供
    /// <see cref="ConfigurableThresholds"/> 四档；手改配置文件的值在
    /// [1,100] 区间内照常生效（冒烟验证以「阈值临时改 100」触发气泡），越界回默认。</summary>
    public const int MinThresholdPercent = 1;
    public const int MaxThresholdPercent = 100;

    /// <summary>可配置档位（%）：10 / 15 / 20 / 30（设置页 ComboBox 选项）。</summary>
    public static readonly int[] ConfigurableThresholds = [10, 15, 20, 30];

    /// <summary>阈值清洗：1–100 区间照常生效；越界/垃圾值回退默认 20。</summary>
    public static int ClampThreshold(int value)
        => value is >= MinThresholdPercent and <= MaxThresholdPercent ? value : DefaultThresholdPercent;

    /// <summary>
    /// 判定一次低电量提醒。
    /// </summary>
    /// <param name="phase">当前相位（上一次判定结果）。</param>
    /// <param name="thresholdPercent">提醒阈值（建议先经 <see cref="ClampThreshold"/> 清洗）。</param>
    /// <param name="validity">电量快照有效性。</param>
    /// <param name="percent">电量百分比（null=未知）。</param>
    /// <param name="sessionActive">链路是否已建立（Connecting/Syncing/Connected/Degraded）；false=会话外，重置相位。</param>
    /// <returns>是否触发提醒，以及判定后的新相位（调用方原样保存）。</returns>
    public static (bool Notify, LowBatteryPhase Phase) Evaluate(
        LowBatteryPhase phase,
        int thresholdPercent,
        DataValidity validity,
        int? percent,
        bool sessionActive)
    {
        if (!sessionActive)
        {
            return (false, LowBatteryPhase.Armed);   // 断开/未连接：重置（重新连接后可再次提醒）
        }
        if (validity != DataValidity.Available || percent is not int battery)
        {
            return (false, phase);   // 无有效电量：不做任何判定
        }
        if (battery > thresholdPercent)
        {
            return battery >= thresholdPercent + HysteresisMarginPercent
                ? (false, LowBatteryPhase.Armed)   // 迟滞恢复：重新武装
                : (false, phase);                  // 阈值之上但未过迟滞线：保持（防抖）
        }
        return phase == LowBatteryPhase.Armed
            ? (true, LowBatteryPhase.Fired)    // 触发一次
            : (false, phase);                  // 本会话已提醒过：不重复
    }
}
