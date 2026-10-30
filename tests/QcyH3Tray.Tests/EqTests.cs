using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// EQ command-layer tests, aligned with Python h3_device.py (get_eq/set_eq and
/// the EQ_* constant tables) and tests/unit_test.py::test_eq_payload/test_eq_parse.
/// </summary>
public class EqTests
{
    // ───────────────────────── 常量表转录守卫 ─────────────────────────

    // EQ_BANDS_FREQS 原样：55/220/500/1k/1.8k/2.8k/4.5k/7.5k/10k/22k Hz。
    [Fact]
    public void Bands_Frequencies_Golden()
    {
        Assert.Equal(
            new ushort[] { 0x0037, 0x00DC, 0x01F4, 0x03E8, 0x0708, 0x0AF0, 0x1194, 0x1D4C, 0x2710, 0x55F0 },
            EqBands.Frequencies);
    }

    // EQ_BAND_DEFAULTS 原样：(ref_gain, Q) = 40/120/150/150/120/80/130/120/120/80，Q 全为 2。
    [Fact]
    public void Bands_Defaults_Golden()
    {
        var expected = new (ushort RefGain, byte Q)[]
        {
            (40, 2), (120, 2), (150, 2), (150, 2), (120, 2),
            (80, 2), (130, 2), (120, 2), (120, 2), (80, 2),
        };
        Assert.Equal(expected, EqBands.Defaults);
    }

