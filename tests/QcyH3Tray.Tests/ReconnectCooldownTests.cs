using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;

namespace QcyH3Tray.Tests;

/// <summary>
/// 阶段 4b 连接冷却/退避测试（真机发现：耳机断开链路后立即重连会被 Windows 拒绝 AccessDenied，
/// 间隔 ≥6-15 秒才稳定）：
/// <list type="bullet">
/// <item>计划内断开后的首次重连至少延迟 <see cref="ConnectionManager.PostDisconnectReconnectDelay"/>；</item>
/// <item>AccessDenied 类失败不按常规 1s 起步退避，单独延迟 <see cref="ConnectionManager.AccessDeniedReconnectDelay"/> 重试；</item>
/// <item>整体行为 = 断开后立即重连失败（拒绝访问）→ 冷却后重试成功。</item>
/// </list>
/// </summary>
public class ReconnectCooldownTests
{
    private const ulong TargetAddress = 0x112233445566;
    private const string TargetName = "QCY H3 Test";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // ───────────────────────── AccessDenied 判定（纯函数） ─────────────────────────

    // UnauthorizedAccessException / 英文 "denied" / 中文「拒绝访问」「访问被拒绝」均判定为 AccessDenied；
    // 普通失败不误判；内部异常链穿透。
    [Theory]
    [InlineData("denied")]
    [InlineData("Access is denied.")]
    [InlineData("拒绝访问")]
    [InlineData("访问被拒绝")]
    public void IsAccessDenied_MessageVariants_True(string message)
        => Assert.True(ConnectionManager.IsAccessDenied(new InvalidOperationException(message)));

    [Fact]
    public void IsAccessDenied_UnauthorizedAccessException_True()
        => Assert.True(ConnectionManager.IsAccessDenied(new UnauthorizedAccessException("whatever")));

    [Fact]
    public void IsAccessDenied_InnerChain_Penetrates()
        => Assert.True(ConnectionManager.IsAccessDenied(new InvalidOperationException(
            "包装", new UnauthorizedAccessException())));

    [Fact]
    public void IsAccessDenied_UnrelatedFailure_False()
        => Assert.False(ConnectionManager.IsAccessDenied(new InvalidOperationException("模拟：设备不可达")));

    // ───────────────────────── 计划内断开的首次重连冷却 ─────────────────────────

