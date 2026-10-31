using QcyH3Tray.App;
using QcyH3Tray.Core.App;

namespace QcyH3Tray.Tests;

/// <summary>
/// 低电量提醒判定纯函数测试（阶段 6）：阈值档位、边沿触发、会话去重、迟滞恢复、
/// 无效电量保持、会话边界（断开/重连）重置。
/// </summary>
public class LowBatteryAdvisorTests
{
    private static (bool Notify, LowBatteryPhase Phase) Evaluate(
        LowBatteryPhase phase,
        int threshold,
        int? percent,
        DataValidity validity = DataValidity.Available,
        bool sessionActive = true)
        => LowBatteryAdvisor.Evaluate(phase, threshold, validity, percent, sessionActive);

    // ───────────────────────── 阈值清洗 ─────────────────────────

    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    public void ClampThreshold_PresetValues_PassThrough(int threshold)
        => Assert.Equal(threshold, LowBatteryAdvisor.ClampThreshold(threshold));

    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(100)]
    public void ClampThreshold_InRangeHandEditedValues_PassThrough(int threshold)
    {
        // UI 只提供 10/15/20/30 档位；手改配置文件 1–100 区间内的值照常生效
        // （冒烟验证路径：阈值临时改 100 触发气泡）
        Assert.Equal(threshold, LowBatteryAdvisor.ClampThreshold(threshold));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    [InlineData(101)]
    [InlineData(500)]
    public void ClampThreshold_OutOfRangeValues_FallBackToDefault(int threshold)
        => Assert.Equal(LowBatteryAdvisor.DefaultThresholdPercent, LowBatteryAdvisor.ClampThreshold(threshold));

    [Fact]
    public void DefaultThreshold_Is20()
        => Assert.Equal(20, LowBatteryAdvisor.DefaultThresholdPercent);

    // ───────────────────────── 触发与去重 ─────────────────────────

    [Fact]
    public void BatteryAtThreshold_Armed_FiresOnce()
    {
        (bool notify, LowBatteryPhase phase) = Evaluate(LowBatteryPhase.Armed, threshold: 20, percent: 20);
        Assert.True(notify);
        Assert.Equal(LowBatteryPhase.Fired, phase);
    }

    [Fact]
    public void BatteryBelowThreshold_Armed_FiresOnce()
    {
        (bool notify, LowBatteryPhase phase) = Evaluate(LowBatteryPhase.Armed, threshold: 20, percent: 7);
        Assert.True(notify);
        Assert.Equal(LowBatteryPhase.Fired, phase);
    }

    [Fact]
    public void BatteryStillLow_Fired_DoesNotRepeat()
    {
        // 同一连接会话内持续低电量（15s 轮询反复上报）：只提醒一次
        foreach (int percent in new[] { 19, 18, 15, 12, 5, 3 })
        {
            (bool notify, LowBatteryPhase phase) = Evaluate(
                LowBatteryPhase.Fired, threshold: 20, percent: percent);
            Assert.False(notify);
            Assert.Equal(LowBatteryPhase.Fired, phase);
        }
    }

    [Fact]
    public void BatteryAboveThresholdButBelowHysteresis_PhaseUnchanged()
    {
        // 阈值之上、迟滞线之下（20+10=30）：不触发也不重新武装（防临界抖动）
        (bool notify1, LowBatteryPhase phase1) = Evaluate(LowBatteryPhase.Fired, threshold: 20, percent: 29);
        Assert.False(notify1);
        Assert.Equal(LowBatteryPhase.Fired, phase1);

        (bool notify2, LowBatteryPhase phase2) = Evaluate(LowBatteryPhase.Armed, threshold: 20, percent: 25);
        Assert.False(notify2);
        Assert.Equal(LowBatteryPhase.Armed, phase2);
    }

    // ───────────────────────── 迟滞恢复 ─────────────────────────

    [Fact]
    public void BatteryRecoversToThresholdPlusMargin_ReArms()
    {
        (bool notify, LowBatteryPhase phase) = Evaluate(LowBatteryPhase.Fired, threshold: 20, percent: 30);
        Assert.False(notify);   // 恢复本身不提醒
        Assert.Equal(LowBatteryPhase.Armed, phase);
    }

    [Fact]
    public void ReArmedThenDropsAgain_FiresAgain()
    {
        // 放回充电盒 → 恢复到 30%（阈值+10）→ 重新武装 → 再降到阈值内 → 再次提醒
        (_, LowBatteryPhase armed) = Evaluate(LowBatteryPhase.Fired, threshold: 20, percent: 55);
        (_, LowBatteryPhase reArmed) = Evaluate(armed, threshold: 20, percent: 30);
        Assert.Equal(LowBatteryPhase.Armed, reArmed);
        (bool notify, LowBatteryPhase fired) = Evaluate(reArmed, threshold: 20, percent: 19);
        Assert.True(notify);
        Assert.Equal(LowBatteryPhase.Fired, fired);
    }

    // ───────────────────────── 无效电量 ─────────────────────────

    [Theory]
    [InlineData(DataValidity.Unknown)]
    [InlineData(DataValidity.Loading)]
    [InlineData(DataValidity.Stale)]
    [InlineData(DataValidity.Error)]
    [InlineData(DataValidity.Unsupported)]
    public void InvalidValidity_NeverFires(DataValidity validity)
    {
        (bool notify, LowBatteryPhase phase) = Evaluate(
            LowBatteryPhase.Armed, threshold: 20, percent: 5, validity: validity);
        Assert.False(notify);
        Assert.Equal(LowBatteryPhase.Armed, phase);   // 状态保持，不误转 Fired
    }

    [Fact]
    public void NullPercent_NeverFires()
    {
        (bool notify, LowBatteryPhase phase) = Evaluate(LowBatteryPhase.Armed, threshold: 20, percent: null);
        Assert.False(notify);
        Assert.Equal(LowBatteryPhase.Armed, phase);
    }

    // ───────────────────────── 会话边界 ─────────────────────────

    [Fact]
    public void SessionInactive_ResetsToArmed()
    {
        (bool notify, LowBatteryPhase phase) = Evaluate(
            LowBatteryPhase.Fired, threshold: 20, percent: 10, sessionActive: false);
        Assert.False(notify);
        Assert.Equal(LowBatteryPhase.Armed, phase);
    }

    [Fact]
    public void ReconnectAfterFired_CanFireAgain()
    {
        // 会话 1：低电量已提醒 → 断开（重置）→ 重新连接后再次低电量 → 再次提醒
        (_, LowBatteryPhase fired) = Evaluate(LowBatteryPhase.Armed, threshold: 20, percent: 18);
        Assert.Equal(LowBatteryPhase.Fired, fired);

        (_, LowBatteryPhase afterDisconnect) = Evaluate(fired, threshold: 20, percent: 18, sessionActive: false);
        Assert.Equal(LowBatteryPhase.Armed, afterDisconnect);

        (bool notify, LowBatteryPhase firedAgain) = Evaluate(
            afterDisconnect, threshold: 20, percent: 18);
        Assert.True(notify);
        Assert.Equal(LowBatteryPhase.Fired, firedAgain);
    }

    // ───────────────────────── 各档位阈值 ─────────────────────────

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(10, 11, false)]
    [InlineData(15, 15, true)]
    [InlineData(30, 29, true)]
    [InlineData(30, 31, false)]
    public void ThresholdBoundaries_EvaluatedCorrectly(int threshold, int percent, bool expected)
    {
        (bool notify, _) = Evaluate(LowBatteryPhase.Armed, threshold, percent);
        Assert.Equal(expected, notify);
    }
}
