using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.Tests;

/// <summary>
/// 命令健康语义测试（阶段 2，D1/D10：健康下沉 Core 唯一所有）：
/// 未连接命令不计数、连接态连续失败×2 → Degraded、任一成功恢复、
/// 重连全量同步清健康、写命令串行化（D8）。
/// </summary>
public class CommandHealthTests
{
    private const ulong TargetAddress = 0x112233445566;
    private const string TargetName = "QCY H3 Test";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static ConnectionManager CreateManager(FakeTransportFactory factory, FakeScanner scanner)
        => new(factory, scanner)
        {
            ReconnectBackoff = [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60)],
            PostDisconnectReconnectDelay = TimeSpan.FromMilliseconds(30),   // 冷却默认 2s，测试压到毫秒级
        };

    private static async Task<ScriptedH3Transport> ConnectAsync(ConnectionManager manager, FakeTransportFactory factory)
    {
        factory.Transports.Enqueue(new ScriptedH3Transport());
        manager.SetTarget(TargetAddress, TargetName);
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));
        return Assert.IsType<ScriptedH3Transport>(factory.Created.Single());
    }

    // ───────────────────────── 未连接：不计数 ─────────────────────────

    // 未连接时写命令抛 InvalidOperationException（Core 兜底），不计命令失败、不改状态；
    // AppState 层在此基础上短路（不发起命令）。
    [Fact]
    public async Task WriteCommand_WhenDisconnected_ThrowsWithoutCountingFailure()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        manager.SetTarget(TargetAddress, TargetName);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SetNoiseModeAsync(AncMode.Off).WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SetGameModeAsync(true).WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SetEqV1Async(0x02, 0, []).WaitAsync(TestTimeout));

        Assert.Equal(0, manager.CommandFailures);
        Assert.Equal(ConnectionState.Disconnected, manager.State);
        Assert.Empty(factory.Created);   // 未发起任何连接
    }

    // ───────────────────────── 连接态：失败计数 → Degraded ─────────────────────────

    // 连接态下写命令无应答：失败 ×1 不降级；连续 ×2 → Degraded；
    // 随后任一命令成功 → 恢复 Connected 且计数清零（横幅随之消失）。
    [Fact]
    public async Task ConsecutiveWriteFailures_EnterDegraded_AndRecoverOnSuccess()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.SilentGameModeWrite = true;   // SET 游戏模式无应答（超时后回读也不会发生）

        Assert.Null(await manager.SetGameModeAsync(true).WaitAsync(TestTimeout));   // 失败 ×1
        Assert.Equal(1, manager.CommandFailures);
        Assert.Equal(ConnectionState.Connected, manager.State);                     // 未达阈值不降级

        Assert.Null(await manager.SetGameModeAsync(true).WaitAsync(TestTimeout));   // 失败 ×2
        Assert.Equal(ConnectionState.Degraded, manager.State);
        Assert.Equal(DataValidity.Error, manager.CurrentDeviceData.GameModeValidity);
        Assert.Single(factory.Created);                                             // 链路未重建

        // 任一命令成功 → 恢复
        NoiseModeSetResult? result = await manager.SetNoiseModeAsync(AncMode.Off).WaitAsync(TestTimeout);
        Assert.NotNull(result);
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(0, manager.CommandFailures);
    }

    // 读命令失败同样计数（单次读超时 ≠ 断线：进 Degraded 而不是拆链路）。
    [Fact]
    public async Task ConsecutiveReadFailures_EnterDegraded()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.SilentEqRead = true;   // GET EQ v1 无应答

        Assert.Null(await manager.GetEqV1Async().WaitAsync(TestTimeout));   // 失败 ×1
        Assert.Equal(1, manager.CommandFailures);
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(DataValidity.Error, manager.CurrentDeviceData.EqValidity);

        Assert.Null(await manager.GetEqV1Async().WaitAsync(TestTimeout));   // 失败 ×2
        Assert.Equal(ConnectionState.Degraded, manager.State);
        Assert.Single(factory.Created);                                     // 链路未重建
    }

    // 重连成功完成全量同步 → 命令健康清零（旧会话的失败不计入新会话）。
    [Fact]
    public async Task ReconnectFullSync_ClearsCommandHealth()
    {
        var factory = new FakeTransportFactory();
        var scanner = new FakeScanner();
        using var manager = CreateManager(factory, scanner);
        ScriptedH3Transport first = await ConnectAsync(manager, factory);

        // 制造 Degraded：连续两次 EQ 读失败
        first.SilentEqRead = true;
        Assert.Null(await manager.GetEqV1Async().WaitAsync(TestTimeout));
        Assert.Null(await manager.GetEqV1Async().WaitAsync(TestTimeout));
        Assert.Equal(ConnectionState.Degraded, manager.State);

        // 链路断开 → 自动重连 → 新会话全量同步成功
        factory.Transports.Enqueue(new ScriptedH3Transport());
        first.RaiseDisconnected();
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        Assert.Equal(0, manager.CommandFailures);
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.EqValidity);
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.GameModeValidity);
        Assert.Equal(2, factory.Created.Count);
    }

    // ───────────────────────── 写命令异常路径（阶段 7 补） ─────────────────────────

    // 传输层写入抛异常（如 GATT 会话已被系统释放）与"静默无应答"不同：
    // 异常按契约上抛给调用方（AppState.RunWriteCommandAsync 统一捕获记日志），
    // 同时照常计命令健康账 + 字段标 Error；随后任一成功即清零恢复。
    [Fact]
    public async Task TransportWriteException_Propagates_CountsFailure_MarksError()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.WriteException = new ObjectDisposedException(nameof(ScriptedH3Transport));

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => manager.SetGameModeAsync(true).WaitAsync(TestTimeout));
        Assert.Equal(1, manager.CommandFailures);
        Assert.Equal(ConnectionState.Connected, manager.State);   // 单次失败不降级
        Assert.Equal(DataValidity.Error, manager.CurrentDeviceData.GameModeValidity);

        transport.WriteException = null;
        Assert.True(await manager.SetGameModeAsync(true).WaitAsync(TestTimeout));
        Assert.Equal(0, manager.CommandFailures);   // 成功清零
        Assert.Equal(DataValidity.Available, manager.CurrentDeviceData.GameModeValidity);
        Assert.Single(factory.Created);             // 链路未重建
    }

    // 连续两次写异常同样进入 Degraded（横幅语义），链路保持不重建；
    // 异常消除后下一次成功命令恢复 Connected——与超时路径（SilentXxx）同一状态机语义。
    [Fact]
    public async Task TransportWriteException_Twice_EntersDegraded_LinkKept_ThenRecovers()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);

        transport.WriteException = new InvalidOperationException("模拟：写入瞬间链路已断开");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SetNoiseModeAsync(AncMode.Off).WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SetNoiseModeAsync(AncMode.Off).WaitAsync(TestTimeout));

        Assert.Equal(ConnectionState.Degraded, manager.State);
        Assert.Equal(DataValidity.Error, manager.CurrentDeviceData.NoiseModeValidity);
        Assert.Single(factory.Created);   // 异常不拆链路（真断线由 transport.Disconnected 判定）

        transport.WriteException = null;
        NoiseModeSetResult? result = await manager.SetNoiseModeAsync(AncMode.Off).WaitAsync(TestTimeout);
        Assert.NotNull(result);
        Assert.Equal(ConnectionState.Connected, manager.State);
        Assert.Equal(0, manager.CommandFailures);
    }

    // ───────────────────────── 写命令串行化（D8） ─────────────────────────

    // 并发 5 个 Set 调用（每个含 写→回读 两步、传输层带延迟）：
    // 传输层上任意时刻至多 1 个在途写 → 帧不交错，全部成功，且 SET/GET 严格成对。
    [Fact]
    public async Task ConcurrentSetCommands_SerializeWritesOnTransport()
    {
        var factory = new FakeTransportFactory();
        using var manager = CreateManager(factory, new FakeScanner());
        ScriptedH3Transport transport = await ConnectAsync(manager, factory);
        int framesBefore = transport.WrittenFrames.Count;   // 连接期（握手+初始化读）的帧不计入
        transport.WriteDelay = TimeSpan.FromMilliseconds(15);

        Task<bool?>[] tasks =
        [
            manager.SetGameModeAsync(true),
            manager.SetGameModeAsync(false),
            manager.SetGameModeAsync(true),
            manager.SetGameModeAsync(false),
            manager.SetGameModeAsync(true),
        ];
        bool?[] results = await Task.WhenAll(tasks).WaitAsync(TestTimeout);

        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(1, transport.MaxConcurrentWrites);   // 严格串行：无交错写
        // 每个命令 = SET + 回读 GET，共 10 帧，且 SET/GET 严格交替（写→回读成对）
        var commandFrames = transport.WrittenFrames.Skip(framesBefore).ToList();
        Assert.Equal(10, commandFrames.Count);
        for (int i = 0; i < commandFrames.Count; i += 2)
        {
            Assert.Equal(OpCode.SetAdvInfo, commandFrames[i][4]);
            Assert.Equal(OpCode.GetAdvInfo, commandFrames[i + 1][4]);
        }
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
