using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// 应用图标生成器（阶段 5）：<see cref="DrawIcon"/> 以 256×256 设计坐标绘制
/// 正面简化头戴耳机（圆弧头梁 + 两侧圆耳罩剪影，Accent 蓝 #2563EB 渐变主体 +
/// 细白描边外圈——深浅任务栏均可辨），按帧表渲染 PNG 后打包多分辨率 ICO。
/// 16/24px 小帧用简化几何（去掉比例细节、加粗线条）保轮廓可辨。
///
/// 用法：
///   dotnet run --project tools/IconGen -- [输出.ico] [--png-dir 目录]
/// 输出缺省 = 自动定位仓库根（向上找 QcyH3Tray.sln）→ src/QcyH3Tray.App/Assets/app.ico；
/// --png-dir 同时导出每帧 PNG（自检/评审用）。
/// </summary>
internal static class Program
{
    /// <summary>ICO 帧表（大 → 小；PNG 压缩帧）。</summary>
    private static readonly int[] FrameSizes = [256, 128, 64, 48, 32, 24, 16];

    private const int Canvas = 256;

    [STAThread]
    private static int Main(string[] args)
    {
        string? pngDir = null;
        string? outputArg = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--png-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pngDir = args[++i];
            }
            else
            {
                outputArg = args[i];
            }
        }

        string output = outputArg ?? Path.Combine(
            FindRepoRoot(AppContext.BaseDirectory),
            "src", "QcyH3Tray.App", "Assets", "app.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (pngDir is not null)
        {
            Directory.CreateDirectory(pngDir);
        }

        var frames = new List<(int Size, byte[] Png)>();
        foreach (int size in FrameSizes)
        {
            byte[] png = RenderFrame(size, simplified: size <= 24);
            frames.Add((size, png));
            if (pngDir is not null)
            {
                File.WriteAllBytes(Path.Combine(pngDir, $"app-{size}.png"), png);
            }
            Console.WriteLine($"frame {size}px: {png.Length} B");
        }

        byte[] ico = BuildIcoContainer(frames);
        File.WriteAllBytes(output, ico);
        Console.WriteLine($"written: {output} ({ico.Length} B, {frames.Count} frames)");
        return 0;
    }

    /// <summary>向上查找包含 QcyH3Tray.sln 的目录（仓库根）。</summary>
    private static string FindRepoRoot(string start)
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QcyH3Tray.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("未找到 QcyH3Tray.sln，请显式传入输出路径");
    }

    /// <summary>渲染单帧：256 设计坐标等比缩放到物理像素（像素对齐，pixelsPerDip=1）。</summary>
    private static byte[] RenderFrame(int size, bool simplified)
    {
        double scale = (double)size / Canvas;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            DrawIcon(context, simplified);
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 图标绘制（256 设计坐标）：正面耳机剪影。
    /// 白描边外圈 = 先画放大一圈的白色版（头梁粗描边 / 耳罩外扩圆角矩形）再叠蓝主体。
    /// </summary>
    private static void DrawIcon(DrawingContext context, bool simplified)
    {
        // 头梁：圆弧路径（两端伸入耳罩后被覆盖）
        var bandStart = new Point(simplified ? 64 : 66, simplified ? 148 : 150);
        var bandApex = simplified ? 54.0 : 58.0;
        var bandEnd = new Point(simplified ? 192 : 190, simplified ? 148 : 150);
        var band = new StreamGeometry();
        using (StreamGeometryContext geo = band.Open())
        {
            geo.BeginFigure(bandStart, isFilled: false, isClosed: false);
            geo.BezierTo(new Point(bandStart.X, bandApex), new Point(bandEnd.X, bandApex), bandEnd,
                isStroked: true, isSmoothJoin: true);
        }

        // 耳罩（蓝主体）：左右对称的圆角矩形
        double cupTop = simplified ? 130 : 128;
        double cupHeight = simplified ? 92 : 92;
        double cupWidth = simplified ? 64 : 64;
        double cupRadius = simplified ? 30 : 28;
        var leftCup = new Rect(simplified ? 34 : 34, cupTop, cupWidth, cupHeight);
        var rightCup = new Rect(simplified ? 158 : 158, cupTop, cupWidth, cupHeight);
        // 白外圈：耳罩外扩一圈
        double haloExpand = simplified ? 8 : 6;
        var leftHalo = new Rect(leftCup.X - haloExpand, cupTop - haloExpand,
            cupWidth + haloExpand * 2, cupHeight + haloExpand * 2);
        var rightHalo = new Rect(rightCup.X - haloExpand, cupTop - haloExpand,
            cupWidth + haloExpand * 2, cupHeight + haloExpand * 2);

        var blueGradient = new LinearGradientBrush(
            Color.FromRgb(0x3D, 0x7B, 0xF0), Color.FromRgb(0x1D, 0x4E, 0xD8), 90.0);
        var haloBrush = Brushes.White;

        // 1. 白描边外圈（深色任务栏可见性）
        double bandHaloWidth = simplified ? 56 : 44;
        context.DrawGeometry(null, new Pen(haloBrush, bandHaloWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        }, band);
        double haloRadius = simplified ? 36 : 33;
        context.DrawRoundedRectangle(haloBrush, null, leftHalo, haloRadius, haloRadius);
        context.DrawRoundedRectangle(haloBrush, null, rightHalo, haloRadius, haloRadius);

        // 2. 蓝色主体
        double bandWidth = simplified ? 36 : 28;
        context.DrawGeometry(null, new Pen(blueGradient, bandWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        }, band);
        context.DrawRoundedRectangle(blueGradient, null, leftCup, cupRadius, cupRadius);
        context.DrawRoundedRectangle(blueGradient, null, rightCup, cupRadius, cupRadius);
    }

    /// <summary>把 PNG 帧序列打包进多分辨率 ICO 容器（ICONDIR + ICONDIRENTRY×N + PNG 数据）。</summary>
    private static byte[] BuildIcoContainer(IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);                       // reserved
            writer.Write((ushort)1);                       // type = icon
            writer.Write((ushort)frames.Count);            // 帧数
            int offset = 6 + 16 * frames.Count;
            foreach ((int size, byte[] png) in frames)
            {
                writer.Write((byte)(size == 256 ? 0 : size));   // 宽（256 编码为 0）
                writer.Write((byte)(size == 256 ? 0 : size));   // 高
                writer.Write((byte)0);                     // 调色板色数
                writer.Write((byte)0);                     // reserved
                writer.Write((ushort)1);                   // color planes
                writer.Write((ushort)32);                  // bits per pixel
                writer.Write((uint)png.Length);            // 数据长度
                writer.Write((uint)offset);                // 数据偏移
                offset += png.Length;
            }
            foreach ((_, byte[] png) in frames)
            {
                writer.Write(png);
            }
        }
        return stream.ToArray();
    }
}