    // 复刻 Python tests/unit_test.py::test_presets_chinese + EQ_PRESET_OFFSETS 原样转录。
    [Fact]
    public void Presets_NamesIdsAndOffsets_Golden()
    {
        Assert.Equal(new[] { "自定义", "默认", "流行", "重低音", "摇滚", "柔和", "古典" }, EqPresets.Names);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, EqPresets.Ids);

        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, EqPresets.Offsets[0x00]);   // 自定义
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, EqPresets.Offsets[0x01]);   // 默认
        Assert.Equal(new[] { 0, 150, 150, 100, 0, 100, 200, 200, 150, 100 }, EqPresets.Offsets[0x02]);   // 流行
        Assert.Equal(new[] { 500, 400, 200, 0, -100, 0, 0, 100, 200, 100 }, EqPresets.Offsets[0x03]);    // 重低音
        Assert.Equal(new[] { 400, 300, 0, -200, -100, 0, 200, 300, 400, 200 }, EqPresets.Offsets[0x04]); // 摇滚
        Assert.Equal(new[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 }, EqPresets.Offsets[0x05]); // 柔和
        Assert.Equal(new[] { 0, 0, 100, 300, 300, 300, 200, 100, 100, 0 }, EqPresets.Offsets[0x06]);     // 古典
    }

    // ───────────────────────── EQ 写入 ─────────────────────────

    // EQ 写推演（h3_device.py set_eq，自定义曲线 10 段全部 +3.5dB = 350（单位 0.01dB），preset 0x00）：
    // params = 22 49 00 70 FE + 10×[freq u16LE + gain 5E 01 + refGain u16LE + Q 02]（共 75B）；
    // 数据段（22 49 之后）= 3 + 70 = 73B；body = sn(01) + 75B → paramLen=004C；帧总长 84B。
    [Fact]
    public async Task SetEq_CustomAllPlus3_5dB_GoldenFrame()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        await client.SetEqAsync(Enumerable.Repeat(350, 10).ToArray(), EqPresets.Custom);

        byte[] frame = link.WrittenFrames.Single();
        Assert.Equal(84, frame.Length);

        // 数据段长度 = 73（preset 1B + "70 FE" 2B + 10×7B 段数据），即 params[1]=0x49。
        Assert.Equal(0x22, frame[8]);
        Assert.Equal(73, frame[9]);

        byte[] expected =
        [
            0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x4C, 0x01, // 帧头 + flag + opCode + paramLen + sn
            0x22, 0x49, 0x00, 0x70, 0xFE,                   // marker + len + preset + 70 FE
            0x37, 0x00, 0x5E, 0x01, 0x28, 0x00, 0x02,       // 55 Hz, +350, ref 40, Q 2
            0xDC, 0x00, 0x5E, 0x01, 0x78, 0x00, 0x02,       // 220 Hz, ref 120
            0xF4, 0x01, 0x5E, 0x01, 0x96, 0x00, 0x02,       // 500 Hz, ref 150
            0xE8, 0x03, 0x5E, 0x01, 0x96, 0x00, 0x02,       // 1 kHz, ref 150
            0x08, 0x07, 0x5E, 0x01, 0x78, 0x00, 0x02,       // 1.8 kHz, ref 120
            0xF0, 0x0A, 0x5E, 0x01, 0x50, 0x00, 0x02,       // 2.8 kHz, ref 80
            0x94, 0x11, 0x5E, 0x01, 0x82, 0x00, 0x02,       // 4.5 kHz, ref 130
            0x4C, 0x1D, 0x5E, 0x01, 0x78, 0x00, 0x02,       // 7.5 kHz, ref 120
            0x10, 0x27, 0x5E, 0x01, 0x78, 0x00, 0x02,       // 10 kHz, ref 120
            0xF0, 0x55, 0x5E, 0x01, 0x50, 0x00, 0x02,       // 22 kHz, ref 80
            0xEF,
        ];
        Assert.Equal(expected, frame);
    }

    // 流行预设写入推演：offsets = [0,150,150,100,0,100,200,200,150,100]（EQ_PRESET_OFFSETS[0x02]），
    // 增益 u16LE：0→00 00, 150→96 00, 100→64 00, 200→C8 00。
    [Fact]
    public async Task SetEqPreset_Pop_GoldenParams()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        await client.SetEqPresetAsync(EqPresets.Pop);

        byte[] expectedParams =
        [
            0x22, 0x49, 0x02, 0x70, 0xFE,
            0x37, 0x00, 0x00, 0x00, 0x28, 0x00, 0x02,
            0xDC, 0x00, 0x96, 0x00, 0x78, 0x00, 0x02,
            0xF4, 0x01, 0x96, 0x00, 0x96, 0x00, 0x02,
            0xE8, 0x03, 0x64, 0x00, 0x96, 0x00, 0x02,
            0x08, 0x07, 0x00, 0x00, 0x78, 0x00, 0x02,
            0xF0, 0x0A, 0x64, 0x00, 0x50, 0x00, 0x02,
            0x94, 0x11, 0xC8, 0x00, 0x82, 0x00, 0x02,
            0x4C, 0x1D, 0xC8, 0x00, 0x78, 0x00, 0x02,
            0x10, 0x27, 0x96, 0x00, 0x78, 0x00, 0x02,
            0xF0, 0x55, 0x64, 0x00, 0x50, 0x00, 0x02,
        ];
        Assert.Equal(75, expectedParams.Length);
        Assert.Equal(expectedParams, link.WrittenFrames.Single()[8..^1]);
    }

    // BuildSetEqPayload（CLI eqprobe 实验 4"先写后读"复用的构造点）：
    // 默认预设 0x01 + 10 段全零偏移 → 22 49 01 70 FE + 10×7B（增益全 0，refGain/Q 取 EqBands.Defaults），
    // 与 set_eq（preset 0x02 金样）同一构造逻辑，听感无变化。
    [Fact]
    public void BuildSetEqPayload_DefaultPresetAllZero_GoldenParams()
    {
        byte[] payload = H3Client.BuildSetEqPayload(EqPresets.GetOffsets(EqPresets.Default), EqPresets.Default);
        Assert.Equal(75, payload.Length);
        Assert.Equal(new byte[] { 0x22, 0x49, 0x01, 0x70, 0xFE }, payload[..5]);
        for (int i = 0; i < EqBands.BandCount; i++)
        {
            int offset = 5 + i * EqBands.BytesPerBand;
            Assert.Equal(
                new byte[] { (byte)(EqBands.Frequencies[i] & 0xFF), (byte)(EqBands.Frequencies[i] >> 8) },
                payload[offset..(offset + 2)]);
            Assert.Equal(new byte[] { 0x00, 0x00 }, payload[(offset + 2)..(offset + 4)]);   // 增益全 0
            ushort refGain = EqBands.Defaults[i].RefGain;
            Assert.Equal(
                new byte[] { (byte)(refGain & 0xFF), (byte)(refGain >> 8) },
                payload[(offset + 4)..(offset + 6)]);
            Assert.Equal(EqBands.Defaults[i].Q, payload[offset + 6]);
        }
    }

    // Python set_eq：短列表补 0（db_offsets[i] if i < len else 0）。
    [Fact]
    public async Task SetEq_ShortList_PadsWithZero()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        await client.SetEqAsync([350]);

        byte[] frame = link.WrittenFrames.Single();
        // 第 1 段增益 350 = 5E 01，其余 9 段增益 00 00。
        Assert.Equal(new byte[] { 0x37, 0x00, 0x5E, 0x01, 0x28, 0x00, 0x02 }, frame[13..20]);
        for (int band = 1; band < 10; band++)
        {
            int offset = 13 + band * 7;
            Assert.Equal(new byte[] { 0x00, 0x00 }, frame[(offset + 2)..(offset + 4)]);
        }
    }

    // Python to_bytes(2, signed) 对越界值会抛异常；C# 对应抛 ArgumentOutOfRange。
    [Fact]
    public async Task SetEq_OffsetOutOfRange_Throws()
    {
        using var client = new H3Client(new FakeBleLink());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.SetEqAsync(new int[] { 40000, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    // ───────────────────────── EQ 读取 ─────────────────────────

    // 复刻 Python tests/unit_test.py::test_eq_parse：10 段，增益 i*50，参考增益 120，Q=2。
    // 数据段 = 00 70 FE + 10×7B = 73B = 0x4B → params = 22 4B 00 70 FE + …。
    [Fact]
    public async Task GetEq_TenBands_MatchesPythonUnitTest()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var bands = new List<byte>
        {
            0x00, 0x70, 0xFE, // Python test_eq_parse 的 data 头
        };
        for (int i = 0; i < 10; i++)
        {
            ushort freq = EqBands.Frequencies[i];
            short gain = (short)(i * 50);
            bands.AddRange(new byte[]
            {
                (byte)(freq & 0xFF), (byte)(freq >> 8),
                (byte)(gain & 0xFF), (byte)((gain >> 8) & 0xFF),
                0x78, 0x00, 0x02, // ref gain 120 + Q 2
            });
        }
        byte[] parameters = [(byte)0x22, (byte)bands.Count, .. bands];

        // 先启动请求（pending 注册完成）再注入响应，与真实收发时序一致。
        var task = client.GetEqAsync(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, parameters);
        var offsets = await task;

        Assert.NotNull(offsets);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 50), offsets);
    }

    // "重复频率后者胜"推演（Python get_eq 注释：设备响应中 4500 Hz 出现两次，最后一条即
    // 用户可调的 Q=2 拷贝；代码 bands[freq] = db_off 覆盖旧值）：
    // 构造 7 条记录（3+49=52B，恰好满足 min 长度 3+7×7），4500 Hz 先 111 后 222。
    [Fact]
    public async Task GetEq_DuplicateFrequency_LastWins()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        byte[] parameters =
        [
            0x22, 0x34,                          // marker + data 长度 52
            0x00, 0x70, 0xFE,
            0x37, 0x00, 0x64, 0x00, 0x28, 0x00, 0x02,  // 55 Hz, +100
            0xDC, 0x00, 0xCE, 0xFF, 0x78, 0x00, 0x02,  // 220 Hz, -50
            0xF4, 0x01, 0x00, 0x00, 0x96, 0x00, 0x02,  // 500 Hz, 0
            0xE8, 0x03, 0x19, 0x00, 0x96, 0x00, 0x02,  // 1 kHz, +25
            0x08, 0x07, 0xE7, 0xFF, 0x78, 0x00, 0x02,  // 1.8 kHz, -25
            0x94, 0x11, 0x6F, 0x00, 0x82, 0x00, 0x02,  // 4.5 kHz, +111（旧）
            0x94, 0x11, 0xDE, 0x00, 0x82, 0x00, 0x02,  // 4.5 kHz, +222（胜）
        ];

        // 先启动请求（pending 注册完成）再注入响应，与真实收发时序一致。
        var task = client.GetEqAsync(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, parameters);
        var offsets = await task;

        Assert.NotNull(offsets);
        // 2800 Hz 不在响应里 → 默认 0；4500 Hz（索引 6）取后一条 222。
        Assert.Equal(new[] { 100, -50, 0, 25, -25, 0, 222, 0, 0, 0 }, offsets);
    }

    // 源码守卫复刻：Python get_eq 要求 len(data) >= 3 + 7×7 = 52 字节（至少 7 条记录），
    // 只含 2 段的响应必须返回 null 而不是部分解析。
    [Fact]
    public async Task GetEq_TwoBandsOnly_ReturnsNull()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        byte[] parameters =
        [
            0x22, 0x11,                          // data 长度 17 = 3 + 2×7 < 52
            0x00, 0x70, 0xFE,
            0x37, 0x00, 0x64, 0x00, 0x28, 0x00, 0x02,  // 55 Hz, +100
            0xDC, 0x00, 0x32, 0x00, 0x78, 0x00, 0x02,  // 220 Hz, +50
        ];

        var task = client.GetEqAsync(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, parameters);

        Assert.Null(await task);
    }

    // params[0] != 0x22 → null（Python: resp.params[0] != 0x22）。
    [Fact]
    public async Task GetEq_WrongMarker_ReturnsNull()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        byte[] parameters = [0x23, 0x03, 0x00, 0x70, 0xFE];

        var task = client.GetEqAsync(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, parameters);

        Assert.Null(await task);
    }

    // 设备未应答 → null。
    [Fact]
    public async Task GetEq_NoResponse_ReturnsNull()
    {
        using var client = new H3Client(new FakeBleLink());
        Assert.Null(await client.GetEqAsync(TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>构造并注入一个 opCode=0xFF 的响应帧（status=0）。</summary>
    private static void PushResponse(FakeBleLink link, byte sn, byte[] parameters)
    {
        var response = new Packet(OpCode.Custom, Packet.TypeResponse, hasResponse: 0, status: 0, sn: sn, parameters: parameters);
        link.RaiseReceived(response.Encode());
    }
}
