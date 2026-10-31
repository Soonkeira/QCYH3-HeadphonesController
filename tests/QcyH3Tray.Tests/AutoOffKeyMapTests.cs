using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 阶段 4b 功能命令测试（真机已裁决：自动关机 0x14 / 按键映射 type=02 / 提示音 3D / 查找耳机 4B）：
/// 写→回读确认与乐观快照、validity 流转（断线 Stale、失败 Error 不落默认值）、
/// ack 判定路径与命令健康记账（假传输层）。
/// </summary>
public class AutoOffKeyMapTests
{
    private const ulong TargetAddress = 0x112233445566;
    private const string TargetName = "QCY H3 Test";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static ConnectionManager CreateManager(FakeTransportFactory factory, FakeScanner scanner)
        => new(factory, scanner) { AutoReconnect = false };

    private static async Task<ScriptedH3Transport> ConnectAsync(ConnectionManager manager, FakeTransportFactory factory)
    {
        factory.Transports.Enqueue(new ScriptedH3Transport());
        manager.SetTarget(TargetAddress, TargetName);
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        return Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
    }

    // ───────────────────────── 自动关机（0x14） ─────────────────────────

    // 未连接读：返回 null（不抛异常、不发起命令）。
    [Fact]
    public async Task GetPowerManager_Disconnected_ReturnsNull()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        manager.SetTarget(TargetAddress, TargetName);

