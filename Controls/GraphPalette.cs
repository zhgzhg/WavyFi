using System.Windows.Media;

namespace WifiOptimizer.Controls;

/// <summary>Stable per-network colors derived from the BSSID, shared by
/// all graphs so a network looks the same everywhere.</summary>
public static class GraphPalette
{
    public static Color ColorFor(string bssid)
    {
        int hash = 17;
        foreach (char c in bssid) hash = hash * 31 + c;
        return HsvToRgb(Math.Abs(hash) % 360, 0.6, 0.95);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
