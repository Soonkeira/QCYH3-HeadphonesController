using System.Windows;
using QcyH3Tray.App;

namespace QcyH3Tray.Tests;

/// <summary>
/// EQ 编辑器纯逻辑（阶段 3b）：
/// - <see cref="EqCurveGeometry"/>：7 段增益 → Polyline 点集（平线=中点、极值=内边距、x 均匀）；
/// - <see cref="EqDirtyTracker"/>：未保存修改追踪（基线比对 / 无基线时的编辑标记）。
/// </summary>
public class EqEditorSupportTests
{
    // ───────────────────────── 曲线几何 ─────────────────────────

    private static readonly double[] Flat = [0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void BandCurvePoints_FlatLine_SitsOnVerticalMidpoint()
    {
        var points = EqCurveGeometry.BandCurvePoints(Flat, 600, 170, 36, 12);

        Assert.Equal(7, points.Count);
        Assert.All(points, point => Assert.Equal(12 + (170 - 24) / 2, point.Y, 6));
    }

    [Fact]
    public void BandCurvePoints_MaxGain_ReachesTopPadding()
    {
        var points = EqCurveGeometry.BandCurvePoints([12, 0, 0, 0, 0, 0, 0], 600, 170, 36, 12);

        Assert.Equal(12, points[0].Y, 6);   // +12 dB → 上内边距线
    }

    [Fact]
    public void BandCurvePoints_MinGain_ReachesBottomPadding()
    {
        var points = EqCurveGeometry.BandCurvePoints([0, 0, 0, 0, 0, 0, -12], 600, 170, 36, 12);

        Assert.Equal(170 - 12, points[^1].Y, 6);
    }

    [Fact]
    public void BandCurvePoints_XCoordinates_EvenlySpreadWithinPadding()
    {
        var points = EqCurveGeometry.BandCurvePoints(Flat, 600, 170, 36, 12);

        Assert.Equal(36, points[0].X, 6);
        Assert.Equal(600 - 36, points[^1].X, 6);
        for (int i = 1; i < points.Count; i++)
        {
            Assert.Equal(528.0 / 6, points[i].X - points[i - 1].X, 6);
        }
    }

    [Fact]
    public void BandCurvePoints_GainBeyondRange_IsClamped()
    {
        var points = EqCurveGeometry.BandCurvePoints([99, -99, 0, 0, 0, 0, 0], 600, 170, 36, 12);

        Assert.Equal(12, points[0].Y, 6);
        Assert.Equal(170 - 12, points[1].Y, 6);
    }

    [Fact]
    public void BandCurvePoints_ZeroOrTinyCanvas_ReturnsEmpty()
    {
        Assert.Empty(EqCurveGeometry.BandCurvePoints(Flat, 0, 170, 36, 12));
        Assert.Empty(EqCurveGeometry.BandCurvePoints(Flat, 60, 170, 36, 12));   // 60 < 2×36
        Assert.Empty(EqCurveGeometry.BandCurvePoints([], 600, 170, 36, 12));
    }

    [Fact]
    public void BaselineLine_SpansHorizontalPaddingAtMidpoint()
    {
        (Point start, Point end) = EqCurveGeometry.BaselineLine(600, 170, 36, 12);

        Assert.Equal(36, start.X, 6);
        Assert.Equal(600 - 36, end.X, 6);
        Assert.Equal(start.Y, end.Y);
        Assert.Equal(12 + (170 - 24) / 2, start.Y, 6);
    }

    // ───────────────────────── dirty 追踪 ─────────────────────────

    [Fact]
    public void Dirty_NoBaseline_TracksAnyUserEdit()
    {
        var tracker = new EqDirtyTracker();
        Assert.False(tracker.IsDirty(Flat, 0));

        tracker.Touch();
        Assert.True(tracker.IsDirty(Flat, 0));

        tracker.Reset();
        Assert.False(tracker.IsDirty(Flat, 0));
    }

    [Fact]
    public void Dirty_WithBaseline_MatchesOnlyWithinTolerance()
    {
        var tracker = new EqDirtyTracker();
        tracker.SetBaseline(Flat, 0);

        Assert.False(tracker.IsDirty(Flat, 0));
        Assert.False(tracker.IsDirty([0, 0, 0.04, 0, 0, 0, 0], 0));   // 容差内
        Assert.True(tracker.IsDirty([0, 0, 0.5, 0, 0, 0, 0], 0));      // 单段变化
        Assert.True(tracker.IsDirty(Flat, 0.5));                        // 总增益变化
    }

    [Fact]
    public void Dirty_SetBaseline_ClearsEditFlag()
    {
        var tracker = new EqDirtyTracker();
        tracker.Touch();
        Assert.True(tracker.IsDirty(Flat, 0));

        tracker.SetBaseline(Flat, 0);
        Assert.False(tracker.IsDirty(Flat, 0));
    }

    [Fact]
    public void Dirty_AfterApply_RestoresCleanUntilNewEdits()
    {
        var tracker = new EqDirtyTracker();
        tracker.SetBaseline(Flat, 0);

        double[] applied = [1, 0, -1, 0, 0, 0, 0];
        tracker.Touch();
        Assert.True(tracker.IsDirty(applied, 0));

        tracker.SetBaseline(applied, 0);
        Assert.False(tracker.IsDirty(applied, 0));
        Assert.True(tracker.IsDirty(Flat, 0));   // 回到旧值也算修改
    }

    [Fact]
    public void Dirty_DifferentBandCount_IsDirty()
    {
        var tracker = new EqDirtyTracker();
        tracker.SetBaseline(Flat, 0);

        Assert.True(tracker.IsDirty([0, 0, 0], 0));
    }
}
