using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// ANC mode payload and decode tests, aligned with Python h3_device.py
/// (class ANCMode + tests/unit_test.py::test_anc_decode).
/// </summary>
public class AncModeTests
{
    // 复刻 Python tests/unit_test.py::test_anc_decode：
    // from_params 对 5 种 UI 模式载荷的映射。
    [Fact]
    public void FromParams_PythonUnitTestVectors()
    {
        Assert.Equal(0, AncMode.FromParams(new byte[] { 0x17, 0x03, 0x02, 0x00, 0x00 })); // 关闭
        Assert.Equal(1, AncMode.FromParams(new byte[] { 0x17, 0x03, 0x03, 0x01, 0x04 })); // 通透
        Assert.Equal(2, AncMode.FromParams(new byte[] { 0x17, 0x03, 0x01, 0x01, 0x00 })); // 降噪低
        Assert.Equal(4, AncMode.FromParams(new byte[] { 0x17, 0x03, 0x01, 0x01, 0x02 })); // 降噪高
        Assert.Equal(5, AncMode.FromParams(new byte[] { 0x17, 0x03, 0x01, 0x05, 0x00 })); // 自适应
    }

    [Fact]
    public void FromParams_AllWirePayloads_RoundTripToUiIndex()
    {
        Assert.Equal(0, AncMode.FromParams(AncMode.Off));
        Assert.Equal(1, AncMode.FromParams(AncMode.Transparency));
        Assert.Equal(2, AncMode.FromParams(AncMode.NoiseCancelLow));
        Assert.Equal(3, AncMode.FromParams(AncMode.NoiseCancelMedium));
        Assert.Equal(4, AncMode.FromParams(AncMode.NoiseCancelHigh));
        Assert.Equal(5, AncMode.FromParams(AncMode.Adaptive));
    }

    // 旧 UI 六模式索引适配（薄封装 over Decode+ToIndex）：通勤/室内/抗风噪场景组无索引 → null，
    // 与 Python from_params 一致（场景组的完整解码见下方 Decode_* 测试）。
    [Fact]
    public void FromParams_CommutingIndoorAntiWind_ReturnsNull()
    {
        Assert.Null(AncMode.FromParams(new byte[] { 0x17, 0x03, 0x01, 0x02, 0x01 })); // 通勤中
        Assert.Null(AncMode.FromParams(new byte[] { 0x17, 0x03, 0x01, 0x03, 0x02 })); // 室内高
        Assert.Null(AncMode.FromParams(new byte[] { 0x17, 0x03, 0x01, 0x04, 0x00 })); // 抗风噪
    }

    [Fact]
    public void FromParams_TooShort_ReturnsNull()
    {
        Assert.Null(AncMode.FromParams(new byte[] { 0x17, 0x03, 0x01 }));
        Assert.Null(AncMode.FromParams([]));
    }

