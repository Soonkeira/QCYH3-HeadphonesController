using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// <see cref="ConnectionManager"/>（QcyH3Tray.Core.App）状态机测试：注入脚本化传输层
/// 与假扫描器，验证连接成功流程、断线重连、退避序列、幂等连接与耳机主动推送解析。
/// </summary>
public class ConnectionManagerTests
{
    private const ulong TargetAddress = 0x112233445566;
    private const string TargetName = "QCY H3 Test";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>测试用退避序列（毫秒级，避免真实 1s/5s/30s 拖慢测试）。</summary>
    private static readonly TimeSpan[] TestBackoff =
    [
        TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(90),
    ];

    private static ConnectionManager CreateManager(FakeTransportFactory factory, FakeScanner scanner)
        => new(factory, scanner) { ReconnectBackoff = (TimeSpan[])TestBackoff.Clone() };

    // ───────────────────────── 连接成功流程 ─────────────────────────

    // 连接成功：状态 Disconnected → Connecting → Connected；握手帧已发；
    // 初始读数（电量/降噪/EQ v1/游戏模式）全部以事件抛出；共 6 帧写入。
    [Fact]
    public async Task Connect_FullFlow_FiresReadEventsAndReachesConnected()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.AutoReconnect = false;

        var states = new List<ConnectionState>();
        int? battery = null;
        manager.StateChanged += states.Add;
        manager.BatteryChanged += percent => battery = percent;
        manager.DeviceDataChanged += _ => { };

        manager.SetTarget(TargetAddress, TargetName);
        bool connected = await manager.ConnectAsync().WaitAsync(TestTimeout);

        Assert.True(connected);
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal([ConnectionState.Connecting, ConnectionState.Connected], states);
        Assert.Equal(85, battery);

