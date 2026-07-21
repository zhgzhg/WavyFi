using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WavyFi.IconGen;

/// <summary>
/// Generates Resources/WavyFi.ico: three overlapping channel-occupancy bells
/// (the app's signature graph shape) on the app's dark background, rendered
/// at all standard icon sizes and packed as PNG frames into one .ico.
/// Usage: dotnet run --project tools/IconGen -- &lt;output.ico&gt;
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    [STAThread]
    private static void Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "WavyFi.ico";
        var frames = Sizes.Select(size => (Size: size, Png: RenderPng(size))).ToList();
        WriteIco(outPath, frames);
        Console.WriteLine($"Wrote {Path.GetFullPath(outPath)} ({frames.Count} frames)");

        if (args.Length > 1) // optional preview PNG (largest frame)
        {
            File.WriteAllBytes(args[1], frames[^1].Png);
            Console.WriteLine($"Wrote preview {Path.GetFullPath(args[1])}");
        }
    }

    private static byte[] RenderPng(int size)
    {
        // Design coordinates are on a 256 canvas, scaled per frame.
        double s = size / 256.0;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24));
            double radius = 56 * s;
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), radius, radius);

            // Back-to-front, overlapping like neighboring channels; the back
            // bells use thinner strokes so the front one dominates.
            DrawBell(dc, s, cx: 84, apexY: 96, halfWidth: 78, stroke: 6.5, Color.FromRgb(0x4F, 0xC3, 0xA1));
            DrawBell(dc, s, cx: 172, apexY: 82, halfWidth: 78, stroke: 8, Color.FromRgb(0xE8, 0xA8, 0x4F));
            DrawBell(dc, s, cx: 128, apexY: 42, halfWidth: 92, stroke: 12, Color.FromRgb(0x6F, 0xA8, 0xFF));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Same bezier bell as the app's ChannelGraph: control-point
    /// height chosen so the curve's apex lands exactly on apexY.</summary>
    private static void DrawBell(DrawingContext dc, double s, double cx, double apexY, double halfWidth, double stroke, Color color)
    {
        double baseY = 214 * s;
        double top = apexY * s;
        double ctrlY = (top - 0.25 * baseY) / 0.75;
        double x0 = (cx - halfWidth) * s, x1 = (cx + halfWidth) * s, xc = cx * s;

        var c1 = new Point(xc - halfWidth * 0.35 * s, ctrlY);
        var c2 = new Point(xc + halfWidth * 0.35 * s, ctrlY);

        // Fill: closed shape down to the baseline, no outline.
        var fillGeo = new StreamGeometry();
        using (var g = fillGeo.Open())
        {
            g.BeginFigure(new Point(x0, baseY), isFilled: true, isClosed: true);
            g.BezierTo(c1, c2, new Point(x1, baseY), false, false);
        }
        fillGeo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)), null, fillGeo);

        // Stroke: the arc only — no baseline bar.
        var arcGeo = new StreamGeometry();
        using (var g = arcGeo.Open())
        {
            g.BeginFigure(new Point(x0, baseY), isFilled: false, isClosed: false);
            g.BezierTo(c1, c2, new Point(x1, baseY), true, false);
        }
        arcGeo.Freeze();
        var pen = new Pen(new SolidColorBrush(color), Math.Max(stroke * s, 1.2))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawGeometry(null, pen, arcGeo);
    }

    /// <summary>Minimal ICO container with PNG-compressed frames (supported
    /// by Windows Vista+ and the .NET SDK's ApplicationIcon embedding).</summary>
    private static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // palette colors
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames)
            writer.Write(png);
    }
}
