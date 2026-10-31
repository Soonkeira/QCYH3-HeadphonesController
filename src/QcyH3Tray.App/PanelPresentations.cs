using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>头部状态文字的语义色调（画刷按 Token 键名解析，映射保持纯逻辑可测）。</summary>
public enum StatusTone
{
    /// <summary>已连接（Success 绿）。</summary>
    Success,

    /// <summary>进行中（同步中/连接中/重连中，Accent 蓝）。</summary>
    Info,

    /// <summary>命令健康降级（Danger 红）。</summary>
    Alert,

    /// <summary>未连接/不可达/蓝牙关闭等中性态（弱化灰）。</summary>
    Muted,
}

/// <summary>电量文字的语义色调。</summary>
public enum BatteryTone
{
    /// <summary>现值（主文字色）。</summary>
    Normal,

    /// <summary>非现值：Loading 占位、Stale 置灰、Unknown/Error 占位（弱化灰）。</summary>
    Muted,
}

/// <summary>
/// 连接状态 / 数据有效性 → UI 展示的纯映射（阶段 3a 抽出，plan.md §6"新 UI 逻辑可测部分有测试"）。
/// 文案唯一来源仍是 <see cref="AppState.StateLabel"/>（阶段 2 统一）；本类只负责色调与占位符。
/// </summary>
internal static class PanelPresentations
{
    /// <summary>九态 → 状态文字色调：已连接=Success；同步中/连接中/重连中=Info；响应异常=Alert；其余=Muted。</summary>
    public static StatusTone ToneFor(ConnectionState state) => state switch
    {
        ConnectionState.Connected => StatusTone.Success,
        ConnectionState.Degraded => StatusTone.Alert,
        ConnectionState.Syncing or ConnectionState.Connecting
            or ConnectionState.Reconnecting or ConnectionState.Scanning => StatusTone.Info,
        _ => StatusTone.Muted,   // Disconnected / Unreachable / BluetoothOff
    };

    /// <summary>状态色调 → Token 画刷键（窗口侧 SetResourceReference 使用）。</summary>
    public static string BrushKeyFor(StatusTone tone) => tone switch
    {
        StatusTone.Success => "Brush/Success",
        StatusTone.Info => "Brush/Accent",
        StatusTone.Alert => "Brush/Danger",
        _ => "Brush/Text.Tertiary",
    };

    /// <summary>
    /// 电量 validity → 文字与色调（plan.md P1-1 #2）：
    /// Available=数值现值；Loading="…"（读取进行中）；Stale=旧值置灰；
    /// Unknown/Error="--" 占位（绝不把未知/失败当现值）。
    /// </summary>
    public static (string Text, BatteryTone Tone) FormatBattery(DataValidity validity, int? percent) => validity switch
    {
        DataValidity.Available when percent is int value => ($"{value}%", BatteryTone.Normal),
        DataValidity.Stale when percent is int stale => ($"{stale}%", BatteryTone.Muted),
        DataValidity.Loading => ("…", BatteryTone.Muted),
        _ => ("--", BatteryTone.Muted),
    };

    /// <summary>电量色调 → Token 画刷键。</summary>
    public static string BrushKeyFor(BatteryTone tone) => tone switch
    {
        BatteryTone.Normal => "Brush/Text.Primary",
        _ => "Brush/Text.Tertiary",
    };
}