        var transport = Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
        // 第 1 帧 = 握手（NOTIFY_COMMUNICATION_WAY，opCode 0x0B，params 00 00）：
        // [0..2] 帧头 [3]flag [4]opCode [5..6]paramLen [7]sn [8..]params [^1]帧尾
        byte[] handshake = transport.WrittenFrames[0];
        Assert.Equal(OpCode.NotifyCommunicationWay, handshake[4]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, handshake[8..^1]);
        // 握手 + 电量 + 降噪模式 + EQ v1（替换原 v2 读，v1 成功不再回退）+ 游戏模式 = 5 帧
        Assert.Equal(5, transport.WrittenFrames.Count);
        byte[] eqFrame = transport.WrittenFrames[3];           // params 从 [8] 起：FE 01 20
        Assert.Equal(new byte[] { 0xFE, 0x01, EqV1.Cmd }, eqFrame[8..11]);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(4, data.NoiseMode);                       // 17 03 01 01 02 → 降噪高
        Assert.Equal("降噪·高", data.NoiseModeInfo?.DisplayName);
        Assert.Equal((byte)0x02, data.EqV1?.EqType);           // v1 基线：eqType=02，masterGain=-550
        Assert.Equal(-550, data.EqV1?.MasterGain);
        Assert.Equal(EqV1BandMap.Frequencies, data.EqV1?.Bands.Select(band => band.Frequency).ToArray());
        Assert.All(data.EqV1?.Bands ?? [], band => Assert.Equal(0, band.Gain));
        Assert.True(data.GameMode);
    }

    // 未设置目标就连接 → InvalidOperationException。
    [Fact]
    public async Task Connect_WithoutTarget_Throws()
    {
        using var manager = new ConnectionManager(new FakeTransportFactory(), new FakeScanner());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectAsync());
    }

    // 连接中重复调用 ConnectAsync 幂等：返回同一任务，只创建一个传输层。
    [Fact]
    public async Task Connect_ConcurrentCalls_AreIdempotent()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);

        Task<bool> first = manager.ConnectAsync();
        Task<bool> second = manager.ConnectAsync();
        Assert.True(ReferenceEquals(first, second));

        bool[] results = await Task.WhenAll(first, second).WaitAsync(TestTimeout);
        Assert.All(results, Assert.True);
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Single(factory.Created);
    }

    // 降噪模式写入：成功 ack + 回读一致。
    [Fact]
    public async Task SetNoiseMode_AcknowledgedAndReadBack()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);
        await manager.ConnectAsync().WaitAsync(TestTimeout);

        // 假设备对 GET 0x17 固定应答 17 03 01 01 02（降噪高 = 4）
        NoiseModeSetResult? result = await manager.SetNoiseModeAsync(AncMode.NoiseCancelHigh).WaitAsync(TestTimeout);

        Assert.NotNull(result);
        Assert.True(result.Acknowledged);
        Assert.Equal(4, result.ReadBackIndex);
        Assert.Equal(4, manager.CurrentDeviceData.NoiseMode);
    }

    // EQ v1 写入：写 → 回读确认 → DeviceData.EqV1 更新为回读值（脚本设备回显写载荷）。
    [Fact]
    public async Task SetEqV1_AcknowledgedAndReadBack()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);
        await manager.ConnectAsync().WaitAsync(TestTimeout);
        var transport = Assert.IsType<ScriptedH3Transport>(factory.Created.Single());

        var bands = EqV1BandMap.Frequencies
            .Select((frequency, i) => new EqV1Band(frequency, 100 * (i - 3), 120))
            .ToList();
        EqV1Info? result = await manager.SetEqV1Async(0x02, -550, bands).WaitAsync(TestTimeout);

        Assert.NotNull(result);
        Assert.Equal((byte)0x02, result.EqType);
        Assert.Equal(-550, result.MasterGain);
        Assert.Equal(bands, result.Bands.ToList());
        Assert.NotNull(transport.LastEqV1Write);
        Assert.Equal(EqV1.Cmd, transport.LastEqV1Write[0]);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(result, data.EqV1);
    }

    // EQ v1 读取转发：已连接时读并更新快照；未连接返回 null（不抛异常）。
    [Fact]
    public async Task GetEqV1_UpdatesSnapshotAndReturnsNullWhenDisconnected()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);

        Assert.Null(await manager.GetEqV1Async().WaitAsync(TestTimeout));   // 未连接

        await manager.ConnectAsync().WaitAsync(TestTimeout);
        EqV1Info? info = await manager.GetEqV1Async().WaitAsync(TestTimeout);

        Assert.NotNull(info);
        Assert.Equal((byte)0x02, info.EqType);
        Assert.Equal(-550, info.MasterGain);
        Assert.Equal(info, manager.CurrentDeviceData.EqV1);
    }

    // 游戏模式写入：SET_ADV_INFO 生效（脚本设备状态翻转）→ 回读一致 → DeviceData 更新。
    [Fact]
    public async Task SetGameMode_AcknowledgedAndReadBack()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);
        await manager.ConnectAsync().WaitAsync(TestTimeout);
        var transport = Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
        Assert.True(manager.CurrentDeviceData.GameMode);

        bool? result = await manager.SetGameModeAsync(false).WaitAsync(TestTimeout);

        Assert.False(result);
        Assert.Equal(0x01, transport.GameModeState);
        Assert.False(manager.CurrentDeviceData.GameMode);
    }

    // 全量刷新：连接后再各读一遍电量/降噪/EQ/游戏模式（额外 4 帧，电量事件再次触发）。
    [Fact]
    public async Task RefreshAll_RereadsAllValues()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);
        await manager.ConnectAsync().WaitAsync(TestTimeout);
        var transport = Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
        int before = transport.WrittenFrames.Count;
        int refreshCount = 0;
        manager.BatteryChanged += _ => refreshCount++;

        await manager.RefreshAllAsync().WaitAsync(TestTimeout);

        Assert.Equal(before + 4, transport.WrittenFrames.Count);
        Assert.Equal(1, refreshCount);                        // 订阅在连接完成后，RefreshAll 触发一次
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    // ───────────────────────── 耳机主动推送 ─────────────────────────

    // 未匹配 sn 的 NotifyAdvInfo 推送（电量 TLV）→ BatteryChanged；
    // CUSTOM 17 03 … 推送 → 降噪模式更新。
    [Fact]
    public async Task DevicePushNotifications_RaiseEvents()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);
        await manager.ConnectAsync().WaitAsync(TestTimeout);

        var transport = Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
        int? battery = null;
        manager.BatteryChanged += percent => battery = percent;

        // 耳机主动报电量 45%（TLV: L=2 T=0 V=0x2D），随机 sn=0x7F 不会被任何 pending 匹配
        var batteryPush = new Packet(OpCode.NotifyAdvInfo, Packet.TypeCommand, hasResponse: 0, sn: 0x7F,
            parameters: [0x02, AdvType.BatteryQuantity, 0x2D]);
        transport.RaiseReceived(batteryPush.Encode());
        Assert.Equal(0x2D, battery);

        // 耳机主动切到通透模式（17 03 03 01 04 → 索引 1）
        var modePush = new Packet(OpCode.Custom, Packet.TypeCommand, hasResponse: 0, sn: 0x7E,
            parameters: AncMode.Transparency);
        transport.RaiseReceived(modePush.Encode());
        Assert.Equal(1, manager.CurrentDeviceData.NoiseMode);
    }

    // 耳机推送场景组降噪模式（真机抓包 2026-10：17 03 01 03 02 = 室内·高）：
    // NoiseMode 旧索引保持 null（场景组不属于六基础模式），NoiseModeInfo 带完整解码。
    [Fact]
    public async Task DevicePush_SceneNoiseMode_RaisesDecodedInfo()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);
        await manager.ConnectAsync().WaitAsync(TestTimeout);

        var transport = Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
        var modePush = new Packet(OpCode.Custom, Packet.TypeCommand, hasResponse: 0, sn: 0x7D,
            parameters: new byte[] { 0x17, 0x03, 0x01, 0x03, 0x02 });
        transport.RaiseReceived(modePush.Encode());

        DeviceData data = manager.CurrentDeviceData;
        Assert.Null(data.NoiseMode);
        Assert.NotNull(data.NoiseModeInfo);
        Assert.Equal(AncFamily.Indoor, data.NoiseModeInfo!.Family);
        Assert.Equal(2, data.NoiseModeInfo.Level);
        Assert.Equal("室内·高", data.NoiseModeInfo.DisplayName);
    }

    // ───────────────────────── 断线与重连 ─────────────────────────

    // 断线 → 状态 Disconnected → 自动重连进入 Scanning 监听 →
    // 目标广播出现 → 用新传输层重连成功，退避归零。
    [Fact]
    public async Task Disconnect_AutoReconnectsWhenAdvertisementSeen()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.SetTarget(TargetAddress, TargetName);

        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        var transport = Assert.IsType<ScriptedH3Transport>(factory.Created.Single());

        transport.RaiseDisconnected();
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);
        Assert.True(scanner.StartCalls >= 1);

        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    // 名称兜底：目标名留空时，"QCY H3" 前缀的广播（即使地址不同）也触发重连。
    [Fact]
    public async Task Reconnect_NameFallbackPrefix_MatchesQcyAdvertisement()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.SetTarget(0xAABBCCDDDEEE, name: "");

        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        ((ScriptedH3Transport)factory.Created[0]).RaiseDisconnected();

        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);
        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(0x001122334455, "QCY H3 Pro", -60, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        Assert.Equal(2, factory.Created.Count);
    }

    // 连接失败退避序列：连续失败时按 30ms/60ms/90ms 递增重试（封顶）。
    [Fact]
    public async Task ReconnectFailures_EscalateBackoff()
    {
        var factory = new FakeTransportFactory { FailNew = true };
        factory.Transports.Enqueue(new ScriptedH3Transport { FailConnect = true });
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.SetTarget(TargetAddress, TargetName);

        // 第 1 次显式连接失败 → 自动重连循环接管（状态很快进入 Scanning 退避/搜索）
        bool connected = await manager.ConnectAsync().WaitAsync(TestTimeout);
        Assert.False(connected);

        // 投放广播触发两次重连尝试，两次都失败
        for (int i = 0; i < 2; i++)
        {
            await WaitForStatusPrefixAsync(manager, "正在搜索", TestTimeout);
            scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
            // 尝试失败后会再次进入搜索（退避递增），等它离开"正在搜索"再投放
            await WaitForStatusPrefixAsync(manager, "连接失败", TestTimeout);
        }

        // 至少 3 次尝试（1 显式 + 2 重连），间隔分别 ≥ 30ms、≥ 60ms
        Assert.True(factory.CreateTimestamps.Count >= 3, $"尝试次数不足：{factory.CreateTimestamps.Count}");
        TimeSpan firstGap = factory.CreateTimestamps[1] - factory.CreateTimestamps[0];
        TimeSpan secondGap = factory.CreateTimestamps[2] - factory.CreateTimestamps[1];
        Assert.True(firstGap >= TimeSpan.FromMilliseconds(20), $"第一次重连间隔过短：{firstGap.TotalMilliseconds}ms");
        Assert.True(secondGap >= TimeSpan.FromMilliseconds(50), $"第二次重连间隔未升级：{secondGap.TotalMilliseconds}ms");

        manager.Disconnect();
    }

    // 连接成功后断线重连成功 → 退避归零（这里用"断线后立即进入搜索且无退避等待"验证）。
    [Fact]
    public async Task DisconnectAfterSuccess_ResetsBackoff()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.SetTarget(TargetAddress, TargetName);

        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        // 先制造一次失败历史：手动断开并重连成功，退避应清零（无"连接失败，N 秒后重试"状态）
        ((ScriptedH3Transport)factory.Created[0]).RaiseDisconnected();
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);
        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        // 成功重连后再次断线：_consecutiveFailures 已归零 → 直接搜索，不出现退避文案
        ((ScriptedH3Transport)factory.Created[1]).RaiseDisconnected();
        await WaitForStatusPrefixAsync(manager, "正在搜索", TestTimeout);
        Assert.DoesNotContain("连接失败，", manager.CurrentStatus);
    }

    // ───────────────────────── 主动断开 ─────────────────────────

    // Disconnect：回到 Disconnected；AutoReconnect=false 时不启动重连循环；
    // 之后可以再次连接（新传输层）。
    [Fact]
    public async Task Disconnect_StopsAndAllowsReconnect()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        factory.Transports.Enqueue(new ScriptedH3Transport());
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);

        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        manager.Disconnect();
        Assert.Equal(ConnectionState.Disconnected, manager.State);
        Assert.Equal(0, scanner.StartCalls);   // 未触发重连监听

        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        Assert.Equal(2, factory.Created.Count);
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static async Task WaitForStateAsync(ConnectionManager manager, ConnectionState state, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (manager.State != state)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"等待状态 {state} 超时，当前 {manager.State}（{manager.CurrentStatus}）。");
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static async Task WaitForStatusPrefixAsync(ConnectionManager manager, string prefix, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!manager.CurrentStatus.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"等待状态文本「{prefix}…」超时，当前「{manager.CurrentStatus}」。");
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
