using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 阶段 4a 扩展功能探针（自动关机 / 按键映射 / 提示音 / 双设备连接）编解码与 H3Client 通道测试。
/// 向量来源：
/// <list type="bullet">
/// <item>本机真机（AA:BB:CC:DD:EE:FF，2026-10）：CUSTOM FE 01 14 → 回包 <c>14 04 FF FF 00 00</c>；</item>
/// <item>qcy-h3s-windows docs/BOTOES.md：H3S 实测按键表 LTV <c>2E 02</c> + 15 条三元组（整表回写验证通过）；</item>
/// <item>qcy-h3s-windows docs/PROTOCOLO-LEITURAS.md：H3S 实测 <c>1d 02 06 08</c>（提示音音量）、
/// <c>24 01 00</c>（CMDID_1T2）、<c>19 04 4a 4c 30 30</c>（语音名称 "JL00"）；</item>
/// <item>Quicky docs/protocol.md / internal/command / internal/response：0x14 读写格式、0x2B 键值对、0x1D 音量格式。</item>
/// </list>
/// </summary>
public class ProbeCodecTests
{
    // ───────────────────────── 自动关机（PowerManager，cmd 0x14） ─────────────────────────

    // 真机向量：本机与 H3S 均回 14 04 FF FF 00 00 → 时长 0xFFFF（不自动关机）、计时 0。
    [Fact]
    public void ParseResponse_RealDeviceVector_NeverOff()
    {
        var info = PowerManager.ParseResponse([0x14, 0x04, 0xFF, 0xFF, 0x00, 0x00]);

        Assert.NotNull(info);
        Assert.Equal(0xFFFF, info.PowerOffMinutes);
        Assert.Equal(0, info.CurrentMinutes);
        Assert.True(info.IsNeverOff);
    }

    [Fact]
    public void ParseResponse_TwentyMinutesWithCounter()
    {
        var info = PowerManager.ParseResponse([0x14, 0x04, 0x14, 0x00, 0x05, 0x00]);

        Assert.NotNull(info);
        Assert.Equal(20, info.PowerOffMinutes);
        Assert.Equal(5, info.CurrentMinutes);
        Assert.False(info.IsNeverOff);
    }

