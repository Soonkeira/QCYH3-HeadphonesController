using System.Windows;

namespace QcyH3Tray.App;

/// <summary>
/// EQ 曲线画布的纯几何：7 段增益（dB）→ Polyline 点集（x 均匀分布、y 线性映射
/// ±12 dB → 内边距矩形上下边）。与控件零依赖，绘制结果可单测。
/// </summary>
internal static class EqCurveGeometry
{
    /// <summary>滑块/曲线的增益范围（dB），与协议钳位（±12.7）不同——UI 范围沿用既有 ±12。</summary>
    public const double MinDb = -12;

    public const double MaxDb = 12;

    /// <summary>
    /// 计算曲线点集。返回恰好 <paramref name="gainsDb"/>.Count 个点；
    /// 宽高不足以容纳内边距（≤ 2×padding）时返回空列表（画布尚未布局完成）。
    /// 平线（全 0 dB）落在垂直中点；+12 → 上内边距线；-12 → 下内边距线。
    /// </summary>
    public static List<Point> BandCurvePoints(
        IReadOnlyList<double> gainsDb,
        double width,
        double height,
        double horizontalPadding,
        double verticalPadding)
    {
        double innerWidth = width - horizontalPadding * 2;
        double innerHeight = height - verticalPadding * 2;
        if (gainsDb.Count == 0 || innerWidth <= 0 || innerHeight <= 0)
        {
            return [];
        }

        double midY = verticalPadding + innerHeight / 2;
        double halfInnerHeight = innerHeight / 2;
        int last = gainsDb.Count - 1;
        var points = new List<Point>(gainsDb.Count);
        for (int i = 0; i < gainsDb.Count; i++)
        {
            double x = last == 0 ? horizontalPadding + innerWidth / 2 : horizontalPadding + innerWidth * i / last;
            double gain = Math.Clamp(gainsDb[i], MinDb, MaxDb);
            double y = midY - gain / MaxDb * halfInnerHeight;
            points.Add(new Point(x, y));
        }
        return points;
    }

    /// <summary>0 dB 虚线基线的两个端点（左内边距 → 右内边距，垂直中点）。</summary>
    public static (Point Start, Point End) BaselineLine(
        double width, double height, double horizontalPadding, double verticalPadding)
    {
        double innerWidth = width - horizontalPadding * 2;
        double midY = verticalPadding + (height - verticalPadding * 2) / 2;
        return (new Point(horizontalPadding, midY), new Point(horizontalPadding + Math.Max(innerWidth, 0), midY));
    }
}

/// <summary>
/// EQ 编辑器「未应用修改」（dirty）追踪：以最近一次确认与耳机一致（读取成功 / 应用成功 /
/// 恢复耳机值）的基线为参照——当前编辑值与基线不符即 dirty。
/// 尚无基线（打开后从未读到耳机值）时退化为「发生过任何用户编辑」。
/// 全部为纯内存逻辑，可单测；容差 0.05 dB 吸收 ×100 取整误差。
/// </summary>
internal sealed class EqDirtyTracker
{
    /// <summary>比较容差（dB）。</summary>
    public const double Tolerance = 0.05;

    private double[] _baselineGains = [];
    private double _baselineMasterGain;
    private bool _hasBaseline;
    private bool _editedSinceBaseline;

    /// <summary>记录一次「与耳机一致」的基线（读取成功 / 应用成功 / 恢复耳机值）。</summary>
    public void SetBaseline(IReadOnlyList<double> gainsDb, double masterGainDb)
    {
        _baselineGains = [.. gainsDb];
        _baselineMasterGain = masterGainDb;
        _hasBaseline = true;
        _editedSinceBaseline = false;
    }

    /// <summary>用户编辑了曲线（滑块变动 / 重置平线 / 加载配置）。有基线时由数值比对决定结果。</summary>
    public void Touch() => _editedSinceBaseline = true;

    /// <summary>清空追踪状态（窗口重置用）。</summary>
    public void Reset()
    {
        _baselineGains = [];
        _baselineMasterGain = 0;
        _hasBaseline = false;
        _editedSinceBaseline = false;
    }

    /// <summary>当前编辑值是否包含未应用的修改。</summary>
    public bool IsDirty(IReadOnlyList<double> currentGains, double currentMasterGain)
        => _hasBaseline
            ? !MatchesBaseline(currentGains, currentMasterGain)
            : _editedSinceBaseline;

    private bool MatchesBaseline(IReadOnlyList<double> currentGains, double currentMasterGain)
    {
        if (currentGains.Count != _baselineGains.Length
            || Math.Abs(currentMasterGain - _baselineMasterGain) > Tolerance)
        {
            return false;
        }
        for (int i = 0; i < _baselineGains.Length; i++)
        {
            if (Math.Abs(currentGains[i] - _baselineGains[i]) > Tolerance)
            {
                return false;
            }
        }
        return true;
    }
}
