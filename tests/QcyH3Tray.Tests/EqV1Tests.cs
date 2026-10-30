using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// EQ v1（cmd 0x20）读写双向测试。金样 = 真机 AA:BB:CC:DD:EE:FF 对 FE 01 20 的
/// 47 字节稳定响应（2026-10 两次读取完全一致）；写格式以 Quicky 为权威
/// （internal/command/EQ.go NewEQV1Command + internal/response/response.go ParseEQV1）。
/// </summary>
public class EqV1Tests
{
    /// <summary>真机 47B 响应 params（含 0x20 回声与声明长度 0x2D=45）。</summary>
    private static readonly byte[] DeviceResponseParams =
    [
        0x20, 0x2D, 0x02, 0xDA, 0xFD,             // 回声 + 声明长度 45 + eqType 2 + masterGain -550
        0x37, 0x00, 0xD4, 0xFE, 0x28, 0x00,       // 55 Hz,   -3.00 dB, Q0.40
        0xDC, 0x00, 0x50, 0xFB, 0x78, 0x00,       // 220 Hz, -12.00 dB, Q1.20
        0xF4, 0x01, 0x64, 0x00, 0x96, 0x00,       // 500 Hz,  +1.00 dB, Q1.50
        0xE8, 0x03, 0x9C, 0xFF, 0x96, 0x00,       // 1 kHz,   -1.00 dB, Q1.50
        0x08, 0x07, 0xC8, 0x00, 0x78, 0x00,       // 1.8 kHz, +2.00 dB, Q1.20
        0xF0, 0x0A, 0x5E, 0x01, 0x50, 0x00,       // 2.8 kHz, +3.50 dB, Q0.80
        0x4C, 0x1D, 0x2C, 0x01, 0x78, 0x00,       // 7.5 kHz, +3.00 dB, Q1.20
    ];

    // ───────────────────────── 读取解析 ─────────────────────────

    // 真机 47B 向量逐字段断言：eqType=0x02、masterGain=-550、7 段 freq/gain/Q 全对上。
    [Fact]
    public void ParseResponse_RealDevice47B_Vector()
    {
        EqV1Info? info = EqV1.ParseResponse(DeviceResponseParams);

        Assert.NotNull(info);
        Assert.Equal(0x02, info.EqType);
        Assert.Equal(-550, info.MasterGain);
        Assert.Equal(7, info.Bands.Count);
        Assert.Equal(new EqV1Band(55, -300, 40), info.Bands[0]);
        Assert.Equal(new EqV1Band(220, -1200, 120), info.Bands[1]);
        Assert.Equal(new EqV1Band(500, 100, 150), info.Bands[2]);
        Assert.Equal(new EqV1Band(1000, -100, 150), info.Bands[3]);
        Assert.Equal(new EqV1Band(1800, 200, 120), info.Bands[4]);
        Assert.Equal(new EqV1Band(2800, 350, 80), info.Bands[5]);
        Assert.Equal(new EqV1Band(7500, 300, 120), info.Bands[6]);
    }

    // 读写双向同构（Quicky NewEQV1Command 与 ParseEQV1 同一布局）：解析 → 构建写载荷
    // 必须逐字节还原真机原始 params（恢复基线"原样写回"依赖此性质）。
    [Fact]
    public void ParseThenBuild_RoundTrip_MatchesDeviceBytes()
    {
        EqV1Info info = EqV1.ParseResponse(DeviceResponseParams)!;

        byte[] payload = EqV1.BuildWritePayload(info.EqType, info.MasterGain, info.Bands);

        Assert.Equal(DeviceResponseParams, payload);
    }

    // 首字节非 0x20 → null（同 v2 的 marker 守卫）。
    [Fact]
    public void ParseResponse_WrongMarker_ReturnsNull()
    {
        Assert.Null(EqV1.ParseResponse([0x22, 0x2D, 0x02, 0xDA, 0xFD]));
    }

