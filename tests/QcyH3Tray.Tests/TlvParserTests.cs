using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// TLV parser tests, aligned with Python rcsp.py parse_tlv:
/// entries are [L][T][V×(L-1)] where L counts T itself; parsing stops at the
/// first truncated/out-of-bounds entry and never throws.
/// </summary>
public class TlvParserTests
{
    // 复刻 Python tests/unit_test.py::test_tlv：
    // 02 00 57 02 05 02 → [(0x00, 57), (0x05, 02)]（电量 87%、工作模式 0x02）。
    [Fact]
    public void Parse_TwoEntries_MatchesPythonUnitTest()
    {
        byte[] data = [0x02, AdvType.BatteryQuantity, 0x57, 0x02, AdvType.WorkMode, 0x02];
        var entries = TlvParser.Parse(data);

        Assert.Equal(2, entries.Count);
        Assert.Equal(AdvType.BatteryQuantity, entries[0].Type);
        Assert.Equal(new byte[] { 0x57 }, entries[0].Value);
        Assert.Equal(AdvType.WorkMode, entries[1].Type);
        Assert.Equal(new byte[] { 0x02 }, entries[1].Value);
    }

    // L=1 表示 V 为空（V 占 L-1=0 字节）。
    [Fact]
    public void Parse_LengthOne_YieldsEmptyValue()
    {
        byte[] data = [0x01, 0x05, 0x02, 0x00, 0x55];
        var entries = TlvParser.Parse(data);

        Assert.Equal(2, entries.Count);
        Assert.Equal((byte)0x05, entries[0].Type);
        Assert.Empty(entries[0].Value);
        Assert.Equal((byte)0x00, entries[1].Type);
        Assert.Equal(new byte[] { 0x55 }, entries[1].Value);
    }

    // 尾部只剩 1 字节（L 无 T 可读）→ 停止，之前的有效条目保留。
    [Fact]
    public void Parse_TrailingSingleByte_Stops()
    {
        byte[] data = [0x02, 0x00, 0x55, 0x07];
        var entries = TlvParser.Parse(data);

        Assert.Single(entries);
        Assert.Equal(new byte[] { 0x55 }, entries[0].Value);
    }

    // L=0 非法 → 立即停止（Python: if length < 1: break）。
    [Fact]
    public void Parse_ZeroLength_StopsImmediately()
    {
        byte[] data = [0x00, 0x05, 0x02, 0x00, 0x55];
        Assert.Empty(TlvParser.Parse(data));
    }

    // L 越界（声明 5 字节但只剩 2 字节值）→ 停止，即"越界即停"。
    [Fact]
    public void Parse_OutOfBoundsLength_StopsImmediately()
    {
        byte[] data = [0x05, 0x07, 0x01, 0x02];
        Assert.Empty(TlvParser.Parse(data));
    }

    // 第二条 L 越界 → 停止，第一条有效。
    [Fact]
    public void Parse_SecondEntryTruncated_StopsAfterFirst()
    {
        byte[] data = [0x02, 0x00, 0x55, 0x03, 0x05, 0x01];
        var entries = TlvParser.Parse(data);

        Assert.Single(entries);
        Assert.Equal(new byte[] { 0x55 }, entries[0].Value);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TlvParser.Parse([]));
    }
}
