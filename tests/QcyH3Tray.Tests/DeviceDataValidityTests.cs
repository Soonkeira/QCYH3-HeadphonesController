using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 设备数据有效性语义测试（阶段 2，plan.md §2.2）：
/// 断线全量转 Stale（值保留）、读失败标 Error 不落默认值、游戏模式未知值 → Unknown
/// （绝不显示为关）、快照整体原子发布、同步中 Loading 可观察。
/// </summary>
public class DeviceDataValidityTests
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

    // 断线：所有已读字段 Available → Stale，但值全部保留（UI 按 validity 置灰，不当作现值）。
    [Fact]
    public async Task Disconnect_MarksAllFieldsStale_KeepsValues()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.RaiseDisconnected();
        await WaitForStateAsync(manager, ConnectionState.Disconnected, TestTimeout);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Stale, data.BatteryValidity);
        Assert.Equal(DataValidity.Stale, data.NoiseModeValidity);
        Assert.Equal(DataValidity.Stale, data.EqValidity);
        Assert.Equal(DataValidity.Stale, data.GameModeValidity);
        // 值保留
        Assert.Equal(85, data.BatteryPercent);
        Assert.Equal(4, data.NoiseMode);
        Assert.NotNull(data.EqV1);
        Assert.True(data.GameMode);
    }

    // 从未读到过的字段断线后保持 Unknown（不伪造 Stale）。
    [Fact]
    public void Disconnect_UnknownFieldsStayUnknown()
    {
        var factory = new FakeTransportFactory();
        using var fresh = CreateManager(factory, new FakeScanner());
        fresh.SetTarget(TargetAddress, TargetName);
        // 从未连接：快照全 Unknown
        DeviceData data = fresh.CurrentDeviceData;
        Assert.Equal(DataValidity.Unknown, data.BatteryValidity);
        Assert.Equal(DataValidity.Unknown, data.NoiseModeValidity);
        Assert.Equal(DataValidity.Unknown, data.EqValidity);
        Assert.Equal(DataValidity.Unknown, data.GameModeValidity);
        fresh.Disconnect();   // 无会话断开：仍 Unknown
        Assert.Equal(DataValidity.Unknown, fresh.CurrentDeviceData.BatteryValidity);
    }

    // 读失败 → Error 且保留旧值（不落默认值，不显示为关）。
    [Fact]
    public async Task ReadFailure_KeepsPreviousValue_MarksError()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.SilentGameModeRead = true;
        await manager.RefreshAllAsync().WaitAsync(TestTimeout);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Error, data.GameModeValidity);
        Assert.True(data.GameMode);   // 旧值保留：绝不变成默认"关"
        Assert.Equal(DataValidity.Available, data.BatteryValidity);
        Assert.Equal(DataValidity.Available, data.EqValidity);
    }

    // 游戏模式读到协议未知值（非 0x01/0x02）→ Unknown（值清空，绝不显示为关）。
    [Fact]
    public async Task GameModeUnknownValue_BecomesUnknown_NotOff()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        Assert.True(manager.CurrentDeviceData.GameMode);   // 初始读到开

        var unknownPush = new Packet(OpCode.NotifyAdvInfo, Packet.TypeCommand, hasResponse: 0, sn: 0x7F,
            parameters: [0x02, AdvType.WorkMode, 0x07]);
        transport.RaiseReceived(unknownPush.Encode());

        DeviceData data = manager.CurrentDeviceData;
        Assert.Null(data.GameMode);
        Assert.Equal(DataValidity.Unknown, data.GameModeValidity);
    }

    // 快照整体发布：一次更新只发一个事件，事件内既有新值也有未变的旧值（无半新半旧）。
    [Fact]
    public async Task Snapshot_PublishedAtomically()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        int events = 0;
        DeviceData? last = null;
        manager.DeviceDataChanged += data =>
        {
            events++;
            last = data;
        };

        var batteryPush = new Packet(OpCode.NotifyAdvInfo, Packet.TypeCommand, hasResponse: 0, sn: 0x7F,
            parameters: [0x02, AdvType.BatteryQuantity, 0x2D]);
        transport.RaiseReceived(batteryPush.Encode());

        Assert.Equal(1, events);
        Assert.NotNull(last);
        Assert.Equal(0x2D, last.BatteryPercent);
        Assert.Equal(DataValidity.Available, last.BatteryValidity);
        Assert.Equal(4, last.NoiseMode);                                        // 未变字段随快照一起到达
        Assert.Equal(DataValidity.Available, last.NoiseModeValidity);
        Assert.Equal(DataValidity.Available, last.GameModeValidity);
    }

    // 同步相可观察 Loading：EQ 读挂起期间 state=Syncing、EQ=Loading、先到的读已 Available。
    [Fact]
    public async Task Syncing_ObservesLoadingBeforeReadsComplete()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedH3Transport
        {
            ResponseGate = gate,
            ResponseGatePredicate = packet =>
                packet.OpCode == OpCode.Custom && packet.Params is [0xFE, 0x01, EqV1.Cmd],
        };
        factory.Transports.Enqueue(transport);
        manager.SetTarget(TargetAddress, TargetName);

        Task<bool> connecting = manager.ConnectAsync();
        await WaitForStateAsync(manager, ConnectionState.Syncing, TestTimeout);
        await WaitForConditionAsync(
            () => manager.CurrentDeviceData.EqValidity == DataValidity.Loading, TestTimeout);

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Loading, data.EqValidity);
        Assert.Equal(DataValidity.Available, data.BatteryValidity);   // 先完成的读已是现值
        Assert.Equal(DataValidity.Available, data.NoiseModeValidity);
        Assert.Equal(DataValidity.Loading, data.GameModeValidity);    // 尚未读到的保持 Loading（同步开始时统一转 Loading）

        gate.TrySetResult(true);
        Assert.True(await connecting.WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.EqValidity);
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.GameModeValidity);
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

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待条件超时。");
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
