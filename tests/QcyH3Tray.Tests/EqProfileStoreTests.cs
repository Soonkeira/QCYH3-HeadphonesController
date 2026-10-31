using QcyH3Tray.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// EQ 本地配置（阶段 3b）：JSON 往返、损坏容错、目录列表、删除、导入导出路径校验、
/// 文件名净化、设备单位 ↔ 滑块 dB 转换。
/// </summary>
public class EqProfileStoreTests
{
    private static string TempDirectory(string tag)
        => Path.Combine(Path.GetTempPath(), $"qcy-h3-eq-profiles-{tag}-{Guid.NewGuid():N}");

    private static EqProfile SampleProfile(string name = "我的曲线") => new()
    {
        Name = name,
        EqType = 0x02,
        MasterGain = -550,
        Bands =
        [
            new EqProfileBand(55, 0, 120),
            new EqProfileBand(220, 150, 118),
            new EqProfileBand(500, -200, 120),
            new EqProfileBand(1000, 300, 121),
            new EqProfileBand(1800, 0, 120),
            new EqProfileBand(2800, -100, 120),
            new EqProfileBand(7500, 200, 119),
        ],
        SavedUtc = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(8)),
    };

    // ───────────────────────── 保存 / 读取往返 ─────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        string directory = TempDirectory("roundtrip");
        try
        {
            var store = new EqProfileStore(directory);
            EqProfile original = SampleProfile();

            string path = store.Save(original);

            Assert.True(File.Exists(path));
            EqProfile loaded = store.Load("我的曲线")!;
            Assert.Equal(original.Name, loaded.Name);
            Assert.Equal(original.EqType, loaded.EqType);
            Assert.Equal(original.MasterGain, loaded.MasterGain);
            Assert.Equal(original.SavedUtc, loaded.SavedUtc);
            Assert.Equal(7, loaded.Bands.Count);
            for (int i = 0; i < 7; i++)
            {
                Assert.Equal(original.Bands[i].Frequency, loaded.Bands[i].Frequency);
                Assert.Equal(original.Bands[i].Gain, loaded.Bands[i].Gain);
                Assert.Equal(original.Bands[i].Q, loaded.Bands[i].Q);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"qcy-h3-eq-profiles-mkdir-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "nested", "profiles");
        try
        {
            new EqProfileStore(directory).Save(SampleProfile());
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_SameName_Overwrites()
    {
        string directory = TempDirectory("overwrite");
        try
        {
            var store = new EqProfileStore(directory);
            store.Save(SampleProfile("A"));
            store.Save(SampleProfile("A") with { MasterGain = 100 });

            Assert.Single(store.ListNames());
            Assert.Equal(100, store.Load("A")!.MasterGain);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_InvalidNameCharacters_AreSanitized()
    {
        string directory = TempDirectory("sanitize");
        try
        {
            var store = new EqProfileStore(directory);
            store.Save(SampleProfile(@"a/b\c:d*e?f""g<h>i|j"));

            string[] names = [.. store.ListNames()];
            Assert.Single(names);
            Assert.DoesNotContain('/', names[0]);
            Assert.DoesNotContain('\\', names[0]);
            Assert.DoesNotContain(':', names[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SafeFileName_EmptyOrDots_FallsBackToDefault()
    {
        Assert.Equal("未命名", EqProfileStore.SafeFileName("   "));
        Assert.Equal("未命名", EqProfileStore.SafeFileName("..."));
        Assert.Equal("abc", EqProfileStore.SafeFileName("abc"));
    }

    // ───────────────────────── 容错 ─────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new EqProfileStore(TempDirectory("missing"));
        Assert.Null(store.Load("不存在"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        string directory = TempDirectory("corrupt");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "坏.json"), "{ 这不是合法 JSON !!!");
            Assert.Null(new EqProfileStore(directory).Load("坏"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ListNames_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(new EqProfileStore(TempDirectory("nodir")).ListNames());
    }

    [Fact]
    public void ListNames_OnlyJsonFiles_Sorted()
    {
        string directory = TempDirectory("list");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "b.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "a.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "c.txt"), "{}");

            Assert.Equal(["a", "b"], new EqProfileStore(directory).ListNames());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ───────────────────────── 删除 ─────────────────────────

    [Fact]
    public void Delete_ExistingFile_RemovesAndReturnsTrue()
    {
        string directory = TempDirectory("delete");
        try
        {
            var store = new EqProfileStore(directory);
            store.Save(SampleProfile("X"));
            Assert.True(store.Delete("X"));
            Assert.Empty(store.ListNames());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Delete_MissingFile_ReturnsFalse()
    {
        var store = new EqProfileStore(TempDirectory("deletemissing"));
        Assert.False(store.Delete("不存在"));
    }

    // ───────────────────────── 导入 / 导出 ─────────────────────────

    [Fact]
    public void ExportThenImport_RoundTripsViaArbitraryPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qcy-h3-eq-export-{Guid.NewGuid():N}.json");
        try
        {
            EqProfile original = SampleProfile("导出件");
            EqProfileStore.Export(path, original);

            EqProfile? imported = EqProfileStore.Import(path);
            Assert.NotNull(imported);
            Assert.Equal(original.MasterGain, imported.MasterGain);
            Assert.Equal(original.Bands.Count, imported.Bands.Count);
            Assert.Equal(original.Bands[3].Gain, imported.Bands[3].Gain);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_CreatesMissingParentDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"qcy-h3-eq-export-dir-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "sub", "x.json");
        try
        {
            EqProfileStore.Export(path, SampleProfile());
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_NonJsonExtension_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qcy-h3-eq-import-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, """{"Name":"x","Bands":[{"Frequency":55,"Gain":0,"Q":120}]}""");
            Assert.Null(EqProfileStore.Import(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_MissingFile_ReturnsNull()
    {
        Assert.Null(EqProfileStore.Import(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void Import_CorruptFile_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qcy-h3-eq-import-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json at all");
            Assert.Null(EqProfileStore.Import(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_EmptyBands_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qcy-h3-eq-import-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Name":"空"}""");
            Assert.Null(EqProfileStore.Import(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ───────────────────────── 设备单位 ↔ 滑块转换 ─────────────────────────

    [Fact]
    public void Converter_RoundTripsDeviceUnits()
    {
        EqProfile profile = SampleProfile();
        (double[] gains, double master, int[] qs) = EqProfileConverter.ToEditor(profile);

        Assert.Equal(7, gains.Length);
        Assert.Equal(-5.5, master, 6);
        Assert.Equal(0, gains[0], 6);
        Assert.Equal(1.5, gains[1], 6);
        Assert.Equal(-2.0, gains[2], 6);
        Assert.Equal(1.18, qs[1] / 100.0, 6);

        EqProfile rebuilt = EqProfileConverter.FromEditor("重建", profile.EqType, gains, master, qs);
        Assert.Equal(profile.MasterGain, rebuilt.MasterGain);
        Assert.Equal(profile.Bands.Select(b => b.Gain), rebuilt.Bands.Select(b => b.Gain));
    }

    [Fact]
    public void Converter_ShortProfile_TreatsMissingBandsAsZero()
    {
        var shortProfile = new EqProfile { Name = "短", EqType = 0x02, MasterGain = 0, Bands = [new EqProfileBand(55, 100, 120)] };
        (double[] gains, _, int[] qs) = EqProfileConverter.ToEditor(shortProfile);

        Assert.Equal(7, gains.Length);
        Assert.Equal(1.0, gains[0], 6);
        Assert.Equal(0, gains[1], 6);
        Assert.All(qs, q => Assert.Equal(EqDetailDefaults.Q, q));
    }

    [Fact]
    public void Converter_FromEditor_FillsSevenNativeBands()
    {
        EqProfile profile = EqProfileConverter.FromEditor(
            "n", 0x02, [.. Enumerable.Repeat(-1.5d, 7)], 2.5, [.. Enumerable.Repeat(120, 7)]);

        Assert.Equal(7, profile.Bands.Count);
        Assert.Equal(EqV1BandMap.Frequencies, profile.Bands.Select(b => b.Frequency));
        Assert.Equal(250, profile.MasterGain);
        Assert.Equal(-150, profile.Bands[0].Gain);
        Assert.Equal(EqV1BandMap.SevenBandCount, profile.Bands.Count);
    }
}
