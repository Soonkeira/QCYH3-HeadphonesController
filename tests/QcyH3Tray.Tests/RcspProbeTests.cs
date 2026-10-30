using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// eqprobe2 判定/解读辅助（<see cref="RcspProbe"/>）的单元测试。向量来源：
/// Python 参考实现的 EQ v2 响应布局（h3_device.py get_eq）与 Quicky 文档的
/// EQ v1/v2、0x44、0x27 响应格式（docs/protocol.md）。
/// </summary>
public class RcspProbeTests
{
    // ───────────────────────── Request Data 识别 ─────────────────────────

    [Fact]
    public void IsRequestDataRead_Fe01Cmd_True()
    {
        Assert.True(RcspProbe.IsRequestDataRead([0xFE, 0x01, 0x22], out byte cmd));
        Assert.Equal(0x22, cmd);
    }

    [Fact]
    public void IsRequestDataRead_OtherShape_False()
    {
        Assert.False(RcspProbe.IsRequestDataRead([0x22, 0x49, 0x00, 0x70, 0xFE], out byte cmd1));
        Assert.Equal(0, cmd1);
        Assert.False(RcspProbe.IsRequestDataRead([], out byte cmd2));
        Assert.Equal(0, cmd2);
        Assert.False(RcspProbe.IsRequestDataRead([0xFE, 0x02, 0x22, 0x00], out byte cmd3));
        Assert.Equal(0, cmd3);
    }

    // ───────────────────────── EQ 全表判定 ─────────────────────────

    // Python/H3 格式正样本：params = 22 49 01 70 FE + 10×7B（55Hz +350, 220Hz -120，其余 0）。
    [Fact]
    public void AnalyzeCustomRead_EqV2PythonFormat_HitsWithPresetAndGains()
    {
        byte[] request = [0xFE, 0x01, 0x22];
        var bands = new List<byte> { 0x22, 0x49, 0x01, 0x70, 0xFE };
        AppendBand(bands, EqBands.Frequencies[0], 350);
        AppendBand(bands, EqBands.Frequencies[1], -120);
        for (int i = 2; i < 10; i++)
        {
            AppendBand(bands, EqBands.Frequencies[i], 0);
        }

        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: [.. bands]);
        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead(request, response);

