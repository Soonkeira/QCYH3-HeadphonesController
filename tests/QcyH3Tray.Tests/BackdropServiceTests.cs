using QcyH3Tray.App.Services;
using Xunit;

namespace QcyH3Tray.Tests;

/// <summary>
/// 玻璃拟态背景板的诊断覆盖解析（BackdropService.ResolveOverride 纯函数）：
/// 环境变量 QCYH3TRAY_BACKDROP 未设置 / 空 / auto / 无法识别 → null（自动降级梯队）；
/// system|mica → SystemBackdrop；accent|acrylic → AccentBlur；opaque|off → Opaque。
/// 大小写不敏感、容忍首尾空白（真机降级对比用，键值宽松一些减少排查成本）。
/// </summary>
public class BackdropServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("unknown-value")]
    public void ResolveOverride_UnsetOrUnknown_ReturnsNull(string? environmentValue)
    {
        Assert.Null(BackdropService.ResolveOverride(environmentValue));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("SYSTEM")]
    [InlineData(" System ")]
    [InlineData("mica")]
    public void ResolveOverride_SystemValues_ForceSystemBackdrop(string environmentValue)
    {
        Assert.Equal(BackdropKind.SystemBackdrop, BackdropService.ResolveOverride(environmentValue));
    }

    [Theory]
    [InlineData("accent")]
    [InlineData("Acrylic")]
    [InlineData(" ACRYLIC ")]
    public void ResolveOverride_AccentValues_ForceAccentBlur(string environmentValue)
    {
        Assert.Equal(BackdropKind.AccentBlur, BackdropService.ResolveOverride(environmentValue));
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("OFF")]
    [InlineData(" Off ")]
    public void ResolveOverride_OpaqueValues_ForceOpaque(string environmentValue)
    {
        Assert.Equal(BackdropKind.Opaque, BackdropService.ResolveOverride(environmentValue));
    }
}
