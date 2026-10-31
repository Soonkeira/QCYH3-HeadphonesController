using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;

namespace QcyH3Tray.Tests;

/// <summary>
/// 资源释放测试（阶段 7 补，plan.md D15 遗留缺口之一）：
/// <see cref="ConnectionManager.Dispose"/> 与在途连接任务 / 重连等待循环 / 已建立会话的竞争——
/// 释放不得挂死、不得抛异常、过期代数静默退出不覆盖已发布状态、释放后入口按契约拒绝。
/// </summary>
public class ResourceReleaseTests
{
    private const ulong TargetAddress = 0x112233445566;
    private const string TargetName = "QCY H3 Test";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static ConnectionManager CreateManager(FakeTransportFactory factory, FakeScanner scanner)
        => new(factory, scanner)
        {
            ReconnectBackoff = [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60)],
            PostDisconnectReconnectDelay = TimeSpan.FromMilliseconds(30),
        };

    private static async Task<ScriptedH3Transport> ConnectAsync(ConnectionManager manager, FakeTransportFactory factory)
    {
        factory.Transports.Enqueue(new ScriptedH3Transport());
        manager.SetTarget(TargetAddress, TargetName);
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        return Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
    }

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

    // ───────────────────────── 连接进行中释放（fake 传输挂起） ─────────────────────────

    // GATT 连接挂在传输层（真实场景：系统栈无响应）。Dispose 必须：
    // 立即返回不挂死；状态发布为 Disconnected"已释放。"；此后挂起的连接完成时
    // 按过期代数静默退出——不改状态、不进重连。Dispose 幂等。
    [Fact]
    public async Task Dispose_DuringConnectingWithHungTransport_ReturnsPromptlyAndStaleAttemptExits()
    {
        var factory = new FakeTransportFactory();
        var scanner = new FakeScanner();
        var manager = CreateManager(factory, scanner);
        var connectGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Transports.Enqueue(new ScriptedH3Transport { ConnectGate = connectGate });
        manager.SetTarget(TargetAddress, TargetName);

        Task<bool> connectTask = manager.ConnectAsync();
        await WaitForStateAsync(manager, ConnectionState.Connecting, TestTimeout);

        await Task.Run(manager.Dispose).WaitAsync(TestTimeout);   // 后台跑同步 Dispose：挂死则此处超时失败

        Assert.Equal(ConnectionState.Disconnected, manager.State);
        Assert.Equal("已释放。", manager.CurrentStatus);

        // 释放挂起连接：过期代数静默退出，不触发重连、不覆盖状态
        connectGate.TrySetResult(true);
        Assert.False(await connectTask.WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Disconnected, manager.State);
        Assert.Equal("已释放。", manager.CurrentStatus);
        Assert.Single(factory.Created);

        manager.Dispose();   // 幂等：二次释放直接返回
        Assert.Equal(ConnectionState.Disconnected, manager.State);
    }

    // 已连接（电量轮询在跑、会话 CTS 活跃）时释放：会话拆除 → 数据全量转 Stale、
    // 状态 Disconnected"已释放。"；释放后连接/断开入口按契约抛 ObjectDisposedException，
    // 刷新与广播尾巴是安全空操作（退出链上的竞态不得抛异常）。
    [Fact]
    public async Task Dispose_WhileConnected_TearsSessionDown_RejectsRestart_KeepsTailSafe()
    {
        var factory = new FakeTransportFactory();
        var scanner = new FakeScanner();
        var manager = CreateManager(factory, scanner);
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.BatteryValidity);

        await Task.Run(manager.Dispose).WaitAsync(TestTimeout);   // 后台跑同步 Dispose：挂死则此处超时失败

        Assert.Equal(ConnectionState.Disconnected, manager.State);
        Assert.Equal("已释放。", manager.CurrentStatus);
        Assert.Equal(DataValidity.Stale, manager.CurrentDeviceData.BatteryValidity);   // 断线失效语义同样适用

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ConnectAsync().WaitAsync(TestTimeout));
        Assert.Throws<ObjectDisposedException>(manager.Disconnect);

        await manager.RefreshAllAsync().WaitAsync(TestTimeout);          // 无会话 → 安全空操作
        Assert.Null(await manager.GetEqV1Async().WaitAsync(TestTimeout));
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));   // 迟到广播：无信号槽 → 忽略
        Assert.Equal(ConnectionState.Disconnected, manager.State);
    }

    // 重连退避等待中释放：循环被取消——等待窗口内不再产生任何新的连接尝试
    //（若取消失败，30/60ms 退避下 200ms 内必然出现多次新 attempt）。
    [Fact]
    public async Task Dispose_DuringReconnectBackoff_StopsLoop_NoFurtherAttempts()
    {
        var factory = new FakeTransportFactory { FailNew = true };
        var manager = CreateManager(factory, new FakeScanner());
        manager.SetTarget(TargetAddress, TargetName);
        Assert.False(await manager.ConnectAsync().WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Reconnecting, manager.State);

        await Task.Run(manager.Dispose).WaitAsync(TestTimeout);   // 后台跑同步 Dispose：挂死则此处超时失败
        int attemptsAtDispose = factory.Created.Count;

        await Task.Delay(200).WaitAsync(TestTimeout);
        Assert.Equal(ConnectionState.Disconnected, manager.State);
        Assert.Equal(attemptsAtDispose, factory.Created.Count);   // 释放后零新尝试
    }
}