    // 载荷常量逐字节对照 h3_device.py ANCMode（含 Python 有而摘要未列的
    // COMMUTING/INDOOR/ANTIWIND）。
    [Fact]
    public void Constants_GoldenBytes()
    {
        Assert.Equal(new byte[] { 0x17, 0x03, 0x02, 0x00, 0x00 }, AncMode.Off);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x03, 0x01, 0x04 }, AncMode.Transparency);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x01, 0x00 }, AncMode.NoiseCancelLow);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x01, 0x01 }, AncMode.NoiseCancelMedium);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x01, 0x02 }, AncMode.NoiseCancelHigh);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x02, 0x00 }, AncMode.CommutingLow);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x02, 0x01 }, AncMode.CommutingMedium);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x02, 0x02 }, AncMode.CommutingHigh);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x03, 0x00 }, AncMode.IndoorLow);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x03, 0x01 }, AncMode.IndoorMedium);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x03, 0x02 }, AncMode.IndoorHigh);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x04, 0x00 }, AncMode.AntiWind);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x01, 0x05, 0x00 }, AncMode.Adaptive);
    }

    // Python ANCMode.ALL = [TRANSPARENCY, NORMAL, NOISY_LOW, NOISY_MED, NOISY_HIGH, ADAPTIVE]。
    [Fact]
    public void All_MatchesPythonCycle()
    {
        Assert.Equal(
            new[]
            {
                AncMode.Off,
                AncMode.Transparency,
                AncMode.NoiseCancelLow,
                AncMode.NoiseCancelMedium,
                AncMode.NoiseCancelHigh,
                AncMode.Adaptive,
            },
            AncMode.All);
    }

    // ───────────────────────── 完整解码（AncMode.Decode，真机 2026-10 抓包） ─────────────────────────

    // 真机抓包（2026-10）：GET 0x17 响应 17 03 01 03 02 → 场景组「室内·高」。
    // Python from_params 对此返回 None，完整解码必须给出 家族=室内 / 档位=高。
    [Fact]
    public void Decode_RealDeviceCapture_IndoorHigh()
    {
        AncModeInfo? info = AncMode.Decode(new byte[] { 0x17, 0x03, 0x01, 0x03, 0x02 });
        Assert.NotNull(info);
        Assert.Equal(AncFamily.Indoor, info.Family);
        Assert.Equal(2, info.Level);
        Assert.Equal("室内·高", info.DisplayName);
    }

    // 全部 13 个线上载荷常量的完整解码：家族 / 档位 / 中文显示名逐一对照。
    [Fact]
    public void Decode_AllWirePayloads_FamilyLevelAndDisplayName()
    {
        (byte[] Payload, AncFamily Family, int? Level, string Name)[] expected =
        [
            (AncMode.Off, AncFamily.Off, null, "关闭"),
            (AncMode.Transparency, AncFamily.Transparency, null, "通透"),
            (AncMode.NoiseCancelLow, AncFamily.NoiseCancelling, 0, "降噪·低"),
            (AncMode.NoiseCancelMedium, AncFamily.NoiseCancelling, 1, "降噪·中"),
            (AncMode.NoiseCancelHigh, AncFamily.NoiseCancelling, 2, "降噪·高"),
            (AncMode.CommutingLow, AncFamily.Commuting, 0, "通勤·低"),
            (AncMode.CommutingMedium, AncFamily.Commuting, 1, "通勤·中"),
            (AncMode.CommutingHigh, AncFamily.Commuting, 2, "通勤·高"),
            (AncMode.IndoorLow, AncFamily.Indoor, 0, "室内·低"),
            (AncMode.IndoorMedium, AncFamily.Indoor, 1, "室内·中"),
            (AncMode.IndoorHigh, AncFamily.Indoor, 2, "室内·高"),
            (AncMode.AntiWind, AncFamily.AntiWind, null, "抗风噪"),
            (AncMode.Adaptive, AncFamily.Adaptive, null, "自适应"),
        ];
        foreach (var (payload, family, level, name) in expected)
        {
            AncModeInfo? info = AncMode.Decode(payload);
            Assert.True(info is not null, $"载荷 {Convert.ToHexString(payload)} 应可解码");
            Assert.Equal(family, info.Family);
            Assert.Equal(level, info.Level);
            Assert.Equal(name, info.DisplayName);
        }
    }

    // 非法负载 → null：长度不足、未知 sub、未知 group。
    [Fact]
    public void Decode_IllegalPayloads_ReturnNull()
    {
        Assert.Null(AncMode.Decode(new byte[] { 0x17, 0x03, 0x01 }));             // 少于 5B
        Assert.Null(AncMode.Decode([]));                                          // 空
        Assert.Null(AncMode.Decode(new byte[] { 0x17, 0x03, 0x09, 0x01, 0x00 })); // 未知 sub
        Assert.Null(AncMode.Decode(new byte[] { 0x17, 0x03, 0x01, 0x06, 0x00 })); // 未知 group
    }

    // ToIndex：基础六模式 → 0..5；场景族与 null → null（旧索引语义的单一实现点）。
    [Fact]
    public void ToIndex_BasicModesAndScenes()
    {
        Assert.Equal(0, AncMode.ToIndex(AncMode.Decode(AncMode.Off)));
        Assert.Equal(1, AncMode.ToIndex(AncMode.Decode(AncMode.Transparency)));
        Assert.Equal(2, AncMode.ToIndex(AncMode.Decode(AncMode.NoiseCancelLow)));
        Assert.Equal(3, AncMode.ToIndex(AncMode.Decode(AncMode.NoiseCancelMedium)));
        Assert.Equal(4, AncMode.ToIndex(AncMode.Decode(AncMode.NoiseCancelHigh)));
        Assert.Equal(5, AncMode.ToIndex(AncMode.Decode(AncMode.Adaptive)));
        Assert.Null(AncMode.ToIndex(AncMode.Decode(AncMode.CommutingLow)));
        Assert.Null(AncMode.ToIndex(AncMode.Decode(AncMode.IndoorHigh)));
        Assert.Null(AncMode.ToIndex(AncMode.Decode(AncMode.AntiWind)));
        Assert.Null(AncMode.ToIndex(null));
    }

    // FromParams（旧索引适配）与 Decode+ToIndex 逐载荷等价（防两套实现漂移）。
    [Fact]
    public void FromParams_MatchesDecodeToIndex_ForEveryConstant()
    {
        foreach (var payload in new[]
                 {
                     AncMode.Off, AncMode.Transparency, AncMode.NoiseCancelLow, AncMode.NoiseCancelMedium,
                     AncMode.NoiseCancelHigh, AncMode.CommutingLow, AncMode.CommutingMedium, AncMode.CommutingHigh,
                     AncMode.IndoorLow, AncMode.IndoorMedium, AncMode.IndoorHigh, AncMode.AntiWind, AncMode.Adaptive,
                 })
        {
            Assert.Equal(AncMode.ToIndex(AncMode.Decode(payload)), AncMode.FromParams(payload));
        }
    }
}
