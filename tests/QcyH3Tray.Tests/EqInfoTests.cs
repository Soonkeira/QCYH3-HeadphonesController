using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 阶段 2 新增的 <see cref="H3Client.GetEqInfoAsync"/>（EQ 读取 + 预设 ID）测试；
/// 解析守卫与 Python get_eq 一致，EQ 频段本体行为由既有 EqTests 覆盖。
/// </summary>
public class EqInfoTests
{
    [Fact]
    public async Task GetEqInfo_ReturnsPresetIdAndOffsets()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        // 数据段 = "03 70 FE"（预设 + 固定头）+ 10×7B 记录 = 73B；params = 22 <len> + 数据段
        var bands = new List<byte> { 0x03, 0x70, 0xFE };
        for (int i = 0; i < 10; i++)
        {
            ushort frequency = EqBands.Frequencies[i];
            short gain = (short)(i * 50);
            bands.AddRange(new byte[]
            {
                (byte)(frequency & 0xFF), (byte)(frequency >> 8),
                (byte)gain, (byte)((uint)gain >> 8),
                0x78, 0x00, 0x02,
            });
        }
        byte[] parameters = [0x22, (byte)bands.Count, .. bands];   // 预设 0x03（重低音）

        var task = client.GetEqInfoAsync(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, parameters);
        EqInfo? info = await task;

        Assert.NotNull(info);
        Assert.Equal((byte)0x03, info.PresetId);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 50), info.Offsets);

        // 请求帧与 GetEqAsync 完全一致（GET 0x22）
        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x01, 0xFE, 0x01, 0x22, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
    }

    // 与 GetEqAsync 相同的守卫：params[0] != 0x22 → null。
    [Fact]
    public async Task GetEqInfo_WrongMarker_ReturnsNull()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetEqInfoAsync(TimeSpan.FromSeconds(5));
        PushResponse(link, sn: 1, [0x23, 0x03, 0x00, 0x70, 0xFE]);

        Assert.Null(await task);
    }

    /// <summary>构造并注入一个 opCode=0xFF 的响应帧（status=0）。</summary>
    private static void PushResponse(FakeBleLink link, byte sn, byte[] parameters)
    {
        var response = new Packet(OpCode.Custom, Packet.TypeResponse, hasResponse: 0, status: 0, sn: sn, parameters: parameters);
        link.RaiseReceived(response.Encode());
    }
}
