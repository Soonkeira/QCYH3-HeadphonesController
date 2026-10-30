using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Icon = System.Drawing.Icon;

namespace QcyH3Tray.App;

/// <summary>
/// 托盘电量徽章图标：32×32 动态绘制（DrawingVisual → RenderTargetBitmap →
/// PNG 打进 ICO 容器）。圆角矩形底 + 居中 Segoe UI 粗体数字（3 位数缩字号）。
/// 按 (文本, 颜色) 缓存，避免每次状态刷新都重建。
/// </summary>
internal static class BatteryIconRenderer
{
    public static readonly Color Gray = FromHex("#9E9E9E");      // 断连/未知
    public static readonly Color Red = FromHex("#D32F2F");       // ≤20
    public static readonly Color Orange = FromHex("#F57C00");    // ≤50
    public static readonly Color Green = FromHex("#2E7D32");     // >50

    private const int SideLength = 32;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Text, byte A, byte R, byte G, byte B), Icon> Cache = [];

    /// <summary>电量 → 底色分级；percent 为 null（未知/断连）时灰色。</summary>
    public static Color ColorFor(int? percent) => percent switch
    {
        null => Gray,
        <= 20 => Red,
        <= 50 => Orange,
        _ => Green,
    };

    public static Icon GetIcon(string text, Color color)
    {
        var key = (text, color.A, color.R, color.G, color.B);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Icon? cached))
            {
                return cached;
            }
            Icon icon = RenderIcon(text, color);
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

    private static Icon RenderIcon(string text, Color color)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(color),
                pen: null,
                rectangle: new Rect(1, 1, SideLength - 2, SideLength - 2),
                radiusX: 8,
                radiusY: 8);

            double fontSize = text.Length switch
            {
                0 => 18,
                1 => 20,
                2 => 18,
                _ => 14,   // 3 位数缩字号
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
                (SideLength - formatted.Width) / 2,
                (SideLength - formatted.Height) / 2 + (fontSize * 0.05)));
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
