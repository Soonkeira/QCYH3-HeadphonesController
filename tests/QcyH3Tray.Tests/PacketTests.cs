using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// Golden-vector tests for RCSP frame encode/decode, aligned byte-for-byte with
/// the Python reference (rcsp.py Packet, tests/unit_test.py).
/// </summary>
public class PacketTests
{
    // 握手命令推演（Python notify_communication_way，opCode 0x0B，params 00 00）：
    // flag = (command<<7)|(hasResponse<<6) = 0xC0；body = sn(01) + 00 00 → paramLen=0003。
    [Fact]
    public void Encode_HandshakeCommand_GoldenFrame()
    {
        var packet = new Packet(OpCode.NotifyCommunicationWay, parameters: [0x00, 0x00]) { Sn = 1 };
        byte[] expected = [0xFE, 0xDC, 0xBA, 0xC0, 0x0B, 0x00, 0x03, 0x01, 0x00, 0x00, 0xEF];
        Assert.Equal(expected, packet.Encode());
    }

    // 降噪命令推演（h3_device.py ANCMode 常量，CUSTOM opCode 0xFF）：
    // body = sn(01) + 5 字节模式 → paramLen=0006。五种 UI 模式各一个完整帧。
    [Fact]
    public void Encode_NoiseModeCommands_GoldenFrames()
    {
        (byte[] payload, byte[] frame)[] cases =
        [
            (AncMode.Off,
                [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x06, 0x01, 0x17, 0x03, 0x02, 0x00, 0x00, 0xEF]),
            (AncMode.Transparency,
                [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x06, 0x01, 0x17, 0x03, 0x03, 0x01, 0x04, 0xEF]),
            (AncMode.NoiseCancelLow,
                [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x06, 0x01, 0x17, 0x03, 0x01, 0x01, 0x00, 0xEF]),
            (AncMode.NoiseCancelMedium,
                [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x06, 0x01, 0x17, 0x03, 0x01, 0x01, 0x01, 0xEF]),
            (AncMode.NoiseCancelHigh,
                [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x06, 0x01, 0x17, 0x03, 0x01, 0x01, 0x02, 0xEF]),
            (AncMode.Adaptive,
                [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x06, 0x01, 0x17, 0x03, 0x01, 0x05, 0x00, 0xEF]),
        ];

        foreach (var (payload, expected) in cases)
        {
            var packet = new Packet(OpCode.Custom, parameters: payload) { Sn = 1 };
            Assert.Equal(expected, packet.Encode());
        }
    }

