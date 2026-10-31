using QcyH3Tray.App;
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

    // ───────────────────────── 阶段 3b 新字段：控制中心窗口 / 首次连接时间 ─────────────────────────

    [Fact]
    public void ControlCenterFields_RoundTrip()
    {
        string path = TempPath("cc-roundtrip");
        try
        {
            var config = new AppConfig
            {
                ControlCenterWidth = 900,
                ControlCenterHeight = 650,
                ControlCenterLeft = 120.5,
                ControlCenterTop = 80,
                FirstConnectedAt = new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.FromHours(8)),
            };
            config.Save(path);

            AppConfig loaded = AppConfig.Load(path);
            Assert.Equal(900, loaded.ControlCenterWidth);
            Assert.Equal(650, loaded.ControlCenterHeight);
            Assert.Equal(120.5, loaded.ControlCenterLeft);
            Assert.Equal(80, loaded.ControlCenterTop);
            Assert.Equal(new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.FromHours(8)), loaded.FirstConnectedAt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ControlCenterFields_MissingInFile_MergeDefaults()
    {
        string path = TempPath("cc-partial");
        try
        {
            // 旧版本配置（无阶段 3b 字段）：窗口尺寸回默认值，功能不受损
            File.WriteAllText(path, """{"TargetAddress": 42, "TargetName": "QCY H3"}""");

            AppConfig loaded = AppConfig.Load(path);
            Assert.Equal(760, loaded.ControlCenterWidth);
            Assert.Equal(600, loaded.ControlCenterHeight);
            Assert.Null(loaded.ControlCenterLeft);
            Assert.Null(loaded.ControlCenterTop);
            Assert.Null(loaded.FirstConnectedAt);
            Assert.Equal((ulong?)42, loaded.TargetAddress);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ───────────────────────── 阶段 6 新字段：通知开关 / 低电量阈值 ─────────────────────────

    [Fact]
    public void NotificationFields_RoundTrip()
    {
        string path = TempPath("notify-roundtrip");
        try
        {
            var config = new AppConfig
            {
                NotificationsEnabled = false,
                LowBatteryThresholdPercent = 30,
            };
            config.Save(path);

            AppConfig loaded = AppConfig.Load(path);
            Assert.False(loaded.NotificationsEnabled);
            Assert.Equal(30, loaded.LowBatteryThresholdPercent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NotificationFields_MissingInFile_MergeDefaults()
    {
        string path = TempPath("notify-partial");
        try
        {
            // 旧版本配置（无阶段 6 字段）：通知默认开、阈值默认 20%，功能不受损
            File.WriteAllText(path, """{"TargetAddress": 42, "TargetName": "QCY H3"}""");

            AppConfig loaded = AppConfig.Load(path);
            Assert.True(loaded.NotificationsEnabled);
            Assert.Equal(20, loaded.LowBatteryThresholdPercent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NotificationFields_DefaultsMatchAdvisor()
    {
        var config = new AppConfig();
        Assert.True(config.NotificationsEnabled);
        Assert.Equal(
            LowBatteryAdvisor.DefaultThresholdPercent,
            LowBatteryAdvisor.ClampThreshold(config.LowBatteryThresholdPercent));
    }
}