    // 空载荷（本机对 FE 01 22 的已知表现）与声明长度 0 → null。
    [Fact]
    public void ParseResponse_EmptyOrZeroLength_ReturnsNull()
    {
        Assert.Null(EqV1.ParseResponse([]));
        Assert.Null(EqV1.ParseResponse([0x20, 0x00]));
    }

    // 数据不足 7 段（available < 3 + 7×6 = 45）→ null，不做部分解析。
    [Fact]
    public void ParseResponse_FewerThanSevenBands_ReturnsNull()
    {
        byte[] threeBands =
        [
            0x20, 0x15, 0x02, 0x00, 0x00,             // 声明长度 21 = 3 + 3×6
            0x37, 0x00, 0xD4, 0xFE, 0x28, 0x00,
            0xDC, 0x00, 0x50, 0xFB, 0x78, 0x00,
            0xF4, 0x01, 0x64, 0x00, 0x96, 0x00,
        ];
        Assert.Null(EqV1.ParseResponse(threeBands));

        // 声明 45 但实际被截断到 30B → available 取实际值，同样不足。
        byte[] truncated = [.. DeviceResponseParams[..30]];
        Assert.Null(EqV1.ParseResponse(truncated));
    }

    // ───────────────────────── 写入构建 ─────────────────────────

    // 全零平线（eqprobe3 步骤 2 的唯一实验写入）：eqIndex/masterGain/Q 沿用真机基线，
    // masterGain=0、7 段 gain=0 → 20 2D 02 00 00 + 段数据。
    [Fact]
    public void BuildWritePayload_FlatLine_Golden()
    {
        EqV1Info baseline = EqV1.ParseResponse(DeviceResponseParams)!;
        var flatBands = baseline.Bands.Select(band => band with { Gain = 0 }).ToList();

        byte[] payload = EqV1.BuildWritePayload(baseline.EqType, masterGain: 0, flatBands);

        byte[] expected =
        [
            0x20, 0x2D, 0x02, 0x00, 0x00,
            0x37, 0x00, 0x00, 0x00, 0x28, 0x00,
            0xDC, 0x00, 0x00, 0x00, 0x78, 0x00,
            0xF4, 0x01, 0x00, 0x00, 0x96, 0x00,
            0xE8, 0x03, 0x00, 0x00, 0x96, 0x00,
            0x08, 0x07, 0x00, 0x00, 0x78, 0x00,
            0xF0, 0x0A, 0x00, 0x00, 0x50, 0x00,
            0x4C, 0x1D, 0x00, 0x00, 0x78, 0x00,
        ];
        Assert.Equal(expected, payload);
    }

    // Quicky clampGain：段增益钳到 ±1270（±12.7 dB）。
    [Fact]
    public void BuildWritePayload_ClampsGainToPlusMinus1270()
    {
        var bands = new List<EqV1Band> { new(55, 5000, 100), new(220, -9000, 100) };

        byte[] payload = EqV1.BuildWritePayload(0x00, 0, bands);

        Assert.Equal(new byte[] { 0xF6, 0x04 }, payload[7..9]);    // +1270 = 0x04F6
        Assert.Equal(new byte[] { 0x0A, 0xFB }, payload[13..15]);  // -1270 = 0xFB0A
    }

