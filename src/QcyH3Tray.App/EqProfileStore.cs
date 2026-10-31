using System.IO;
using System.Text.Json;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>EQ 配置文件中的单段（设备单位：freq Hz / gain 1/100-dB / Q×100）。</summary>
public sealed record EqProfileBand(ushort Frequency, int Gain, int Q);

/// <summary>
/// EQ 编辑器的本地命名配置（<c>%APPDATA%\QcyH3Controller\eq-profiles\*.json</c>）。
/// 刻意按设备单位（1/100-dB / Q×100）存整数，写回耳机零换算损失；只有 7 段原生通道
/// （freq/gain/Q + eqType + masterGain + 保存时间），无任何十段伪装字段。
/// </summary>
public sealed record EqProfile
{
    /// <summary>配置显示名（与文件名 stem 一致，导入的外部文件可能不同）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>EQ 预设索引（读到的值原样保留；真机基线 0x02）。</summary>
    public byte EqType { get; init; }

    /// <summary>总增益，1/100-dB。</summary>
    public int MasterGain { get; init; }

    /// <summary>7 段原生通道（频率/增益/Q，设备单位）。</summary>
    public IReadOnlyList<EqProfileBand> Bands { get; init; } = [];

    /// <summary>保存时间（本地时区偏移保留）。</summary>
    public DateTimeOffset? SavedUtc { get; init; }
}

/// <summary>
/// EQ 本地配置存储：JSON 读写 + 目录管理。文件缺失 / 损坏 / 不可读一律返回
/// <see langword="null"/>（与 <see cref="QcyH3Tray.Core.Config.AppConfig"/> 同款容错语义），
/// 不抛异常不打断编辑流程。目录可注入以便测试。
/// </summary>
internal sealed class EqProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>默认配置目录（%APPDATA%\QcyH3Controller\eq-profiles）。</summary>
    public static string ProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QcyH3Controller",
        "eq-profiles");

    private readonly string _directory;

    public EqProfileStore(string? directory = null)
        => _directory = directory ?? ProfilesDirectory;

    /// <summary>列出全部配置名（文件 stem，按名称排序）；目录不存在 → 空列表。</summary>
    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }
        return [.. Directory.GetFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>把当前曲线保存为命名配置（同名覆盖）。返回写入的完整路径。</summary>
    public string Save(EqProfile profile)
    {
        string fileName = SafeFileName(string.IsNullOrWhiteSpace(profile.Name) ? "未命名" : profile.Name);
        string path = Path.Combine(_directory, fileName + ".json");
        Write(path, profile);
        return path;
    }

    /// <summary>按名读取；缺失/损坏/不可读 → <see langword="null"/>。</summary>
    public EqProfile? Load(string name)
    {
        string path = Path.Combine(_directory, SafeFileName(name) + ".json");
        return Read(path);
    }

    /// <summary>删除命名配置；文件不存在返回 false（其余 IO 异常吞掉记日志）。</summary>
    public bool Delete(string name)
    {
        try
        {
            string path = Path.Combine(_directory, SafeFileName(name) + ".json");
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException)
        {
            ErrorLog.Write($"删除 EQ 配置「{name}」失败", exception);
            return false;
        }
    }

    /// <summary>导出到用户指定路径（SaveFileDialog 结果；父目录自动创建）。</summary>
    public static void Export(string path, EqProfile profile)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        Write(path, profile);
    }

    /// <summary>
    /// 从用户指定路径导入（OpenFileDialog 结果）。路径校验：文件必须存在且扩展名为 .json；
    /// JSON 损坏 / 段数为 0 → <see langword="null"/>（调用方提示，不抛异常）。
    /// </summary>
    public static EqProfile? Import(string path)
    {
        try
        {
            if (Path.GetExtension(path) is not (".json" or ".JSON")
                || !File.Exists(path))
            {
                return null;
            }
            EqProfile? profile = JsonSerializer.Deserialize<EqProfile>(File.ReadAllText(path), JsonOptions);
            return profile is not null && profile.Bands.Count > 0 ? profile : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
        {
            ErrorLog.Write($"导入 EQ 配置失败：{path}", exception);
            return null;
        }
    }

    /// <summary>文件名净化：非法字符替换为 "_"（配置名来自用户输入）。</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char character in name.Trim())
        {
            builder.Append(character == '\\' || character == '/' || Array.IndexOf(invalid, character) >= 0
                ? '_'
                : character);
        }
        string result = builder.ToString().TrimEnd('.');
        return result.Length == 0 ? "未命名" : result;
    }

    private static void Write(string path, EqProfile profile)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    private static EqProfile? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<EqProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
        {
            ErrorLog.Write($"读取 EQ 配置失败：{path}", exception);
            return null;
        }
    }
}

/// <summary>EQ 编辑器 ↔ 配置档案的纯转换（设备单位 ↔ 滑块 dB 值），可单测。</summary>
internal static class EqProfileConverter
{
    /// <summary>滑块值 → 配置档案（滑块 dB 值 ×100 取整；Q 用编辑器当前 Q 表）。</summary>
    public static EqProfile FromEditor(
        string name,
        byte eqType,
        IReadOnlyList<double> gainsDb,
        double masterGainDb,
        IReadOnlyList<int> qs)
    {
        var bands = new List<EqProfileBand>(EqV1BandMap.SevenBandCount);
        for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
        {
            bands.Add(new EqProfileBand(
                Frequency: EqV1BandMap.Frequencies[i],
                Gain: ToHundredths(i < gainsDb.Count ? gainsDb[i] : 0),
                Q: i < qs.Count ? qs[i] : EqDetailDefaults.Q));
        }
        return new EqProfile
        {
            Name = name,
            EqType = eqType,
            MasterGain = ToHundredths(masterGainDb),
            Bands = bands,
            SavedUtc = DateTimeOffset.Now,
        };
    }

    /// <summary>配置档案 → 滑块值（设备单位 ÷100；缺段补 0、Q 缺省用真机基线）。</summary>
    public static (double[] GainsDb, double MasterGainDb, int[] Qs) ToEditor(EqProfile profile)
    {
        var gains = new double[EqV1BandMap.SevenBandCount];
        var qs = new int[EqV1BandMap.SevenBandCount];
        Array.Fill(qs, EqDetailDefaults.Q);
        for (int i = 0; i < profile.Bands.Count && i < EqV1BandMap.SevenBandCount; i++)
        {
            gains[i] = profile.Bands[i].Gain / 100.0;
            qs[i] = profile.Bands[i].Q;
        }
        return (gains, profile.MasterGain / 100.0, qs);
    }

    /// <summary>滑块 dB 值 → 1/100-dB 整数（协议钳 ±12.7 之前的 UI 范围是 ±12.0）。</summary>
    public static int ToHundredths(double gainDb) => (int)Math.Round(gainDb * 100);
}

/// <summary>EQ 编辑器的设备单位默认值（真机基线：Q=1.20、eqType=0x02）。</summary>
internal static class EqDetailDefaults
{
    /// <summary>未知 Q 时的默认值（Q×100；与 Quicky 实现一致）。</summary>
    public const int Q = 120;

    /// <summary>无读取时的 eqType 回退（真机基线，与 Core 既定回退一致）。</summary>
    public const byte EqType = 0x02;
}