        Assert.Null(await manager.GetPowerManagerAsync().WaitAsync(TestTimeout));
        Assert.Empty(factory.Created);
    }

    // 写入 30 分钟：写帧 = 14 04 1E 00 00 00（u16LE 分钟 + 当前计时 0）→ 回读一致 → 快照更新。
    [Fact]
    public async Task SetPowerManager_AcknowledgedAndReadBack()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        PowerManagerInfo? result = await manager.SetPowerManagerAsync(30).WaitAsync(TestTimeout);

        Assert.NotNull(result);
        Assert.Equal(30, result.PowerOffMinutes);
        byte[] write = transport.WrittenFrames
            .Single(frame => frame.Length >= 14 && frame[4] == OpCode.Custom
                && frame[8] == PowerManager.Cmd && frame[9] == PowerManager.DataLength);
        Assert.Equal(new byte[] { 0x14, 0x04, 0x1E, 0x00, 0x00, 0x00 }, write[8..14]);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(30, data.AutoOffMinutes);
        Assert.Equal(DataValidity.Available, data.AutoOffValidity);
        Assert.NotNull(data.AutoOffUpdatedUtc);
    }

    // 写 ack 但回读无响应：用写入值做乐观快照（模式对齐 SetEqV1Async）。
    [Fact]
    public async Task SetPowerManager_NoReadBack_OptimisticSnapshot()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.SilentAutoOffRead = true;
        PowerManagerInfo? result = await manager.SetPowerManagerAsync(15).WaitAsync(TestTimeout);

        Assert.NotNull(result);
        Assert.Equal(15, result.PowerOffMinutes);
        Assert.Equal(15, manager.CurrentDeviceData.AutoOffMinutes);
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.AutoOffValidity);
    }

    // 写无 ack：返回 null、validity 转 Error 且保留旧值（绝不落默认值）。
    [Fact]
    public async Task SetPowerManager_NoAck_ReturnsNull_MarksError_KeepsValue()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        Assert.Equal(PowerManagerInfo.NeverOffMinutes, manager.CurrentDeviceData.AutoOffMinutes);

        transport.SilentAutoOffWrite = true;
        Assert.Null(await manager.SetPowerManagerAsync(30).WaitAsync(TestTimeout));

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Error, data.AutoOffValidity);
        Assert.Equal(PowerManagerInfo.NeverOffMinutes, data.AutoOffMinutes);   // 旧值保留
    }

    // 读失败（RefreshAll 中自动关机读无响应）：Error 且保留旧值，其它读不受影响。
    [Fact]
    public async Task AutoOffReadFailure_MarksError_KeepsValue()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.SilentAutoOffRead = true;
        await manager.RefreshAllAsync().WaitAsync(TestTimeout);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Error, data.AutoOffValidity);
        Assert.Equal(PowerManagerInfo.NeverOffMinutes, data.AutoOffMinutes);
        Assert.Equal(DataValidity.Available, data.EqValidity);
    }

    // 断线：AutoOff 与 KeyMap 一并转 Stale、值保留（MarkAllStale 全量语义）。
    [Fact]
    public async Task Disconnect_MarksAutoOffAndKeyMapStale_KeepsValues()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        Assert.NotNull(await manager.GetKeyMappingsAsync().WaitAsync(TestTimeout));

        transport.RaiseDisconnected();
        await WaitForStateAsync(manager, ConnectionState.Disconnected, TestTimeout);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Stale, data.AutoOffValidity);
        Assert.Equal(DataValidity.Stale, data.KeyMapValidity);
        Assert.Equal(PowerManagerInfo.NeverOffMinutes, data.AutoOffMinutes);
        Assert.NotNull(data.KeyMap);
    }

    // ───────────────────────── 按键映射（type=02 整表） ─────────────────────────

    // 未连接读：返回 null。
    [Fact]
    public async Task GetKeyMappings_Disconnected_ReturnsNull()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        manager.SetTarget(TargetAddress, TargetName);

        Assert.Null(await manager.GetKeyMappingsAsync().WaitAsync(TestTimeout));
    }

    // 读整表：快照 KeyMap = 设备表（含出厂槽），Available。
    [Fact]
    public async Task GetKeyMappings_ReturnsFullTable_AndUpdatesSnapshot()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        List<KeyMappingEntry>? entries = await manager.GetKeyMappingsAsync().WaitAsync(TestTimeout);

        Assert.NotNull(entries);
        Assert.Equal(transport.KeyTable, entries);
        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(entries, data.KeyMap);
        Assert.Equal(DataValidity.Available, data.KeyMapValidity);
    }

    // 整表写：改一条可编辑行 → 写帧 LTV = [len=1+3N][0x02][整表] → 回读一致 → 快照更新；
    // 出厂槽逐字节不变（整表写守卫的数据面）。
    [Fact]
    public async Task SetKeyMappings_AcknowledgedAndReadBack_FactoryRowsUntouched()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        transport.KeyTable =
        [
            new(0x07, 0x01, 0x01), new(0x07, 0x02, 0x07),
            new(0x03, 0x01, 0x06), new(0x03, 0x02, 0x02),
            new(0x07, 0x81, 0x01), new(0x03, 0x81, 0x05),      // 出厂槽
        ];

        var edited = new List<KeyMappingEntry>(transport.KeyTable)
        {
            [0] = new KeyMappingEntry(0x07, 0x01, 0x03),        // 单击 → 下一曲
        };
        IReadOnlyList<KeyMappingEntry>? result = await manager.SetKeyMappingsAsync(edited).WaitAsync(TestTimeout);

        Assert.NotNull(result);
        Assert.Equal(edited, result);

        // 写帧：SET_ADV_INFO params = [len][0x02][整表]
        byte[] write = transport.WrittenFrames
            .Single(frame => frame.Length > 9 && frame[4] == OpCode.SetAdvInfo && frame[9] == AdvType.KeySettings);
        Assert.Equal(1 + 6 * KeyMap.BytesPerEntry, write[8]);
        Assert.Equal(KeyMap.BuildSetAdvInfoPayload(edited), write[8..^1]);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(result, data.KeyMap);
        Assert.Equal(DataValidity.Available, data.KeyMapValidity);
        // 出厂槽原样
        Assert.Contains(new KeyMappingEntry(0x07, 0x81, 0x01), data.KeyMap!);
        Assert.Contains(new KeyMappingEntry(0x03, 0x81, 0x05), data.KeyMap!);
    }

    // 写无 ack：null + Error + 旧表保留。
    [Fact]
    public async Task SetKeyMappings_NoAck_MarksError_KeepsTable()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        List<KeyMappingEntry> original = await manager.GetKeyMappingsAsync().WaitAsync(TestTimeout)
            ?? throw new InvalidOperationException("前置读取失败。");

        transport.SilentKeyMapWrite = true;
        Assert.Null(await manager.SetKeyMappingsAsync(original).WaitAsync(TestTimeout));

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Error, data.KeyMapValidity);
        Assert.Equal(original, data.KeyMap);
    }

    // ───────────────────────── 提示音 / 查找耳机（写 + ack，无回读） ─────────────────────────

    // 播放提示音 toneID=1：帧 = 3D 01 01（Quicky TonePlay.go），ack（status=0）→ true。
    [Fact]
    public async Task PlayTone_GoldenFrame_Acknowledged()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        Assert.True(await manager.PlayToneAsync(1).WaitAsync(TestTimeout));
        byte[] frame = transport.WrittenFrames[^1];
        Assert.Equal(OpCode.Custom, frame[4]);
        Assert.Equal(new byte[] { ToneCommands.PlayToneCmd, 0x01, 0x01 }, frame[8..11]);
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    // 查找耳机：帧 = 4B 01 01（qcy-h3s find_earphones），ack → true。
    [Fact]
    public async Task FindEarbuds_GoldenFrame_Acknowledged()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        Assert.True(await manager.FindEarbudsAsync().WaitAsync(TestTimeout));
        byte[] frame = transport.WrittenFrames[^1];
        Assert.Equal(new byte[] { ToneCommands.FindEarbudsCmd, 0x01, 0x01 }, frame[8..11]);
    }

    // 写无 ack：false 且计入命令健康（连续 ×2 → Degraded，链路不重建）。
    [Fact]
    public async Task ToneSilentWrite_ReturnsFalse_CountsCommandHealth()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        transport.SilentToneFindWrite = true;

        Assert.False(await manager.PlayToneAsync(1).WaitAsync(TestTimeout));
        Assert.Equal(1, manager.CommandFailures);
        Assert.False(await manager.FindEarbudsAsync().WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Degraded, manager.State);
        Assert.Single(factory.Created);
    }

    // 未连接：ack 类命令抛 InvalidOperationException（D1 短路语义，不计命令失败）。
    [Fact]
    public async Task ToneCommands_WhenDisconnected_Throw()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        manager.SetTarget(TargetAddress, TargetName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PlayToneAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.FindEarbudsAsync());
        Assert.Equal(0, manager.CommandFailures);
        Assert.Empty(factory.Created);
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
}
