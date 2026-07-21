using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WavyFi;

/// <summary>
/// Persists window size, position and maximized state in
/// HKCU\Software\WavyFi. A window closed while minimized is restored
/// normal — starting minimized looks like the app failed to launch.
/// </summary>
internal static class WindowPlacement
{
    private const string KeyPath = @"Software\WavyFi";

    public static void Restore(Window window)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return;

            double left = ReadDouble(key, "Left");
            double top = ReadDouble(key, "Top");
            double width = ReadDouble(key, "Width");
            double height = ReadDouble(key, "Height");

            if (width >= window.MinWidth && height >= window.MinHeight &&
                width >= 400 && height >= 300)
            {
                var virtualScreen = new Rect(
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

                // The screen may have shrunk since the last run (resolution
                // change, unplugged monitor): clamp the size to fit, then pull
                // the position back so the whole window stays visible.
                width = Math.Min(width, virtualScreen.Width);
                height = Math.Min(height, virtualScreen.Height);
                window.Width = width;
                window.Height = height;

                if (!double.IsNaN(left) && !double.IsNaN(top))
                {
                    window.Left = Math.Clamp(left, virtualScreen.Left,
                        Math.Max(virtualScreen.Left, virtualScreen.Right - width));
                    window.Top = Math.Clamp(top, virtualScreen.Top,
                        Math.Max(virtualScreen.Top, virtualScreen.Bottom - height));
                }
            }

            if (key.GetValue("Maximized") is int maximized && maximized == 1)
                window.WindowState = WindowState.Maximized;
        }
        catch
        {
            // Unreadable/corrupt values: keep XAML defaults.
        }
    }

    public static void Save(Window window)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);

            // When maximized or minimized, RestoreBounds holds the normal-state
            // rectangle — that's what should come back as the resize baseline.
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;

            key.SetValue("Left", bounds.Left.ToString(CultureInfo.InvariantCulture));
            key.SetValue("Top", bounds.Top.ToString(CultureInfo.InvariantCulture));
            key.SetValue("Width", bounds.Width.ToString(CultureInfo.InvariantCulture));
            key.SetValue("Height", bounds.Height.ToString(CultureInfo.InvariantCulture));
            key.SetValue("Maximized",
                window.WindowState == WindowState.Maximized ? 1 : 0,
                RegistryValueKind.DWord);
        }
        catch
        {
            // Failing to persist placement is never worth an error dialog.
        }
    }

    public static double LoadFontScale()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return 1.0;
            var value = ReadDouble(key, "FontScale");
            return double.IsNaN(value) ? 1.0 : Math.Clamp(value, 0.8, 1.6);
        }
        catch
        {
            return 1.0;
        }
    }

    public static void SaveFontScale(double scale)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("FontScale", scale.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // Not worth surfacing.
        }
    }

    /// <summary>Persists a grid's column order and visibility as one compact
    /// value per grid — "header=displayIndex:visible;..." under Cols.&lt;alias&gt;.
    /// Keyed by header text so layouts survive versions that add columns.</summary>
    public static void SaveColumnLayout(string gridAlias, DataGrid grid)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            var spec = string.Join(";", grid.Columns.Select(c =>
                $"{c.Header}={c.DisplayIndex}:{(c.Visibility == Visibility.Visible ? 1 : 0)}"));
            key.SetValue($"Cols.{gridAlias}", spec);
        }
        catch
        {
            // Not worth surfacing.
        }
    }

    public static void RestoreColumnLayout(string gridAlias, DataGrid grid)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue($"Cols.{gridAlias}") is not string spec || spec.Length == 0)
                return;

            var stored = new Dictionary<string, (int Index, bool Visible)>();
            foreach (var part in spec.Split(';'))
            {
                var eq = part.LastIndexOf('=');
                if (eq <= 0) continue;
                var fields = part[(eq + 1)..].Split(':');
                if (fields.Length == 2 &&
                    int.TryParse(fields[0], out var index) &&
                    int.TryParse(fields[1], out var visible))
                    stored[part[..eq]] = (index, visible == 1);
            }
            if (stored.Count == 0) return;

            foreach (var column in grid.Columns)
                if (column.Header is string header && stored.TryGetValue(header, out var s))
                    column.Visibility = s.Visible ? Visibility.Visible : Visibility.Collapsed;

            // Columns unknown to the stored layout (added in a newer app
            // version) keep their declared position, after the known ones.
            var ordered = grid.Columns
                .Select((column, declared) => (
                    Column: column,
                    Target: column.Header is string header && stored.TryGetValue(header, out var s)
                        ? s.Index
                        : 1000 + declared))
                .OrderBy(x => x.Target)
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].Column.DisplayIndex = i;
        }
        catch
        {
            // Corrupt layout: keep the declared defaults.
        }
    }

    private static double ReadDouble(RegistryKey key, string name) =>
        key.GetValue(name) is string s &&
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
}
