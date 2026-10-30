using QcyH3Tray.Core.Config;

namespace QcyH3Tray.Tests;

/// <summary>
/// <see cref="AppConfig"/> 读写测试：往返、缺失、损坏、字段缺失合并默认。
/// </summary>
public class AppConfigTests
{
    private static string TempPath(string tag)
        => Path.Combine(Path.GetTempPath(), $"qcy-h3-config-{tag}-{Guid.NewGuid():N}.json");

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        string path = TempPath("roundtrip");
        try
        {
            var config = new AppConfig { TargetAddress = 0x112233445566, TargetName = "QCY H3", AutoStart = true };
            config.Save(path);

            AppConfig loaded = AppConfig.Load(path);
            Assert.Equal((ulong?)0x112233445566, loaded.TargetAddress);
            Assert.Equal("QCY H3", loaded.TargetName);
            Assert.True(loaded.AutoStart);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        AppConfig config = AppConfig.Load(TempPath("missing"));

        Assert.Null(config.TargetAddress);
        Assert.Equal(string.Empty, config.TargetName);
        Assert.False(config.AutoStart);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        string path = TempPath("corrupt");
        File.WriteAllText(path, "{ 这不是合法 JSON !!!");
        try
        {
            AppConfig config = AppConfig.Load(path);
            Assert.Null(config.TargetAddress);
            Assert.Equal(string.Empty, config.TargetName);
            Assert.False(config.AutoStart);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PartialFile_MergesDefaults()
    {
        string path = TempPath("partial");
        File.WriteAllText(path, """{"TargetAddress": 42}""");
        try
        {
            AppConfig config = AppConfig.Load(path);
            Assert.Equal((ulong?)42, config.TargetAddress);
            Assert.Equal(string.Empty, config.TargetName);
            Assert.False(config.AutoStart);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"qcy-h3-config-dir-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "sub", "config.json");
        try
        {
            new AppConfig { TargetName = "QCY H3" }.Save(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
