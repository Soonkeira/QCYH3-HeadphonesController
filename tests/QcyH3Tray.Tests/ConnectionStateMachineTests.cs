using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 统一连接状态机测试（阶段 2 / plan.md §2.1 九态模型）：
/// 转换合法性矩阵、蓝牙关闭→恢复自动重连（D4）、不可达超时、generation 竞争、
/// 初始化读失败进 Degraded 不掉线（D9）。
/// </summary>
public class ConnectionStateMachineTests
{
    private const ulong TargetAddress = 0x112233445566;
    private const string TargetName = "QCY H3 Test";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] TestBackoff =
    [
        TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(60),
        TimeSpan.FromMilliseconds(90),
    ];

    private static ConnectionManager CreateManager(
        FakeTransportFactory factory, FakeScanner scanner, FakeRadioMonitor? radio = null)
        => new(factory, scanner, radio)
        {
            ReconnectBackoff = (TimeSpan[])TestBackoff.Clone(),
            PostDisconnectReconnectDelay = TimeSpan.FromMilliseconds(30),   // 冷却默认 2s，测试压到毫秒级
        };

    // ───────────────────────── 转换合法性矩阵 ─────────────────────────

    // 全相转换图：每个 (from, to) 组合的合法性必须与规格一致；
    // 同状态重入（from == to）总是允许（仅刷新状态文本，不触发 StateChanged）。
    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Reconnecting, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Scanning, true)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Unreachable, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connecting, true)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Syncing, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Degraded, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.BluetoothOff, true)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Scanning, true)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Connecting, true)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Syncing, false)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Degraded, false)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Unreachable, false)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.BluetoothOff, true)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Scanning, true)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Unreachable, true)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Connecting, true)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Syncing, false)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Scanning, ConnectionState.Degraded, false)]
    [InlineData(ConnectionState.Scanning, ConnectionState.BluetoothOff, true)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Scanning, false)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Unreachable, true)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Connecting, true)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Syncing, false)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.Degraded, false)]
    [InlineData(ConnectionState.Unreachable, ConnectionState.BluetoothOff, true)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Scanning, false)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Unreachable, false)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connecting, true)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Syncing, true)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Degraded, false)]
    [InlineData(ConnectionState.Connecting, ConnectionState.BluetoothOff, true)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Scanning, false)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Unreachable, false)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Syncing, true)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Syncing, ConnectionState.Degraded, true)]
    [InlineData(ConnectionState.Syncing, ConnectionState.BluetoothOff, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Scanning, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Unreachable, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Syncing, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Degraded, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.BluetoothOff, false)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Scanning, false)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Unreachable, false)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Syncing, false)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Degraded, true)]
    [InlineData(ConnectionState.Degraded, ConnectionState.BluetoothOff, false)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Disconnected, true)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Scanning, true)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Unreachable, false)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Connecting, true)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Syncing, false)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.Degraded, false)]
    [InlineData(ConnectionState.BluetoothOff, ConnectionState.BluetoothOff, true)]
    public void IsLegalTransition_MatchesSpec(ConnectionState from, ConnectionState to, bool expected)
    {
        Assert.Equal(expected, ConnectionManager.IsLegalTransition(from, to));
    }

    // ───────────────────────── 蓝牙关闭 → 恢复（D4） ─────────────────────────

    // 蓝牙未开：扫描 Start 抛 BluetoothUnavailableException → 进入 BluetoothOff
    // （旧实现循环直接退出，永不恢复）；radio 恢复事件 → 自动回到重连流程并连上。
    [Fact]
    public async Task BluetoothOff_OnScannerStartFailure_AutoRecoversWhenRadioReturns()
    {
        var factory = new FakeTransportFactory { FailNew = true };
        var scanner = new FakeScanner { FailStart = true };
        var radio = new FakeRadioMonitor();
        using var manager = CreateManager(factory, scanner, radio);
        manager.SetTarget(TargetAddress, TargetName);

        Assert.False(await manager.ConnectAsync().WaitAsync(TestTimeout));   // 显式连接失败 → 重连循环
        await WaitForStateAsync(manager, ConnectionState.BluetoothOff, TestTimeout);
        Assert.True(radio.StartCalls >= 1);   // 已开始监视 radio

        // radio 恢复 → 自动重启重连流程（不再需要手动"立即重连"）
        scanner.FailStart = false;
        radio.SetAvailable(true);
        await WaitForStatusPrefixAsync(manager, "正在搜索", TestTimeout);

        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    // 重连等待期间 radio 消失（monitor 报告 false）→ 直接进入 BluetoothOff（不再空转扫描）。
    [Fact]
    public async Task BluetoothOff_MonitorReportsOffWhileReconnecting_EntersBluetoothOff()
    {
        var factory = new FakeTransportFactory { FailNew = true };
        var scanner = new FakeScanner();
        var radio = new FakeRadioMonitor();
        using var manager = CreateManager(factory, scanner, radio);
        manager.SetTarget(TargetAddress, TargetName);

        Assert.False(await manager.ConnectAsync().WaitAsync(TestTimeout));
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);

        radio.SetAvailable(false);
        await WaitForStateAsync(manager, ConnectionState.BluetoothOff, TestTimeout);

        // 恢复后自动重连
        factory.Transports.Enqueue(new ScriptedH3Transport());
        radio.SetAvailable(true);
        await WaitForStatusPrefixAsync(manager, "正在搜索", TestTimeout);
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);
    }

    // BluetoothOff 状态下显式 ConnectAsync（用户点"立即重连"）也允许直接发起连接。
    [Fact]
    public async Task BluetoothOff_ExplicitConnect_StartsConnecting()
    {
        var factory = new FakeTransportFactory { FailNew = true };
        var scanner = new FakeScanner { FailStart = true };
        var radio = new FakeRadioMonitor();
        using var manager = CreateManager(factory, scanner, radio);
        manager.SetTarget(TargetAddress, TargetName);

        Assert.False(await manager.ConnectAsync().WaitAsync(TestTimeout));
        await WaitForStateAsync(manager, ConnectionState.BluetoothOff, TestTimeout);

        scanner.FailStart = false;
        factory.Transports.Enqueue(new ScriptedH3Transport());
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    // ───────────────────────── 耳机不可达 ─────────────────────────

    // 搜索超过 UnreachableAfter 仍未见到目标广播 → Unreachable（继续监听）；
    // 广播出现后正常连接。
    [Fact]
    public async Task Unreachable_SearchTimeout_KeepsListeningAndRecoversOnAdvertisement()
    {
        var factory = new FakeTransportFactory { FailNew = true };
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.UnreachableAfter = TimeSpan.FromMilliseconds(120);
        manager.SetTarget(TargetAddress, TargetName);

        var states = new List<ConnectionState>();
        manager.StateChanged += states.Add;
        Assert.False(await manager.ConnectAsync().WaitAsync(TestTimeout));

        await WaitForStateAsync(manager, ConnectionState.Unreachable, TestTimeout);
        Assert.Contains(ConnectionState.Unreachable, states);
        Assert.Contains("耳机不可达", manager.CurrentStatus);

        // 耳机重新出现（如开机/出盒）→ 监听到广播 → 连接（先失败一次后成功）
        await WaitForStatusPrefixAsync(manager, "耳机不可达", TestTimeout);
        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);
        manager.Disconnect();
    }

    // ───────────────────────── generation 竞争 ─────────────────────────

    // 旧连接任务（被 Disconnect/新会话取代）迟到完成时：结果为 false、
    // 不得改动状态、不得安装会话（旧任务结果不覆盖新会话）。
    [Fact]
    public async Task Generation_StaleConnectResult_DoesNotOverrideNewSession()
    {
        var factory = new FakeTransportFactory();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Transports.Enqueue(new ScriptedH3Transport { ConnectGate = gate });
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);

        Task<bool> staleTask = manager.ConnectAsync();
        await WaitForStateAsync(manager, ConnectionState.Connecting, TestTimeout);

        manager.Disconnect();   // 代数 +1：旧流程过期
        Assert.Equal(ConnectionState.Disconnected, manager.State);

        factory.Transports.Enqueue(new ScriptedH3Transport());
        Task<bool> newTask = manager.ConnectAsync();   // 新会话（新代数）
        Assert.True(await newTask.WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Connected, manager.State);

        gate.TrySetResult(true);   // 旧流程此时才恢复执行
        Assert.False(await staleTask.WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Connected, manager.State);   // 未被覆盖
        Assert.Equal(2, factory.Created.Count);                   // 未创建多余传输层
    }

    // ───────────────────────── 初始化读容错（D9） ─────────────────────────

    // 握手后初始化读连续失败 ≥ 阈值（失败项位于同步末尾，中间无成功重置计数）：
    // 进入 Degraded（链路保持，绝不断开），失败项标 Error、成功项 Available；
    // 之后任一命令成功即恢复 Connected。
    [Fact]
    public async Task InitReadFailures_EnterDegraded_KeepLinkAndRecoverOnSuccess()
    {
        var factory = new FakeTransportFactory();
        // 阶段 4b 起初始化读含自动关机（同步末尾新增读）；连续失败场景需一并静音，
        // 否则末尾成功的读会按既有语义（任一成功清零）打断"连续失败"链。
        factory.Transports.Enqueue(new ScriptedH3Transport
        {
            SilentEqRead = true,
            SilentGameModeRead = true,
            SilentAutoOffRead = true,
        });
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);

        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Degraded, manager.State);   // 不掉线
        Assert.True(manager.CommandFailures >= ConnectionManager.CommandFailureThreshold,
            $"命令失败计数应达阈值：{manager.CommandFailures}");
        Assert.Single(factory.Created);                          // 链路未重建

        DeviceData data = manager.CurrentDeviceData;
        Assert.Equal(DataValidity.Error, data.EqValidity);
        Assert.Equal(DataValidity.Error, data.GameModeValidity);
        Assert.Equal(DataValidity.Available, data.BatteryValidity);
        Assert.Equal(DataValidity.Available, data.NoiseModeValidity);
        Assert.Equal(85, data.BatteryPercent);                   // 成功项照常可用

        // 任一命令成功 → 恢复 Connected
        NoiseModeSetResult? result = await manager.SetNoiseModeAsync(AncMode.Transparency).WaitAsync(TestTimeout);
        Assert.NotNull(result);
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(0, manager.CommandFailures);
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.NoiseModeValidity);
    }

    // 单项初始化读失败（低于阈值）：仍进 Connected，失败字段标 Error（不是默认值）。
    [Fact]
    public async Task InitReadFailure_BelowThreshold_EntersConnectedWithErrorField()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport { SilentGameModeRead = true });
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);

        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(0, manager.CommandFailures);                // Connected 转换强制 ResetHealth
        Assert.Equal(DataValidity.Error, manager.CurrentDeviceData.GameModeValidity);
        Assert.Null(manager.CurrentDeviceData.GameMode);         // 不落默认值（绝不当"关"）
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.BatteryValidity);
    }

    // 连接成功路径必须先经过 Syncing（状态事件先于数据事件，D9 前半）。
    [Fact]
    public async Task Connect_StateOrder_PassesThroughSyncingBeforeConnected()
    {
        var factory = new FakeTransportFactory();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = CreateManager(factory, new FakeScanner());
        manager.AutoReconnect = false;
        manager.SetTarget(TargetAddress, TargetName);

        var states = new List<ConnectionState>();
        manager.StateChanged += states.Add;
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));

        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.Syncing, ConnectionState.Connected],
            states);
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
