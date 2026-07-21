using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WavyFi.Models;

namespace WavyFi.Controls;

/// <summary>
/// inSSIDer-style channel occupancy graph: each network is a bell curve
/// spanning its ~20 MHz width, peaking at its RSSI. Stale networks fade.
/// </summary>
public class ChannelGraph : FrameworkElement
{
    private static readonly Brush Bg;
    private static readonly Pen GridPen;
    private static readonly Brush AxisBrush;
    private static readonly Typeface Font = new("Segoe UI");

    static ChannelGraph()
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
    private HashSet<string> _selectedBssids = new();
    private string _band = "2.4 GHz";
    private double _minCh = -1, _maxCh = 16;
    private int[] _labels = Enumerable.Range(1, 14).ToArray();

    public ChannelGraph()
    {
        ClipToBounds = true; // out-of-axis channels must not paint over neighbors
    }

    public string Band
    {
        get => _band;
        set
        {
            _band = value;
            (_minCh, _maxCh, _labels) = value switch
            {
                "5 GHz" => (30.0, 179.0, new[] { 36, 44, 52, 60, 100, 112, 124, 136, 149, 157, 165, 173 }),
                "6 GHz" => (-3.0, 237.0, new[] { 1, 33, 65, 97, 129, 161, 193, 225 }),
                _ => (-1.0, 18.0, Enumerable.Range(1, 14).ToArray()),
            };
            InvalidateVisual();
        }
    }

    /// <summary>Channel *number* → axis position. Only channel 14 is displaced:
    /// it sits at 2484 MHz, 12 MHz above channel 13 instead of the usual 5 MHz
    /// step, i.e. position 15.4 ((2484 - 2407) / 5). Continuous spans around it
    /// (curve edges) stay linear, so this applies to discrete numbers only.</summary>
    private double Pos(int ch) =>
        _band == "2.4 GHz" && ch == 14 ? 15.4 : ch;

    public void SetEntries(IEnumerable<NetworkEntry> entries)
    {
        // Weakest first so the strongest signals draw on top.
        _entries = entries.Where(e => e.Band == _band).OrderBy(e => e.Rssi).ToList();
        InvalidateVisual();
    }

    public void SetSelection(IEnumerable<string> bssids)
    {
        _selectedBssids = new HashSet<string>(bssids);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (w < 80 || h < 60) return;

        const double left = 36, right = 10, top = 12, bottom = 22;
        double plotW = w - left - right, plotH = h - top - bottom;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double Y(double dbm) => top + (-30 - dbm) / 70.0 * plotH;
        double XPos(double pos) => left + (pos - _minCh) / (_maxCh - _minCh) * plotW;

        for (int dbm = -30; dbm >= -100; dbm -= 10)
        {
            double y = Y(dbm);
            dc.DrawLine(GridPen, new Point(left, y), new Point(w - right, y));
            var t = Text(dbm.ToString(), 9.5, AxisBrush, ppd);
            dc.DrawText(t, new Point(left - t.Width - 4, y - t.Height / 2));
        }

        foreach (int ch in _labels)
        {
            var t = Text(ch.ToString(), 9.5, AxisBrush, ppd);
            dc.DrawText(t, new Point(XPos(Pos(ch)) - t.Width / 2, h - bottom + 4));
        }

        if (_entries.Count == 0)
        {
            var empty = Text($"No {_band} networks", 12, AxisBrush, ppd);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        bool hasSelection = _selectedBssids.Count > 0;

        // Selected networks draw last so they sit on top of everything else.
        foreach (var e in _entries.OrderBy(x => _selectedBssids.Contains(x.Bssid)))
        {
            if (e.Channel <= 0 || e.CenterChannel <= 0) continue;

            bool isSelected = _selectedBssids.Contains(e.Bssid);
            var color = GraphPalette.ColorFor(e.Bssid);
            byte fillAlpha = e.IsStale ? (byte)0x14
                : isSelected ? (byte)0x80
                : hasSelection ? (byte)0x20
                : (byte)0x48;
            byte lineAlpha = e.IsStale ? (byte)0x50
                : hasSelection && !isSelected ? (byte)0x58
                : (byte)0xFF;
            var lineBrush = new SolidColorBrush(Color.FromArgb(lineAlpha, color.R, color.G, color.B));
            var fill = new SolidColorBrush(Color.FromArgb(fillAlpha, color.R, color.G, color.B));
            var stroke = new Pen(lineBrush, isSelected ? 3.0 : e.IsConnected ? 2.5 : 1.5);

            // Channel numbers step 5 MHz, so a span of W MHz covers W/10
            // channel numbers to each side of the bonded center. Map the
            // center channel number to its axis position once, then span in
            // position space — keeps edges linear around the channel-14 quirk.
            double halfWidth = e.ChannelWidthMhz / 10.0;
            double centerPos = Pos(e.CenterChannel);
            double xL = XPos(centerPos - halfWidth), xC = XPos(centerPos), xR = XPos(centerPos + halfWidth);
            double yBase = Y(-100);
            double yTop = Y(Math.Clamp(e.Rssi, -100, -30));
            // Control-point height chosen so the bezier's apex lands on yTop.
            double yCtrl = (yTop - 0.25 * yBase) / 0.75;

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(xL, yBase), isFilled: true, isClosed: true);
                g.BezierTo(
                    new Point(xC - (xC - xL) * 0.4, yCtrl),
                    new Point(xC + (xR - xC) * 0.4, yCtrl),
                    new Point(xR, yBase), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(fill, stroke, geo);

            var label = Text(e.DisplayName, isSelected ? 12 : 10.5, lineBrush, ppd);
            double lx = Math.Clamp(xC - label.Width / 2, left, Math.Max(left, w - right - label.Width));
            dc.DrawText(label, new Point(lx, Math.Max(2, yTop - label.Height - 1)));
        }
    }

    private static FormattedText Text(string s, double size, Brush brush, double ppd) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush, ppd);
}
