using System.Text.Json;
using System.Text.Json.Serialization;

namespace QcyH3Tray.Core.Config;

/// <summary>
/// 用户配置，存放于 <c>%APPDATA%\QcyH3Controller\config.json</c>。
/// 文件缺失、字段缺失或损坏时回退默认值（字段级默认 = 实例属性的初始值）。
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>目标设备地址；null = 尚未选择设备。</summary>
    public ulong? TargetAddress { get; set; }

    /// <summary>目标设备名（地址匹配失败时的兜底匹配）。</summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>开机自启（阶段 3 托盘应用预留）。</summary>
    public bool AutoStart { get; set; }

    // ───────────────────────── 阶段 3b：控制中心窗口（向后兼容默认值） ─────────────────────────

    /// <summary>控制中心窗口宽度（DIP）。缺失/损坏时回退 760。</summary>
    public double ControlCenterWidth { get; set; } = 760;

    /// <summary>控制中心窗口高度（DIP）。缺失/损坏时回退 600。</summary>
    public double ControlCenterHeight { get; set; } = 600;

    /// <summary>控制中心窗口 X 坐标（DIP）；null = 未记录（居中显示）。</summary>
    public double? ControlCenterLeft { get; set; }

    /// <summary>控制中心窗口 Y 坐标（DIP）；null = 未记录（居中显示）。</summary>
    public double? ControlCenterTop { get; set; }

    /// <summary>首次连接耳机成功的时间（本地时间）；null = 从未连接成功。</summary>
    public DateTimeOffset? FirstConnectedAt { get; set; }

    // ───────────────────────── 阶段 6：桌面通知（向后兼容默认值） ─────────────────────────

    /// <summary>桌面通知开关（托盘气泡，Win10+ 呈现为 toast；低电量提醒等）。缺失时回退 true。</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// 低电量提醒阈值（%）。可选档位 10/15/20/30（默认 20）；
    /// 非法值在消费端（<c>LowBatteryAdvisor.ClampThreshold</c>）回退默认，不在此处校验。
    /// </summary>
    public int LowBatteryThresholdPercent { get; set; } = 20;

    /// <summary>默认配置文件完整路径。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QcyH3Controller",
            "config.json");

    public static AppConfig Load() => Load(DefaultPath);

    /// <summary>读取配置；文件不存在或解析失败（损坏）时返回默认实例。</summary>
    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppConfig();
            }
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 损坏/不可读：容忍并回退默认
            return new AppConfig();
        }
    }

    public void Save() => Save(DefaultPath);

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