    // masterGain 超出 i16 → 抛 ArgumentOutOfRange（与 v2 SetEq 的越界处理一致）。
    [Fact]
    public void BuildWritePayload_MasterGainOutOfRange_Throws()
    {
        var bands = new List<EqV1Band> { new(55, 0, 100) };
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EqV1.BuildWritePayload(0x00, 100_000, bands));
    }

    // 空段列表 → 抛 ArgumentException（防手滑写一个无段表）。
    [Fact]
    public void BuildWritePayload_EmptyBands_Throws()
    {
        Assert.Throws<ArgumentException>(() => EqV1.BuildWritePayload(0x00, 0, []));
    }

    // ───────────────────────── 10 段 ↔ 7 段映射 ─────────────────────────

    // 流行预设（10 段）→ 7 段：丢弃 4500/10000/22000（索引 6/8/9），7.5k 取索引 7。
    [Fact]
    public void TenToSeven_Drops4500And10000And22000()
    {
        int[] pop = EqPresets.Offsets[EqPresets.Pop];   // [0,150,150,100,0,100,200,200,150,100]

        int[] seven = EqV1BandMap.TenToSeven(pop);

        Assert.Equal(new[] { 0, 150, 150, 100, 0, 100, 200 }, seven);
    }

    // 真机 7 段曲线 → 10 段：按频率回填，4500/10000/22000 补 0；再 TenToSeven 应还原。
    [Fact]
    public void SevenToTen_FillsMissingWithZero_AndRoundTrips()
    {
        int[] seven = [-300, -1200, 100, -100, 200, 350, 300];

        int[] ten = EqV1BandMap.SevenToTen(seven);

        Assert.Equal(10, ten.Length);
        Assert.Equal(new[] { -300, -1200, 100, -100, 200, 350, 0, 300, 0, 0 }, ten);
        Assert.Equal(seven, EqV1BandMap.TenToSeven(ten));
    }

    // ───────────────────────── H3Client 接线 ─────────────────────────

    // GetEqV1Async：注入真机 47B 响应 → 完整解出 7 段。
    [Fact]
    public async Task GetEqV1Async_RealDeviceVector_ParsesSevenBands()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetEqV1Async(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, DeviceResponseParams);
        EqV1Info? info = await task;

        Assert.NotNull(info);
        Assert.Equal(0x02, info.EqType);
        Assert.Equal(-550, info.MasterGain);
        Assert.Equal(new[] { 55, 220, 500, 1000, 1800, 2800, 7500 }, info.Bands.Select(b => (int)b.Frequency));
        Assert.Equal(new[] { -300, -1200, 100, -100, 200, 350, 300 }, info.Bands.Select(b => b.Gain));
    }

    // 注入本机对 FE 01 22 的"空载荷"式响应 → null（守卫生效）。
    [Fact]
    public async Task GetEqV1Async_EmptyPayload_ReturnsNull()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetEqV1Async(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, []);
        EqV1Info? info = await task;

        Assert.Null(info);
    }

    // 设备未应答 → null。
    [Fact]
    public async Task GetEqV1Async_NoResponse_ReturnsNull()
    {
        using var client = new H3Client(new FakeBleLink());
        Assert.Null(await client.GetEqV1Async(TimeSpan.FromMilliseconds(50)));
    }

    // SetEqV1Async 黄金帧：opCode 0xFF + params 47B（含 sn 01）→ 帧总长 56B。
    // FE DC BA | C0 | FF | 00 30 | 01 | 20 2D 02 DA FD + 7×6B | EF。
    [Fact]
    public async Task SetEqV1Async_RealDeviceCurve_GoldenFrame()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);
        EqV1Info baseline = EqV1.ParseResponse(DeviceResponseParams)!;

        await client.SetEqV1Async(baseline.EqType, baseline.MasterGain, baseline.Bands);

        byte[] frame = link.WrittenFrames.Single();
        Assert.Equal(56, frame.Length);
        Assert.Equal(new byte[] { 0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x30, 0x01 }, frame[..8]);
        Assert.Equal(DeviceResponseParams, frame[8..^1]);
        Assert.Equal(0xEF, frame[^1]);
    }

    /// <summary>构造并注入一个 opCode=0xFF 的响应帧（status=0），与 EqTests 同款。</summary>
    private static void PushResponse(FakeBleLink link, byte sn, byte[] parameters)
    {
        var response = new Packet(OpCode.Custom, Packet.TypeResponse, hasResponse: 0, status: 0, sn: sn, parameters: parameters);
        link.RaiseReceived(response.Encode());
    }
}