    // 守卫：回声不符 / 数据不足 / 声明长度 < 4 → null（不抛异常）。
    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x14 })]
    [InlineData(new byte[] { 0x14, 0x04, 0xFF, 0xFF })]             // 声明 4B 但实际数据只有 2B
    [InlineData(new byte[] { 0x14, 0x02, 0xFF, 0xFF, 0x00, 0x00 })] // 声明长度 2 < 4
    [InlineData(new byte[] { 0x22, 0x04, 0xFF, 0xFF, 0x00, 0x00 })] // 回声非 0x14
    public void ParseResponse_Guards_ReturnNull(byte[] parameters)
    {
        Assert.Null(PowerManager.ParseResponse(parameters));
    }

    // 原样回写的核心保证：把真机读到的 (0xFFFF, 0) 原样传回，写载荷与读响应逐字节一致。
    [Fact]
    public void BuildWritePayload_NeverOff_MatchesRealDeviceReadBytes()
    {
        byte[] payload = PowerManager.BuildWritePayload(0xFFFF, 0);

        Assert.Equal(new byte[] { 0x14, 0x04, 0xFF, 0xFF, 0x00, 0x00 }, payload);
    }

    [Fact]
    public void BuildWritePayload_ThirtyMinutes_LittleEndian()
    {
        Assert.Equal(new byte[] { 0x14, 0x04, 0x1E, 0x00, 0x00, 0x00 }, PowerManager.BuildWritePayload(30, 0));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0x10000, 0)]
    [InlineData(30, -1)]
    [InlineData(30, 0x10000)]
    public void BuildWritePayload_OutOfU16Range_Throws(int powerOff, int current)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerManager.BuildWritePayload(powerOff, current));
    }

    [Fact]
    public void Describe_CoversBothFields()
    {
        string never = PowerManager.Describe(new PowerManagerInfo(0xFFFF, 0));
        Assert.Contains("不自动关机", never);
        Assert.Contains("0xFFFF", never);

        string minutes = PowerManager.Describe(new PowerManagerInfo(30, 12));
        Assert.Contains("30 分钟", minutes);
        Assert.Contains("12 分钟", minutes);
    }

    // ───────────────────────── 按键映射（KeyMap，ADV_INFO type=02） ─────────────────────────

    // qcy-h3s docs/BOTOES.md 的 H3S 真机键表值区（15 条三元组，LTV 头 2E 02 之外的全部字节）。
    private static readonly byte[] H3SKeyTableValue =
    [
        0x07, 0x01, 0x01, 0x07, 0x02, 0x07, 0x07, 0x03, 0x04, 0x07, 0x04, 0x00, 0x07, 0x05, 0x00,
        0x03, 0x01, 0x06, 0x03, 0x02, 0x00, 0x03, 0x03, 0x00, 0x03, 0x04, 0x00, 0x03, 0x05, 0x02,
        0x05, 0x01, 0x05, 0x05, 0x02, 0x00, 0x05, 0x03, 0x00, 0x05, 0x04, 0x00, 0x05, 0x05, 0x03,
    ];

    [Fact]
    public void ParseAdvInfoValue_H3SRealVector_FifteenEntries()
    {
        var entries = KeyMap.ParseAdvInfoValue(H3SKeyTableValue);

        Assert.NotNull(entries);
        Assert.Equal(15, entries.Count);
        Assert.Equal(new KeyMappingEntry(0x07, 0x01, 0x01), entries[0]);
        Assert.Equal(new KeyMappingEntry(0x07, 0x02, 0x07), entries[1]);
        Assert.Equal(new KeyMappingEntry(0x07, 0x03, 0x04), entries[2]);
        Assert.Equal(new KeyMappingEntry(0x03, 0x01, 0x06), entries[5]);
        Assert.Equal(new KeyMappingEntry(0x03, 0x05, 0x02), entries[9]);
        Assert.Equal(new KeyMappingEntry(0x05, 0x01, 0x05), entries[10]);
        Assert.Equal(new KeyMappingEntry(0x05, 0x05, 0x03), entries[14]);
    }

    // 与 h3s get_key_settings 相同的容错：末尾残尾（不足 3B）忽略。
    [Fact]
    public void ParseAdvInfoValue_TrailingPartialBytesIgnored()
    {
        byte[] value = [.. H3SKeyTableValue, 0x09, 0x01]; // 多 2 字节残尾

        var entries = KeyMap.ParseAdvInfoValue(value);

        Assert.NotNull(entries);
        Assert.Equal(15, entries.Count);
    }

    [Fact]
    public void ParseAdvInfoValue_ShortValues_ReturnNull()
    {
        Assert.Null(KeyMap.ParseAdvInfoValue([]));
        Assert.Null(KeyMap.ParseAdvInfoValue([0x07, 0x01]));
    }

    // 整表回写：由解析结果重建的 SET_ADV_INFO LTV 与 H3S 真机读到的 [2E 02 …] 逐字节一致。
    [Fact]
    public void BuildSetAdvInfoPayload_H3SVector_MatchesRealLtvBytes()
    {
        var entries = KeyMap.ParseAdvInfoValue(H3SKeyTableValue)!;

        byte[] payload = KeyMap.BuildSetAdvInfoPayload(entries);

        byte[] expected = [(byte)(1 + H3SKeyTableValue.Length), 0x02, .. H3SKeyTableValue];
        Assert.Equal(expected, payload);
        Assert.Equal(47, payload.Length);
    }

    [Fact]
    public void BuildSetAdvInfoPayload_RoundTrip()
    {
        var original = KeyMap.ParseAdvInfoValue(H3SKeyTableValue)!;

        var rebuilt = KeyMap.ParseAdvInfoValue(KeyMap.BuildSetAdvInfoPayload(original).AsSpan(2));

        Assert.NotNull(rebuilt);
        Assert.Equal(original, rebuilt);
    }

    [Fact]
    public void BuildSetAdvInfoPayload_Guards()
    {
        Assert.Throws<ArgumentException>(() => KeyMap.BuildSetAdvInfoPayload([]));

        var tooBig = Enumerable.Range(0, 86).Select(i => new KeyMappingEntry((byte)i, 1, 1)).ToList(); // 1+258 > 255
        Assert.Throws<ArgumentException>(() => KeyMap.BuildSetAdvInfoPayload(tooBig));
    }

    // 原始值回写（探针用）：与三元组重建等价，且对残尾原样带回。
    [Fact]
    public void BuildSetAdvInfoPayloadRaw_MatchesEntriesBuildAndCarriesTail()
    {
        byte[] expected = KeyMap.BuildSetAdvInfoPayload(KeyMap.ParseAdvInfoValue(H3SKeyTableValue)!);
        Assert.Equal(expected, KeyMap.BuildSetAdvInfoPayloadRaw(H3SKeyTableValue));

        byte[] withTail = [.. H3SKeyTableValue, 0x09, 0x01];
        byte[] payload = KeyMap.BuildSetAdvInfoPayloadRaw(withTail);
        Assert.Equal((byte)(1 + withTail.Length), payload[0]);
        Assert.Equal(0x02, payload[1]);
        Assert.Equal(withTail, payload[2..]);
    }

    [Fact]
    public void BuildSetAdvInfoPayloadRaw_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeyMap.BuildSetAdvInfoPayloadRaw([]));
    }

    [Theory]
    [InlineData((byte)1, "单击")]
    [InlineData((byte)2, "双击")]
    [InlineData((byte)3, "三击")]
    [InlineData((byte)5, "长按")]
    [InlineData((byte)0, "未知(0x00)")]
    public void ActionName_SpotChecks(byte action, string expected)
    {
        Assert.Equal(expected, KeyMap.ActionName(action));
    }

    [Fact]
    public void FunctionName_SpotChecks()
    {
        Assert.Equal("无功能", KeyMap.FunctionName(0x00));
        Assert.Equal("播放/暂停", KeyMap.FunctionName(0x01));
        Assert.Equal("游戏模式切换", KeyMap.FunctionName(0x07));
        Assert.Contains("无功能", KeyMap.FunctionName(0x7F));   // APK 127→0 归一化
        Assert.Contains("降噪", KeyMap.FunctionName(0xFF));     // KEY_FUNC_ID_SWITCH_ANC_MODE
        Assert.StartsWith("未知", KeyMap.FunctionName(0x12));
    }

    // ───────────────────────── 探针解读（RcspProbe 纯函数） ─────────────────────────

    // qcy-h3s 真机向量 1d 02 06 08 → 音量 6、最大 8；单字节 max 缺省 100（Quicky ParseToneVolume）。
    [Fact]
    public void DescribeToneVolume_H3SVector()
    {
        string text = RcspProbe.DescribeToneVolume([0x1D, 0x02, 0x06, 0x08])!;

        Assert.Contains("6", text);
        Assert.Contains("8", text);
        Assert.Contains("提示音音量", text);

        string fallbackMax = RcspProbe.DescribeToneVolume([0x1D, 0x01, 0x06])!;
        Assert.Contains("100", fallbackMax);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x1D, 0x02 })]
    [InlineData(new byte[] { 0x1D, 0x00, 0x06, 0x08 })]
    [InlineData(new byte[] { 0x24, 0x01, 0x00 })]
    public void DescribeToneVolume_Guards_ReturnNull(byte[] p)
    {
        Assert.Null(RcspProbe.DescribeToneVolume(p));
    }

    // qcy-h3s 真机向量 19 04 4a 4c 30 30 → "JL00"。
    [Fact]
    public void DescribeVoiceName_H3SVector()
    {
        string text = RcspProbe.DescribeVoiceName([0x19, 0x04, 0x4A, 0x4C, 0x30, 0x30])!;

        Assert.Contains("JL00", text);
    }

    [Fact]
    public void DescribeVoiceName_Guards_ReturnNull()
    {
        Assert.Null(RcspProbe.DescribeVoiceName([]));
        Assert.Null(RcspProbe.DescribeVoiceName([0x19, 0x00]));
        Assert.Null(RcspProbe.DescribeVoiceName([0x1D, 0x02, 0x06, 0x08]));
    }

    // qcy-h3s 真机向量 24 01 00（CMDID_1T2 双设备连接）。
    [Fact]
    public void DescribeSwitchRead_1T2Vector()
    {
        string text = RcspProbe.DescribeSwitchRead(0x24, [0x24, 0x01, 0x00])!;

        Assert.Contains("双设备连接", text);
        Assert.Contains("0x00", text);
    }

    [Fact]
    public void DescribeSwitchRead_Guards_ReturnNull()
    {
        Assert.Null(RcspProbe.DescribeSwitchRead(0x24, []));
        Assert.Null(RcspProbe.DescribeSwitchRead(0x24, [0x24, 0x01]));
        Assert.Null(RcspProbe.DescribeSwitchRead(0x24, [0x1D, 0x01, 0x00])); // 回声不符
    }

    [Theory]
    [InlineData((byte)0x14, "自动关机")]
    [InlineData((byte)0x24, "双设备连接")]
    [InlineData((byte)0x1D, "提示音音量")]
    [InlineData((byte)0x2B, "按键功能")]
    public void CustomCmdName_SpotChecks(byte cmd, string expectedPart)
    {
        Assert.Contains(expectedPart, RcspProbe.CustomCmdName(cmd));
    }

    // ───────────────────────── H3Client 通道（FakeBleLink 黄金帧） ─────────────────────────

    // 自动关机读：请求 FE 01 14（sn=1），真机回包 params = 14 04 FF FF 00 00。
    [Fact]
    public async Task GetPowerManagerAsync_GoldenRequestResponse()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetPowerManagerAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xFF, 0x00, 0x08, 0x00, 0x01, 0x14, 0x04, 0xFF, 0xFF, 0x00, 0x00, 0xEF]);
        PowerManagerInfo? info = await task;

        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x01, 0xFE, 0x01, 0x14, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
        Assert.NotNull(info);
        Assert.Equal(0xFFFF, info.PowerOffMinutes);
        Assert.Equal(0, info.CurrentMinutes);
    }

    [Fact]
    public async Task GetPowerManagerAsync_NoResponse_Null()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        PowerManagerInfo? info = await client.GetPowerManagerAsync(TimeSpan.FromMilliseconds(50));

        Assert.Null(info);
    }

    // 自动关机写：14 04 FF FF 00 00（原样回写真机基线）。
    [Fact]
    public async Task SetPowerManagerAsync_GoldenRequest()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        await client.SetPowerManagerAsync(0xFFFF, 0, TimeSpan.FromSeconds(5));

        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x07, 0x01, 0x14, 0x04, 0xFF, 0xFF, 0x00, 0x00, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
    }

    // 按键表读：GET_ADV_INFO 掩码 1<<2 → TLV [2E 02 …] → 15 条三元组。
    [Fact]
    public async Task GetKeyMappingsAsync_GoldenRequestResponse()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        byte[] tlv = [(byte)(1 + H3SKeyTableValue.Length), 0x02, .. H3SKeyTableValue];
        var task = client.GetKeyMappingsAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived(BuildAdvInfoResponse(tlv, sn: 1));
        var entries = await task;

        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xC1, 0x00, 0x05, 0x01, 0x00, 0x00, 0x00, 0x04, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
        Assert.NotNull(entries);
        Assert.Equal(15, entries.Count);
        Assert.Equal(new KeyMappingEntry(0x07, 0x01, 0x01), entries[0]);
    }

    // 全掩码 153B 表截取片段：type=02 夹在电量/名称等 TLV 中间时应被正确选中。
    [Fact]
    public async Task GetKeyMappingsAsync_PicksType02AmongOtherTlvs()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        byte[] tlvStream =
        [
            0x02, AdvType.BatteryQuantity, 0x55,                                  // 电量 85%
            0x04, AdvType.DeviceName, 0x51, 0x43, 0x59,                           // 名称 "QCY"
            .. KeyMap.BuildSetAdvInfoPayload(
                [new KeyMappingEntry(0x07, 0x01, 0x01), new KeyMappingEntry(0x03, 0x01, 0x06)]),
        ];
        var task = client.GetKeyMappingsAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived(BuildAdvInfoResponse(tlvStream, sn: 1));
        var entries = await task;

        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new KeyMappingEntry(0x03, 0x01, 0x06), entries[1]);
    }

    [Fact]
    public async Task GetKeyMappingsAsync_NoResponse_Null()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var entries = await client.GetKeyMappingsAsync(TimeSpan.FromMilliseconds(50));

        Assert.Null(entries);
    }

    // 按键表写：SET_ADV_INFO LTV [2E 02 …]（15 条整表）。
    [Fact]
    public async Task SetKeyMappingsAsync_GoldenRequest()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var entries = KeyMap.ParseAdvInfoValue(H3SKeyTableValue)!;
        await client.SetKeyMappingsAsync(entries, TimeSpan.FromSeconds(5));

        byte[] expectedRequest =
        [
            0xFE, 0xDC, 0xBA, 0xC0, 0xC0, 0x00, 0x30, 0x01,
            .. KeyMap.BuildSetAdvInfoPayload(entries),
            0xEF,
        ];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
    }

    // 通用 CUSTOM 读（0x24 双设备连接）：请求 FE 01 24，H3S 真机回包 24 01 00。
    [Fact]
    public async Task SendCustomReadAsync_OneToTwo_GoldenRequestResponse()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.SendCustomReadAsync(0x24, TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xFF, 0x00, 0x05, 0x00, 0x01, 0x24, 0x01, 0x00, 0xEF]);
        Packet? response = await task;

        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x01, 0xFE, 0x01, 0x24, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
        Assert.NotNull(response);
        Assert.Equal([0x24, 0x01, 0x00], response.Params);
        Assert.Contains("双设备连接", RcspProbe.DescribeSwitchRead(0x24, response.Params)!);
    }

    /// <summary>构造 GET_ADV_INFO 响应帧（type=response、status=0、指定 sn、TLV 流 params）。</summary>
    private static byte[] BuildAdvInfoResponse(byte[] tlvStream, byte sn)
    {
        int paramLength = 1 + 1 + tlvStream.Length; // status + sn + params
        var frame = new List<byte>
        {
            0xFE, 0xDC, 0xBA,
            0x00,                       // type=response, hasResponse=0
            OpCode.GetAdvInfo,          // 0xC1
            (byte)(paramLength >> 8), (byte)(paramLength & 0xFF),
            0x00,                       // status
            sn,
        };
        frame.AddRange(tlvStream);
        frame.Add(0xEF);
        return [.. frame];
    }
}
