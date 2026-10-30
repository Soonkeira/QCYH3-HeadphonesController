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