    // 断开 → 首次重连不立即发起：传输层创建距断开 ≥ PostDisconnectReconnectDelay（200ms 档）。
    [Fact]
    public async Task Disconnect_FirstReconnect_WaitsPostDisconnectDelay()
    {
        var factory = new FakeTransportFactory();
        var scanner = new FakeScanner();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = new ConnectionManager(factory, scanner)
        {
            PostDisconnectReconnectDelay = TimeSpan.FromMilliseconds(200),
            AccessDeniedReconnectDelay = TimeSpan.FromMilliseconds(50),
            ReconnectBackoff = [TimeSpan.FromMilliseconds(30)],
        };
        manager.SetTarget(TargetAddress, TargetName);
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));

        DateTime disconnectedAt = DateTime.UtcNow;
        ((ScriptedH3Transport)factory.Created[0]).RaiseDisconnected();
        await WaitForStatusPrefixAsync(manager, "连接断开，", TestTimeout);   // 冷却文案（区别于"连接失败"）
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);   // 冷却结束后开始搜索

        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        // 第 2 个传输层的创建时间距断开 ≥ 冷却期（留 20ms 调度余量）
        Assert.Equal(2, factory.Created.Count);
        TimeSpan gap = factory.CreateTimestamps[1] - disconnectedAt;
        Assert.True(gap >= TimeSpan.FromMilliseconds(180), $"断开后首次重连过快：{gap.TotalMilliseconds}ms");
    }

    // ───────────────────────── AccessDenied 专用冷却 ─────────────────────────

    // 断开后立即重连失败（AccessDenied）→ 下一次尝试按专用延迟（150ms 档）而非常规退避（30ms）
    // → 冷却后重试成功（真机「断开→立即重连被拒→退避后成功」场景的假传输层复现）。
    [Fact]
    public async Task Reconnect_AfterAccessDeniedFailure_UsesDedicatedDelay_ThenSucceeds()
    {
        var factory = new FakeTransportFactory();
        var scanner = new FakeScanner();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = new ConnectionManager(factory, scanner)
        {
            PostDisconnectReconnectDelay = TimeSpan.FromMilliseconds(30),
            AccessDeniedReconnectDelay = TimeSpan.FromMilliseconds(150),
            ReconnectBackoff = [TimeSpan.FromMilliseconds(30)],
        };
        manager.SetTarget(TargetAddress, TargetName);
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));

        ((ScriptedH3Transport)factory.Created[0]).RaiseDisconnected();
        await WaitForStatusPrefixAsync(manager, "正在搜索", TestTimeout);
        // 第 1 次重连：Windows 拒绝访问（UnauthorizedAccessException 变体）
        factory.Transports.Enqueue(new ScriptedH3Transport
        {
            ConnectException = new UnauthorizedAccessException("拒绝访问。"),
        });
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStatusPrefixAsync(manager, "系统拒绝访问", TestTimeout);
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);   // 冷却结束后开始搜索

        // 第 2 次重连：按 AccessDenied 专用延迟（150ms）而非常规退避（30ms）
        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        Assert.Equal(3, factory.Created.Count);
        TimeSpan gap = factory.CreateTimestamps[2] - factory.CreateTimestamps[1];
        Assert.True(gap >= TimeSpan.FromMilliseconds(130), $"AccessDenied 重试间隔未走专用冷却：{gap.TotalMilliseconds}ms");
        Assert.Equal(ConnectionState.Connected, manager.State);
    }

    // 连接成功 → AccessDenied 标记清除：后续普通失败回到常规退避（快速重试）。
    // AccessDenied 专用延迟取 600ms（与常规 30ms 拉开 20 倍差），时间断言上界 400ms
    // ——调度抖动（JIT/负载）只会拉大间隔，拉开量程避免上界断言偶发超线。
    [Fact]
    public async Task AccessDeniedFlag_ClearedOnSuccess()
    {
        var factory = new FakeTransportFactory();
        var scanner = new FakeScanner();
        factory.Transports.Enqueue(new ScriptedH3Transport());
        using var manager = new ConnectionManager(factory, scanner)
        {
            PostDisconnectReconnectDelay = TimeSpan.FromMilliseconds(30),
            AccessDeniedReconnectDelay = TimeSpan.FromMilliseconds(600),
            ReconnectBackoff = [TimeSpan.FromMilliseconds(30)],
        };
        manager.SetTarget(TargetAddress, TargetName);
        Assert.True(await manager.ConnectAsync().WaitAsync(TestTimeout));

        ((ScriptedH3Transport)factory.Created[0]).RaiseDisconnected();
        await WaitForStatusPrefixAsync(manager, "正在搜索", TestTimeout);
        factory.Transports.Enqueue(new ScriptedH3Transport
        {
            ConnectException = new UnauthorizedAccessException("拒绝访问。"),
        });
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStatusPrefixAsync(manager, "系统拒绝访问", TestTimeout);
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);

        // 重试成功
        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        // 再次断开：普通失败（非 AccessDenied）→ 常规退避间隔（30ms 档），不再走 150ms 冷却。
        // 同步用传输层出队计数（Connecting 停留 <1ms，5ms 轮询不可观察；Created.Count 单调确定）。
        ((ScriptedH3Transport)factory.Created[2]).RaiseDisconnected();
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);
        factory.Transports.Enqueue(new ScriptedH3Transport { FailConnect = true });
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForConditionAsync(() => factory.Created.Count >= 4, TestTimeout);   // 尝试已启动并出队
        await WaitForStateAsync(manager, ConnectionState.Scanning, TestTimeout);      // 失败 → 退避 → 重新搜索

        factory.Transports.Enqueue(new ScriptedH3Transport());
        scanner.Emit(new BleScanResult(TargetAddress, TargetName, -50, HasQcyVendorData: true));
        await WaitForStateAsync(manager, ConnectionState.Connected, TestTimeout);

        Assert.True(factory.CreateTimestamps.Count >= 5);
        // 最后一次失败（常规退避 30ms）到重试的间隔远小于 AccessDenied 冷却（600ms）
        int last = factory.CreateTimestamps.Count - 1;
        TimeSpan gap = factory.CreateTimestamps[last] - factory.CreateTimestamps[last - 1];
        Assert.True(gap < TimeSpan.FromMilliseconds(400), $"普通失败不应走 AccessDenied 冷却：{gap.TotalMilliseconds}ms");
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
