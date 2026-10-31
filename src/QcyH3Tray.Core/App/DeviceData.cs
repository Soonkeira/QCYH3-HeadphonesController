using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Core.App;

/// <summary>单项设备数据的有效性。UI 按 validity 决定显示现值、占位符、置灰旧值还是错误文案。</summary>
public enum DataValidity
{
    /// <summary>从未读到过（连接建立前；或读到的值无法解码）。</summary>
    Unknown,

    /// <summary>已发起读、尚未返回（连接同步中 / 面板全量刷新进行中）。</summary>
    Loading,

    /// <summary>最近一次读成功，值为现值。</summary>
    Available,

    /// <summary>有值但链路已断开（或超过保鲜期）：显示旧值时必须置灰，不得当作现值。</summary>
    Stale,

    /// <summary>最近一次读/写失败（超时、异常、回读不符）。保留旧值但不得当现值展示。</summary>
    Error,

    /// <summary>协议确认本机不支持（如 EQ v1 读失败但 v2 回退成功——UI 消费通道不可用）。</summary>
    Unsupported,
}

/// <summary>
/// 设备数据不可变快照（电量 / 降噪模式 / EQ / 游戏模式）。任一项更新都会以整体快照
/// 抛出 <see cref="ConnectionManager.DeviceDataChanged"/>，UI 不会看到半新半旧的组合。
/// 每项带 <see cref="DataValidity"/> 与最后确认时间；值与 validity 分离：
/// 读失败时值保留旧值、validity 转 <see cref="DataValidity.Error"/>，绝不写默认值。
/// </summary>
public sealed record DeviceData
{
    // ───────────────────────── 电量 ─────────────────────────

    /// <summary>电量百分比（0-100）；null=未知。</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>电量有效性。</summary>
    public DataValidity BatteryValidity { get; init; } = DataValidity.Unknown;

    /// <summary>电量最后确认时间（读取/推送成功时刻）；null=从未确认。</summary>
    public DateTimeOffset? BatteryUpdatedUtc { get; init; }

    // ───────────────────────── 降噪模式 ─────────────────────────

    /// <summary>
    /// 降噪模式旧索引：0=关闭 1=通透 2=降噪低 3=降噪中 4=降噪高 5=自适应；
    /// 场景模式（通勤/室内/抗风噪）无索引 → null。展示用 <see cref="NoiseModeInfo"/>。
    /// </summary>
    public int? NoiseMode { get; init; }

    /// <summary>
    /// 降噪模式完整解码（家族 + 档位 + 中文显示名，含场景模式，真机抓包 2026-10）；
    /// null=未知。
    /// </summary>
    public AncModeInfo? NoiseModeInfo { get; init; }

    /// <summary>降噪模式有效性。</summary>
    public DataValidity NoiseModeValidity { get; init; } = DataValidity.Unknown;

    /// <summary>降噪模式最后确认时间；null=从未确认。</summary>
    public DateTimeOffset? NoiseModeUpdatedUtc { get; init; }

    // ───────────────────────── EQ ─────────────────────────

    /// <summary>当前 EQ 预设 ID（0x00=自定义 … 0x06=古典）；null=未知。v2 回退通道数据，UI 不消费（见 plan.md D11）。</summary>
    public byte? EqPresetId { get; init; }

    /// <summary>10 段 EQ 增益（1/100-dB）；null=未知。v2 回退通道数据，UI 不消费（见 plan.md D11）。</summary>
    public int[]? EqOffsets { get; init; }

    /// <summary>
    /// EQ v1 表快照（7 段 + masterGain + eqType，cmd 0x20）；null=未知。
    /// 本机固件 EQ 读走 v1（v2 回空载荷），UI 预设高亮/编辑器都以它为准。
    /// </summary>
    public EqV1Info? EqV1 { get; init; }

    /// <summary>EQ 有效性（以 UI 消费的 v1 通道为准；v1 失败而 v2 成功 → Unsupported）。</summary>
    public DataValidity EqValidity { get; init; } = DataValidity.Unknown;

    /// <summary>EQ 最后确认时间；null=从未确认。</summary>
    public DateTimeOffset? EqUpdatedUtc { get; init; }

    // ───────────────────────── 游戏模式 ─────────────────────────

    /// <summary>游戏模式（低延迟）；null=未知。</summary>
    public bool? GameMode { get; init; }

    /// <summary>游戏模式有效性；读到协议未知值时为 Unknown（绝不显示为关）。</summary>
    public DataValidity GameModeValidity { get; init; } = DataValidity.Unknown;

    /// <summary>游戏模式最后确认时间；null=从未确认。</summary>
    public DateTimeOffset? GameModeUpdatedUtc { get; init; }

    // ───────────────────────── 自动关机 ─────────────────────────

    /// <summary>
    /// 自动关机配置时长，分钟（cmd 0x14 读回 powerOffTime，u16LE）；
    /// 0xFFFF = 不自动关机（<see cref="PowerManagerInfo.NeverOffMinutes"/>）；null=未知。
    /// </summary>
    public int? AutoOffMinutes { get; init; }

    /// <summary>自动关机有效性。</summary>
    public DataValidity AutoOffValidity { get; init; } = DataValidity.Unknown;

    /// <summary>自动关机最后确认时间；null=从未确认。</summary>
    public DateTimeOffset? AutoOffUpdatedUtc { get; init; }

    // ───────────────────────── 按键映射 ─────────────────────────

    /// <summary>
    /// 按键映射原始表（GET_ADV_INFO type=2 三元组整表，含出厂槽 action≥0x81）；null=未知。
    /// 编辑器以它为唯一数据源（整表读 → 编辑 → 整表写，禁止部分表写）。
    /// </summary>
    public IReadOnlyList<KeyMappingEntry>? KeyMap { get; init; }

    /// <summary>按键映射表有效性。</summary>
    public DataValidity KeyMapValidity { get; init; } = DataValidity.Unknown;

    /// <summary>按键映射最后确认时间；null=从未确认。</summary>
    public DateTimeOffset? KeyMapUpdatedUtc { get; init; }

    /// <summary>
    /// 链路断开时全量失效：<see cref="DataValidity.Loading"/>/<see cref="DataValidity.Available"/>/
    /// <see cref="DataValidity.Error"/> → <see cref="DataValidity.Stale"/>；
    /// Unknown（从未读到）保持 Unknown，Unsupported（协议事实）保持 Unsupported。
    /// 值一律保留（UI 按 validity 置灰或显示占位符）。
    /// </summary>
    public DeviceData MarkAllStale()
    {
        static DataValidity Downgrade(DataValidity validity) => validity switch
        {
            DataValidity.Loading or DataValidity.Available or DataValidity.Error => DataValidity.Stale,
            _ => validity,
        };
        return this with
        {
            BatteryValidity = Downgrade(BatteryValidity),
            NoiseModeValidity = Downgrade(NoiseModeValidity),
            EqValidity = Downgrade(EqValidity),
            GameModeValidity = Downgrade(GameModeValidity),
            AutoOffValidity = Downgrade(AutoOffValidity),
            KeyMapValidity = Downgrade(KeyMapValidity),
        };
    }
}
