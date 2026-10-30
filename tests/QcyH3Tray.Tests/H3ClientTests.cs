using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// Command-layer tests for <see cref="H3Client"/> driven by an in-memory
/// <see cref="FakeBleLink"/>, aligned with Python h3_device.py (class H3Device).
/// </summary>
public class H3ClientTests
{
    // sn 推演（Python _next_sn：+1 取 8 位，命中 0 跳到 1，即 1..255 循环）：
    // 连续 300 次取号应得到 1..255, 1..45，且任何一次都不为 0。
    [Fact]
    public void NextSn_ThreeHundredTimes_CyclesSkippingZero()
    {
        using var client = new H3Client(new FakeBleLink());

        var expected = new byte[300];
        var actual = new byte[300];
        for (int i = 0; i < 300; i++)
        {
            actual[i] = client.NextSn();
            expected[i] = (byte)((i % 255) + 1);
        }

        Assert.DoesNotContain((byte)0, actual);
        Assert.Equal(expected, actual);
    }

    // 握手推演：请求帧 golden（见 PacketTests），设备响应 flag=0x00、status=0、sn=1、params 00 00：
    // paramLen = status(1)+sn(1)+params(2) = 0004。
    [Fact]
    public async Task Handshake_GoldenRequestResponse()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.HandshakeAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0x0B, 0x00, 0x04, 0x00, 0x01, 0x00, 0x00, 0xEF]);
        var response = await task;

        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0x0B, 0x00, 0x03, 0x01, 0x00, 0x00, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());

        Assert.NotNull(response);
        Assert.Equal(0, response.Status);
        Assert.Equal(1, response.Sn);
        Assert.Equal(new byte[] { 0x00, 0x00 }, response.Params);
    }

    // 电量推演（h3_device.py get_battery → get_adv_info(1<<0)，响应 params 为 TLV 流：
    // [02 00 55] = L=2, T=0(电量), V=0x55=85%）。
    [Fact]
    public async Task GetBatteryPercent_GoldenRequestResponse()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetBatteryPercentAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05, 0x00, 0x01, 0x02, 0x00, 0x55, 0xEF]);
        int? percent = await task;

        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xC1, 0x00, 0x05, 0x01, 0x00, 0x00, 0x00, 0x01, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
        Assert.Equal(85, percent);
    }

    // 游戏模式读取推演（h3_device.py get_work_mode：GET_ADV_INFO 掩码 1<<5 = 00 00 00 20，
    // 响应 TLV [02 05 02]=开 / [02 05 01]=关，v[0]==0x02 即 True）。
    [Theory]
    [InlineData(new byte[] { 0x02 }, true)]
    [InlineData(new byte[] { 0x01 }, false)]
    public async Task GetWorkMode_TlvValueDecoded(byte[] tlvValue, bool expected)
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetWorkModeAsync(TimeSpan.FromSeconds(5));
        byte[] frame =
        [
            0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05, 0x00, 0x01, 0x02, AdvType.WorkMode, tlvValue[0], 0xEF,
        ];
        link.RaiseReceived(frame);
        bool? result = await task;

        Assert.Equal(expected, result);
        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xC1, 0x00, 0x05, 0x01, 0x00, 0x00, 0x00, 0x20, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
    }

    // 游戏模式读取：响应里没有 WORK_MODE TLV → null。
    [Fact]
    public async Task GetWorkMode_MissingTlv_ReturnsNull()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetWorkModeAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05, 0x00, 0x01, 0x02, 0x00, 0x55, 0xEF]);

        Assert.Null(await task);
    }

    // 游戏模式写入推演（h3_device.py set_game_mode：SET_ADV_INFO params 02 05 02/01）。
    [Theory]
    [InlineData(true, new byte[] { 0xFE, 0xDC, 0xBA, 0xC0, 0xC0, 0x00, 0x04, 0x01, 0x02, 0x05, 0x02, 0xEF })]
    [InlineData(false, new byte[] { 0xFE, 0xDC, 0xBA, 0xC0, 0xC0, 0x00, 0x04, 0x01, 0x02, 0x05, 0x01, 0xEF })]
    public async Task SetGameMode_GoldenFrames(bool enabled, byte[] expectedFrame)
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.SetGameModeAsync(enabled, TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC0, 0x00, 0x04, 0x00, 0x01, 0x00, 0xEF]);
        await task;

        Assert.Equal(expectedFrame, link.WrittenFrames.Single());
    }

    // 降噪回读推演（h3_device.py get_mode：GET 0x17 响应 params 17 03 01 01 02 → 模式 4）。
    [Fact]
    public async Task GetNoiseMode_DecodesLevel()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetNoiseModeAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xFF, 0x00, 0x07, 0x00, 0x01, 0x17, 0x03, 0x01, 0x01, 0x02, 0xEF]);
        int? mode = await task;

        Assert.Equal(4, mode);
        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x01, 0xFE, 0x01, 0x17, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
    }

    // 未匹配任何 pending sn 的响应包走通知回调（Python: elif self.on_notification）。
    [Fact]
    public void UnmatchedSn_RoutesToNotification()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        Packet? notified = null;
        client.NotificationReceived += packet => notified = packet;
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xFF, 0x00, 0x05, 0x00, 0x7F, 0x22, 0x01, 0x00, 0xEF]);

        Assert.NotNull(notified);
        Assert.Equal(0x7F, notified.Sn);
        Assert.Equal(new byte[] { 0x22, 0x01, 0x00 }, notified.Params);
    }

    // 通知流分片 + 前导垃圾：响应分 3 片到达仍能重组（Python _handle_notify 的缓冲逻辑）。
    // 注意：帧头本身必须完整出现在某一片内——缓冲中只有半个帧头时（如 "FE DC"），
    // Python 参考实现会因 find(HEADER) 失败而清空缓冲，C# 移植保持同一语义。
    [Fact]
    public async Task FragmentedNotifyStream_ReassemblesResponse()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetBatteryPercentAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xAA, 0xBB, 0xCC]);                             // 垃圾：无帧头 → 缓冲清空
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC1]);                 // 完整帧头 + flag + opCode
        link.RaiseReceived([0x00, 0x05, 0x00, 0x01, 0x02, 0x00, 0x55]);     // 长度 + status + sn + 参数
        link.RaiseReceived([0xEF]);                                         // 帧尾

        Assert.Equal(85, await task);
    }

    // 超时未应答 → 返回 null（Python asyncio.wait_for → None）。
    [Fact]
    public async Task SendAsync_Timeout_ReturnsNull()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var response = await client.GetBatteryAsync(TimeSpan.FromMilliseconds(50));

        Assert.Null(response);
        Assert.Single(link.WrittenFrames);
    }

    // hasResponse=0 的包写入后立即返回 null（Python: fut is None → return None），flag bit6=0。
    [Fact]
    public async Task SendAsync_FireAndForget_WritesAndReturnsNull()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var packet = new Packet(OpCode.SetAdvInfo, hasResponse: 0, parameters: [0x02, AdvType.WorkMode, 0x02]);
        var response = await client.SendAsync(packet, TimeSpan.FromSeconds(5));

        Assert.Null(response);
        byte[] expectedFrame = [0xFE, 0xDC, 0xBA, 0x80, 0xC0, 0x00, 0x04, 0x01, 0x02, 0x05, 0x02, 0xEF];
        Assert.Equal(expectedFrame, link.WrittenFrames.Single());
    }

    // DATA(1) 通道响应回归（真机排查假设场景）：设备对 0xFF 读命令经 opCode=1 帧应答，
    // body = status + sn + xmOpCode + params。解码必须消费 xmOpCode 字节且 sn 匹配与
    // opCode 无关 → GetNoiseModeAsync 仍能命中并按 params 解析出模式。
    // 帧: FE DC BA 00 01 00 08 00(status) 01(sn) 17(xm) 17 03 01 01 02(params) EF，paramLen=8。
    [Fact]
    public async Task GetNoiseMode_DataOpCodeResponse_MatchesSnAndConsumesXmOpCode()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        var task = client.GetNoiseModeAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0x01, 0x00, 0x08, 0x00, 0x01, 0x17, 0x17, 0x03, 0x01, 0x01, 0x02, 0xEF]);
        int? mode = await task;

        Assert.Equal(4, mode);
        byte[] expectedRequest = [0xFE, 0xDC, 0xBA, 0xC0, 0xFF, 0x00, 0x04, 0x01, 0xFE, 0x01, 0x17, 0xEF];
        Assert.Equal(expectedRequest, link.WrittenFrames.Single());
    }

    // PacketTraced 诊断：命中 pending 的帧上报 MatchedPending=true，完整保留帧字节。
    [Fact]
    public async Task PacketTraced_MatchedFrame_ReportsMatch()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);
        FrameTrace? trace = null;
        client.PacketTraced += t => trace = t;

        var task = client.GetBatteryPercentAsync(TimeSpan.FromSeconds(5));
        byte[] frame = [0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05, 0x00, 0x01, 0x02, 0x00, 0x55, 0xEF];
        link.RaiseReceived(frame);
        await task;

        Assert.NotNull(trace);
        Assert.NotNull(trace!.Packet);
        Assert.True(trace.MatchedPending);
        Assert.Equal(0x01, trace.Packet!.Sn);
        Assert.Equal(frame, trace.Frame);
    }

    // PacketTraced 诊断：sn 未命中的帧上报 MatchedPending=false，并给出判定后仍挂起的
    // sn 快照——"为何没匹配上"的依据（设备回的 sn 与请求不一致时立即可见）。
    [Fact]
    public async Task PacketTraced_UnmatchedFrame_ReportsPendingSnapshot()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);
        var traces = new List<FrameTrace>();
        client.PacketTraced += traces.Add;

        var task = client.GetBatteryPercentAsync(TimeSpan.FromSeconds(5));
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05, 0x00, 0x7F, 0x02, 0x00, 0x55, 0xEF]); // sn=0x7F
        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05, 0x00, 0x01, 0x02, 0x00, 0x55, 0xEF]); // sn=0x01 命中
        await task;

        Assert.Equal(2, traces.Count);
        Assert.False(traces[0].MatchedPending);
        Assert.Equal(new byte[] { 0x01 }, traces[0].PendingSns); // 请求 0x01 当时仍在挂起
        Assert.True(traces[1].MatchedPending);
        Assert.Empty(traces[1].PendingSns);
    }

    // PacketTraced 诊断：帧尾不是 EF 的帧也必须上报（解码失败、被丢弃）——真机上
    // "设备明明应答了却超时"时，先看这里有没有解析失败记录。
    [Fact]
    public void PacketTraced_MalformedFrame_ReportsDecodeFailure()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);
        var traces = new List<FrameTrace>();
        client.PacketTraced += traces.Add;

        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05, 0x00, 0x01, 0x02, 0x00, 0x55, 0x00]); // 帧尾 00 ≠ EF

        var trace = Assert.Single(traces);
        Assert.Null(trace.Packet);
        Assert.False(trace.MatchedPending);
        Assert.Equal(13, trace.Frame.Length);
    }

    // SnapshotRxBuffer：不完整帧滞留在重组缓冲时可读到——诊断"应答被截断、分片未到齐"。
    [Fact]
    public void SnapshotRxBuffer_ReturnsPartialFrameBytes()
    {
        var link = new FakeBleLink();
        using var client = new H3Client(link);

        link.RaiseReceived([0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05]); // 7 字节，不足最小帧长

        Assert.Equal(new byte[] { 0xFE, 0xDC, 0xBA, 0x00, 0xC1, 0x00, 0x05 }, client.SnapshotRxBuffer());
    }
}
