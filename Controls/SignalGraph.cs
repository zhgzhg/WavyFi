using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WavyFi.Models;

namespace WavyFi.Controls;

/// <summary>
/// Signal strength over time for the networks selected in the table.
/// One polyline per selected network over a sliding 5-minute window.
/// </summary>
public class SignalGraph : FrameworkElement
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly Brush Bg;
    private static readonly Pen GridPen;
    private static readonly Brush AxisBrush;
    private static readonly Typeface Font = new("Segoe UI");

    static SignalGraph()
    {
        Bg = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
        Bg.Freeze();
        var gridBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46));
        gridBrush.Freeze();
        GridPen = new Pen(gridBrush, 1);
        GridPen.Freeze();
        AxisBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x94));
        AxisBrush.Freeze();
    }

    private IReadOnlyList<NetworkEntry> _entries = Array.Empty<NetworkEntry>();

    public SignalGraph()
    {
        ClipToBounds = true; // never paint outside the plot area
    }

    private double _fontScale = 1.0;
    public double FontScale
    {
        get => _fontScale;
        set { _fontScale = value; InvalidateVisual(); }
    }

    public void SetEntries(IEnumerable<NetworkEntry> entries)
    {
        _entries = entries.ToList();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (w < 80 || h < 50) return;

        double fs = _fontScale;
        double pad = Math.Max(1.0, fs); // grow axis margins with the font
        double left = 36 * pad, bottom = 20 * pad;
        const double right = 10, top = 8;
        double plotW = w - left - right, plotH = h - top - bottom;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Anchor the window to the newest sample when data is frozen
        // (scanning paused) so re-renders don't slide the lines away.
        var now = DateTime.Now;
        var newest = DateTime.MinValue;
        foreach (var e in _entries)
            if (e.History.Count > 0 && e.History[^1].Time > newest)
                newest = e.History[^1].Time;
        if (newest != DateTime.MinValue && now - newest > TimeSpan.FromSeconds(10))
            now = newest;

        double Y(double dbm) => top + (-30 - Math.Clamp(dbm, -100, -30)) / 70.0 * plotH;
        double X(DateTime t) =>
            left + Math.Clamp((t - (now - Window)) / Window, 0, 1) * plotW;

        for (int dbm = -30; dbm >= -100; dbm -= 10)
        {
            double y = Y(dbm);
            dc.DrawLine(GridPen, new Point(left, y), new Point(w - right, y));
            var t = Text(dbm.ToString(), 9 * fs, AxisBrush, ppd);
            dc.DrawText(t, new Point(left - t.Width - 4, y - t.Height / 2));
        }

        for (int min = 5; min >= 0; min--)
        {
            double x = left + (1 - min / 5.0) * plotW;
            var t = Text(min == 0 ? "now" : $"-{min}m", 9 * fs, AxisBrush, ppd);
            dc.DrawText(t, new Point(Math.Min(x - t.Width / 2, w - right - t.Width), h - bottom + 3));
        }

        if (_entries.Count == 0)
        {
            var hint = Text("Select network(s) in the table to plot signal", 11 * fs, AxisBrush, ppd);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }

        foreach (var e in _entries)
        {
            var samples = e.History.Where(s => now - s.Time <= Window).ToList();
            if (samples.Count == 0) continue;

            var color = GraphPalette.ColorFor(e.Bssid);
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, e.IsConnected ? 2.2 : 1.5)
            {
                LineJoin = PenLineJoin.Round,
            };

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(samples[0].Time), Y(samples[0].Rssi)), false, false);
                for (int i = 1; i < samples.Count; i++)
                    g.LineTo(new Point(X(samples[i].Time), Y(samples[i].Rssi)), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);

            var last = samples[^1];
            double lx = X(last.Time), ly = Y(last.Rssi);
            dc.DrawEllipse(brush, null, new Point(lx, ly), 2.5, 2.5);

            var label = Text($"{e.DisplayName} ({last.Rssi})", 10 * fs, brush, ppd);
            dc.DrawText(label, new Point(
                Math.Clamp(lx - label.Width, left, Math.Max(left, w - right - label.Width)),
                Math.Clamp(ly - label.Height - 2, 0, Math.Max(0, h - bottom - label.Height))));
        }
    }

    private static FormattedText Text(string s, double size, Brush brush, double ppd) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush, ppd);
}
