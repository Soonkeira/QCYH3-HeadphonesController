using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Icon = System.Drawing.Icon;

namespace QcyH3Tray.App;

/// <summary>
/// 托盘图标（阶段 5，替代原纯数字徽章的 <c>BatteryIconRenderer</c>）：
/// 迷你耳机剪影（正面轮廓，32×32 设计坐标，高约 24px）+ 右下角电量徽章胶囊
/// （约 13×10，底色沿用电量分级：&gt;50 绿 / ≤50 橙 / ≤20 红 / 未知灰）。
/// 剪影=浅白填充 + 深灰描边（浅色任务栏可辨）；断连/电量未知 → 剪影转灰、无徽章
/// （完整信息在 tooltip）。徽章文本为空即不画徽章。
/// 按 (文本, 徽章色, 剪影状态) 缓存，避免每次状态刷新都重建 GDI 句柄。
/// </summary>
internal static class TrayIconRenderer
{
    public static readonly Color Gray = FromHex("#9E9E9E");      // 断连/未知
    public static readonly Color Red = FromHex("#D32F2F");       // ≤20
    public static readonly Color Orange = FromHex("#F57C00");    // ≤50
    public static readonly Color Green = FromHex("#2E7D32");     // >50

    /// <summary>剪影（已连接）：近白填充，浅色任务栏上靠深灰描边成形。</summary>
    public static readonly Color SilhouetteFillActive = FromHex("#F4F6FA");
    public static readonly Color SilhouetteStrokeActive = FromHex("#474C55");
    /// <summary>剪影（断连/未知）：整只转灰。</summary>
    public static readonly Color SilhouetteFillMuted = FromHex("#A6A9AF");
    public static readonly Color SilhouetteStrokeMuted = FromHex("#7C7F86");

    private const int SideLength = 32;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Text, byte A, byte R, byte G, byte B, bool Active), Icon> Cache = [];

    /// <summary>电量 → 徽章底色分级；percent 为 null（未知/断连）时灰色。</summary>
    public static Color ColorFor(int? percent) => percent switch
    {
        null => Gray,
        <= 20 => Red,
        <= 50 => Orange,
        _ => Green,
    };

    /// <summary>电量 → 徽章文本；null（未知/断连）= 空串（不画徽章，tooltip 承载状态）。</summary>
    public static string BadgeTextFor(int? percent) => percent?.ToString() ?? string.Empty;

    /// <summary>电量 → 剪影填充色：有电量读数 = 活跃白，否则置灰。</summary>
    public static Color SilhouetteFillFor(int? percent)
        => percent is null ? SilhouetteFillMuted : SilhouetteFillActive;

    public static Icon GetIcon(int? percent)
    {
        string text = BadgeTextFor(percent);
        Color color = ColorFor(percent);
        bool active = percent is not null;
        var key = (text, color.A, color.R, color.G, color.B, active);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Icon? cached))
            {
                return cached;
            }
            Icon icon = RenderIcon(text, color, active);
            Cache[key] = icon;
            return icon;
        }
    }

    /// <summary>应用退出时清理全部缓存图标（GDI 句柄）。</summary>
    public static void ClearCache()
    {
        lock (Gate)
        {
            foreach (Icon icon in Cache.Values)
            {
                icon.Dispose();
            }
            Cache.Clear();
        }
    }

    private static Icon RenderIcon(string text, Color color, bool active)
    {
        Color fill = active ? SilhouetteFillActive : SilhouetteFillMuted;
        Color stroke = active ? SilhouetteStrokeActive : SilhouetteStrokeMuted;

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            // 1. 迷你耳机剪影（正面）：头梁弧 + 左右圆角耳罩，约占 24px 高。
            //    头梁描边法：先粗的深灰描边、后细的浅色描边——形成"描边夹浅芯"的轮廓带
            var silhouetteBrush = new SolidColorBrush(fill);
            var silhouettePen = new Pen(new SolidColorBrush(stroke), 1.2);
            var band = new StreamGeometry();
            using (StreamGeometryContext geo = band.Open())
            {
                geo.BeginFigure(new Point(9.2, 17.5), isFilled: false, isClosed: false);
                geo.BezierTo(new Point(9.2, 5.2), new Point(22.8, 5.2), new Point(22.8, 17.5),
                    isStroked: true, isSmoothJoin: true);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(stroke), 4.8), band);
            context.DrawGeometry(null, new Pen(silhouetteBrush, 2.6), band);

            context.DrawRoundedRectangle(silhouetteBrush, silhouettePen, new Rect(5.2, 14.5, 8, 12), 3.8, 3.8);
            context.DrawRoundedRectangle(silhouetteBrush, silhouettePen, new Rect(18.6, 14.5, 8, 12), 3.8, 3.8);

            // 2. 电量徽章（右下角胶囊）：文本为空（断连/未知）不画
            if (text.Length > 0)
            {
                var badgeRect = new Rect(17.8, 20.8, 13, 9.5);
                context.DrawRoundedRectangle(
                    new SolidColorBrush(color),
                    new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 1),
                    badgeRect, radiusX: 4.5, radiusY: 4.5);

                double fontSize = text.Length switch
                {
                    1 => 8.0,
                    2 => 7.2,
                    _ => 6.2,   // 3 位数缩字号
                };
                var formatted = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    fontSize,
                    Brushes.White,
                    pixelsPerDip: 1.0);
                // FormattedText 高度含行距，视觉居中需要向下微调
                context.DrawText(formatted, new Point(
                    badgeRect.X + (badgeRect.Width - formatted.Width) / 2,
                    badgeRect.Y + (badgeRect.Height - formatted.Height) / 2 + (fontSize * 0.05)));
            }
        }

        var bitmap = new RenderTargetBitmap(SideLength, SideLength, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var pngStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(pngStream);

        return new Icon(new MemoryStream(BuildIcoContainer(pngStream.ToArray(), SideLength)));
    }

    /// <summary>把 PNG 位图包进单帧 ICO 容器（ICONDIR 6B + ICONDIRENTRY 16B + PNG 数据）。</summary>
    private static byte[] BuildIcoContainer(byte[] png, int sideLength)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);              // reserved
            writer.Write((ushort)1);              // type = icon
            writer.Write((ushort)1);              // 帧数
            writer.Write((byte)sideLength);       // 宽（px）
            writer.Write((byte)sideLength);       // 高（px）
            writer.Write((byte)0);                // 调色板色数
            writer.Write((byte)0);                // reserved
            writer.Write((ushort)1);              // color planes
            writer.Write((ushort)32);             // bits per pixel
            writer.Write((uint)png.Length);       // 数据长度
            writer.Write((uint)(6 + 16));         // 数据偏移
            writer.Write(png);
        }
        return stream.ToArray();
    }

    private static Color FromHex(string hex)
        => (Color)ColorConverter.ConvertFromString(hex);
}