        Assert.True(verdict.Hit);
        Assert.Contains("EQ v2(0x22)", verdict.Judgment);
        Assert.Contains("Python/H3 格式", verdict.Interpretation);
        Assert.Contains("预设 0x01（默认）", verdict.Interpretation);
        Assert.Contains("段数=10", verdict.Interpretation);
        Assert.Contains("55Hz=350", verdict.Interpretation);
        Assert.Contains("220Hz=-120", verdict.Interpretation);
    }

    // Quicky 格式正样本（EQ v1，cmd 0x20）：params = 20 48 eqType mgLo mgHi + 10×7B（含 bandType）。
    [Fact]
    public void AnalyzeCustomRead_EqV1QuickyFormat_HitsWithEqTypeAndMasterGain()
    {
        byte[] request = [0xFE, 0x01, RcspProbe.EqV1Cmd];
        var bands = new List<byte> { 0x20, 0x48, 0x02, 0x64, 0x00 };  // eqType=2, masterGain=+100
        for (int i = 0; i < 10; i++)
        {
            ushort freq = (ushort)(1000 + i * 100);
            bands.AddRange([(byte)(freq & 0xFF), (byte)(freq >> 8)]);
            bands.AddRange([0x32, 0x00]);   // gain = +50
            bands.AddRange([0x64, 0x00]);   // Q = 100
            bands.Add(0x01);                // bandType
        }

        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: [.. bands]);
        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead(request, response);

        Assert.True(verdict.Hit);
        Assert.Contains("EQ v1(0x20)", verdict.Judgment);
        Assert.Contains("Quicky 格式", verdict.Interpretation);
        Assert.Contains("eqType=0x02", verdict.Interpretation);
        Assert.Contains("masterGain=100", verdict.Interpretation);
    }

    // 本机第一轮已证的行为：status=0 + 空载荷 → 不命中，判定文案要说明这是"认识命令但无数据"。
    [Fact]
    public void AnalyzeCustomRead_EmptyPayload_DoesNotHitWithKnownWording()
    {
        byte[] request = [0xFE, 0x01, 0x22];
        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: []);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead(request, response);

        Assert.False(verdict.Hit);
        Assert.Contains("载荷为空", verdict.Judgment);
    }

    // 无响应（超时）→ 不命中，判定文案与 H3S 观察到的"不支持读=无帧"对齐。
    [Fact]
    public void AnalyzeCustomRead_NoResponse_DoesNotHitAndMentionsTimeout()
    {
        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, 0x22], null);

        Assert.False(verdict.Hit);
        Assert.Contains("无响应", verdict.Judgment);
    }

    // 回声不一致 → 不命中。
    [Fact]
    public void AnalyzeCustomRead_CmdEchoMismatch_DoesNotHit()
    {
        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: [0x17, 0x03, 0x02, 0x00, 0x00]);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, 0x22], response);

        Assert.False(verdict.Hit);
        Assert.Contains("0x17", verdict.Judgment);
    }

    // EQ 表数据不足 52B → 不命中（判定给出实际长度）。
    [Fact]
    public void AnalyzeCustomRead_ShortEqTable_DoesNotHit()
    {
        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1,
            parameters: [0x22, 0x30, 0x01, 0x70, 0xFE, 0x37, 0x00, 0x00, 0x00, 0x28, 0x00, 0x02]);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, 0x22], response);

        Assert.False(verdict.Hit);
        Assert.Contains("数据不足", verdict.Judgment);
    }

    // ───────────────────────── 0x44 / 0x27 单值判定 ─────────────────────────

    [Fact]
    public void AnalyzeCustomRead_MaxEqCount_HitsWithCount()
    {
        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: [0x44, 0x01, 0x03]);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, RcspProbe.MaxEqCountCmd], response);

        Assert.True(verdict.Hit);
        Assert.Contains("槽位数 = 3", verdict.Interpretation);
    }

    [Fact]
    public void AnalyzeCustomRead_AdaptiveEq_HitsWithState()
    {
        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: [0x27, 0x01, 0x01]);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, RcspProbe.AdaptiveEqCmd], response);

        Assert.True(verdict.Hit);
        Assert.Contains("自适应 EQ 状态 = 0x01", verdict.Interpretation);
    }

    [Fact]
    public void AnalyzeCustomRead_SingleValueZeroDeclaredLength_DoesNotHit()
    {
        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: [0x44, 0x00]);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, RcspProbe.MaxEqCountCmd], response);

        Assert.False(verdict.Hit);
    }

    // 通用 command（如 0x30 版本）带数据 → 命中并列出值区。
    [Fact]
    public void AnalyzeCustomRead_GenericVersionCmd_HitsWithValueArea()
    {
        var response = new Packet(OpCode.Custom, type: Packet.TypeResponse, status: 0, sn: 1, parameters: [0x30, 0x03, 0x00, 0x01, 0x08]);

        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0xFE, 0x01, 0x30], response);

        Assert.True(verdict.Hit);
        Assert.Contains("00 01 08", verdict.Interpretation);
    }

    // 非 Request Data 形状的请求 → 不命中。
    [Fact]
    public void AnalyzeCustomRead_NonRequestDataShape_DoesNotHit()
    {
        ProbeVerdict verdict = RcspProbe.AnalyzeCustomRead([0x17, 0x03, 0x01, 0x01, 0x02], null);

        Assert.False(verdict.Hit);
        Assert.Contains("不是 FE 01 cmd", verdict.Interpretation);
    }

    // ───────────────────────── feature map / sys info 解读 ─────────────────────────

    [Fact]
    public void DescribeFeatureMap_Empty_MentionsEmpty()
    {
        string text = RcspProbe.DescribeFeatureMap([]);
        Assert.Contains("载荷为空", text);
    }

    // 位图 0x03 0x40 → bit0、bit1、bit14 置位（LSB 在前）。
    [Fact]
    public void DescribeFeatureMap_Bitmap_ListsSetBits()
    {
        string text = RcspProbe.DescribeFeatureMap([0x03, 0x40]);
        Assert.Contains("置位 bit：0 1 14", text);
    }

    [Fact]
    public void DescribeSysInfoPayload_FunctionEchoOnly_MatchesRound1Behavior()
    {
        string text = RcspProbe.DescribeSysInfoPayload([0x07]);
        Assert.Contains("仅 function 回声 0x07", text);
    }

    // 有属性数据时同时给出 [attrId][len][value] 与 TLV 两种解读（不抛异常）。
    [Fact]
    public void DescribeSysInfoPayload_WithAttributes_ShowsBothParses()
    {
        // 数据区 0C 02 01 00：按 [attrId][len][value] 读 = attr 0x0C 长度 2 值 01 00；
        // 同一串按 TLV 读 = L=0C(12) 越界 → 无合法 TLV 项。
        string text = RcspProbe.DescribeSysInfoPayload([0x07, 0x0C, 0x02, 0x01, 0x00]);
        Assert.Contains("attr 0x0C", text);
        Assert.Contains("TLV 解读", text);
    }

    // ───────────────────────── GATT 直读字节解读 ─────────────────────────

    // Quicky 直读格式：[eqType][masterGain 2B][10×7B] → 段数 10。
    [Fact]
    public void DescribeDirectEqBytes_QuickyDirectFormat_ParsesBands()
    {
        var data = new List<byte> { 0x03, 0x00, 0x00 };
        for (int i = 0; i < 10; i++)
        {
            data.AddRange([0x37, 0x00, 0x64, 0x00, 0x80, 0x00, 0x02]);
        }

        string text = RcspProbe.DescribeDirectEqBytes([.. data]);
        Assert.Contains("eqType=0x03", text);
        Assert.Contains("段数=10", text);
    }

    [Fact]
    public void DescribeDirectEqBytes_TooShort_ReportsRaw()
    {
        string text = RcspProbe.DescribeDirectEqBytes([0x01, 0x02]);
        Assert.Contains("不足 3B", text);
    }

    // ───────────────────────── EQ 家族常量 ─────────────────────────

    // Quicky 调研的 EQ 家族 command ID 原样：20/22/27/44/46/47。
    [Fact]
    public void EqFamilyCmdIds_Golden()
    {
        Assert.Equal(new byte[] { 0x20, 0x22, 0x27, 0x44, 0x46, 0x47 }, RcspProbe.EqFamilyCmdIds);
        Assert.Equal(0x63, RcspProbe.SweepMaxCmdId);
    }

    /// <summary>追加一段 7B 频段记录（freq u16LE + gain i16LE + refGain u16LE + Q u8）。</summary>
    private static void AppendBand(List<byte> bands, ushort frequency, int gain)
    {
        short clamped = (short)Math.Clamp(gain, short.MinValue, short.MaxValue);
        bands.AddRange([(byte)(frequency & 0xFF), (byte)(frequency >> 8)]);
        bands.AddRange([(byte)(clamped & 0xFF), (byte)((clamped >> 8) & 0xFF)]);
        bands.AddRange([0x00, 0x00]);   // refGain = 0（解读不使用）
        bands.Add(0x02);                // Q = 2
    }
}