    // 降噪回读命令推演（h3_device.py get_mode：params FE 01 17，sn=2）：
    // body = sn(02) + 3 字节 → paramLen=0004。
    [Fact]
    public void Encode_AncStateRead_GoldenFrame()
    {
        var packet = new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x17]) { Sn = 2 };
        byte[] expected = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x02, 0xFE, 0x01, 0x17, 0xEF];
        Assert.Equal(expected, packet.Encode());
    }

    // 游戏模式写入推演（h3_device.py set_game_mode：SET_ADV_INFO=0xC0，params 02 05 02/01）：
    // body = sn(01) + 3 字节 → paramLen=0004。
    [Fact]
    public void Encode_GameModeCommands_GoldenFrames()
    {
        var on = new Packet(OpCode.SetAdvInfo, parameters: [0x02, AdvType.WorkMode, 0x02]) { Sn = 1 };
        byte[] expectedOn = [0xFE, 0xDC, 0xBA, 0xC0, 0xC0, 0x00, 0x04, 0x01, 0x02, 0x05, 0x02, 0xEF];
        Assert.Equal(expectedOn, on.Encode());

        var off = new Packet(OpCode.SetAdvInfo, parameters: [0x02, AdvType.WorkMode, 0x01]) { Sn = 1 };
        byte[] expectedOff = [0xFE, 0xDC, 0xBA, 0xC0, 0xC0, 0x00, 0x04, 0x01, 0x02, 0x05, 0x01, 0xEF];
        Assert.Equal(expectedOff, off.Encode());
    }

    // 电量查询推演（h3_device.py get_battery → get_adv_info(1<<0)：GET_ADV_INFO=0xC1，
    // 掩码 4B 大端 00 00 00 01；body = sn(01) + 4 字节 → paramLen=0005）。
    [Fact]
    public void Encode_BatteryQuery_GoldenFrame()
    {
        var packet = new Packet(OpCode.GetAdvInfo, parameters: [0x00, 0x00, 0x00, 0x01]) { Sn = 1 };
        byte[] expected = [0xFE, 0xDC, 0xBA, 0xC0, 0xC1, 0x00, 0x05, 0x01, 0x00, 0x00, 0x00, 0x01, 0xEF];
        Assert.Equal(expected, packet.Encode());
    }

    // hasResponse=0 时 flag bit6=0：flag = 1<<7 = 0x80（Python flag = (type&1)<<7|(hasResponse&1)<<6）。
    [Fact]
    public void Encode_FireAndForget_FlagBit6Clear()
    {
        var packet = new Packet(OpCode.SetAdvInfo, hasResponse: 0, parameters: [0x02, AdvType.WorkMode, 0x02]) { Sn = 1 };
        byte[] expected = [0xFE, 0xDC, 0xBA, 0x80, 0xC0, 0x00, 0x04, 0x01, 0x02, 0x05, 0x02, 0xEF];
        Assert.Equal(expected, packet.Encode());
    }

    // xmOpCode 推演（rcsp.py encode：opCode==1 且 xm_op_code 非 None 时在 sn 后插入 1B）：
    // opCode=01, xm=05, params=AB CD, sn=03 → body = 03 05 AB CD → paramLen=0004。
    [Fact]
    public void Encode_XmOpCode_InsertedForDataOpCode()
    {
        var packet = new Packet(OpCode.Data, xmOpCode: 0x05, parameters: [0xAB, 0xCD]) { Sn = 3 };
        byte[] expected = [0xFE, 0xDC, 0xBA, 0xC0, 0x01, 0x00, 0x04, 0x03, 0x05, 0xAB, 0xCD, 0xEF];
        Assert.Equal(expected, packet.Encode());
    }

    // opCode != 1 时即使设置了 xmOpCode 也不插入（Python: if self.op_code == 1 and xm is not None）。
    [Fact]
    public void Encode_XmOpCode_OmittedForOtherOpCodes()
    {
        var packet = new Packet(OpCode.Custom, xmOpCode: 0x05, parameters: [0xFE, 0x01, 0x22]) { Sn = 1 };
        byte[] expected = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x01, 0xFE, 0x01, 0x22, 0xEF];
        Assert.Equal(expected, packet.Encode());
    }

    // 复刻 Python tests/unit_test.py::test_packet_roundtrip：
    // Packet(opCode=CUSTOM, params=FE 01 22) 默认 sn=0 编码，长度字段 = sn(1) + 参数(3) = 0004。
    [Fact]
    public void EncodeDecode_Roundtrip_MatchesPythonUnitTest()
    {
        var packet = new Packet(OpCode.Custom, parameters: [0xFE, 0x01, 0x22]);
        byte[] raw = packet.Encode();

        byte[] expected = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x00, 0xFE, 0x01, 0x22, 0xEF];
        Assert.Equal(expected, raw);

        var decoded = Packet.Decode(raw);
        Assert.NotNull(decoded);
        Assert.Equal(OpCode.Custom, decoded.OpCode);
        Assert.Equal(Packet.TypeCommand, decoded.Type);
        Assert.Equal(new byte[] { 0xFE, 0x01, 0x22 }, decoded.Params);
    }

    // 复刻 Python tests/unit_test.py::test_response_decode：
    // 响应帧 flag=0x00（bit7=0），opCode=0xFF，长度=5（status 1 + sn 1 + 参数 3），
    // status=0, sn=7, 参数 22 01 00。
    [Fact]
    public void Decode_Response_GoldenVector()
    {
        byte[] raw = [0xFE, 0xDC, 0xBA, 0x00, 0xFF, 0x00, 0x05, 0x00, 0x07, 0x22, 0x01, 0x00, 0xEF];
        var packet = Packet.Decode(raw);

        Assert.NotNull(packet);
        Assert.Equal(Packet.TypeResponse, packet.Type);
        Assert.Equal(0, packet.Status);
        Assert.Equal(7, packet.Sn);
        Assert.Equal(new byte[] { 0x22, 0x01, 0x00 }, packet.Params);
    }

    [Theory]
    [InlineData(0)]                      // 空数据
    [InlineData(7)]                      // 少于最小帧长 8
    public void Decode_TooShort_ReturnsNull(int length)
    {
        Assert.Null(Packet.Decode(new byte[length]));
    }

    [Fact]
    public void Decode_GarbageWithoutHeader_ReturnsNull()
    {
        byte[] garbage = [0x01, 0x02, 0x03, 0xAB, 0xCD, 0xEF, 0x12, 0x34, 0x56, 0x78];
        Assert.Null(Packet.Decode(garbage));
    }

    // 握手帧缺最后一个字节：paramLen 指示的总长超出数据 → 截断返回 null。
    [Fact]
    public void Decode_TruncatedFrame_ReturnsNull()
    {
        byte[] truncated = [0xFE, 0xDC, 0xBA, 0xC0, 0x0B, 0x00, 0x03, 0x01, 0x00, 0x00];
        Assert.Null(Packet.Decode(truncated));
    }

    // 帧尾不是 EF → 返回 null。
    [Fact]
    public void Decode_WrongFooter_ReturnsNull()
    {
        byte[] wrongFooter = [0xFE, 0xDC, 0xBA, 0xC0, 0x0B, 0x00, 0x03, 0x01, 0x00, 0x00, 0x00];
        Assert.Null(Packet.Decode(wrongFooter));
    }

    // 响应帧 body 短于 status+sn 所需（paramLen=0）→ 边界检查返回 null
    // （Python 同帧会抛 IndexError，C# 按约定必须返回 null 不抛异常）。
    [Fact]
    public void Decode_ResponseWithEmptyBody_ReturnsNull()
    {
        byte[] raw = [0xFE, 0xDC, 0xBA, 0x00, 0xFF, 0x00, 0x00, 0xEF];
        Assert.Null(Packet.Decode(raw));
    }

    // decode 在数据中搜索帧头，垃圾前缀被跳过（Python data.find(HEADER)）。
    [Fact]
    public void Decode_SkipsLeadingGarbage()
    {
        byte[] garbage = [0xAA, 0xBB, 0xCC];
        byte[] frame = [0xFE, 0xDC, 0xBA, 0xC0, 0x0B, 0x00, 0x03, 0x01, 0x00, 0x00, 0xEF];
        var packet = Packet.Decode([.. garbage, .. frame]);

        Assert.NotNull(packet);
        Assert.Equal(OpCode.NotifyCommunicationWay, packet.OpCode);
        Assert.Equal(1, packet.Sn);
    }
}
