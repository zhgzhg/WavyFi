using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WavyFi.Ui;

/// <summary>
/// Per-grid header dressing: a column-chooser context menu built from the
/// grid's own columns, plus a descriptive tooltip per header. Compact grids
/// get tighter header padding.
/// </summary>
public static class GridChrome
{
    private static readonly Dictionary<string, string> ColumnDescriptions = new()
    {
        ["(E)SSID"] = "Extended Service Set ID — the network name broadcast by the access point; \"(hidden)\" when suppressed.",
        ["Signal (%)"] = "Signal quality as a percentage derived from RSSI.",
        ["RSSI (dBm)"] = "Received signal strength — closer to 0 is stronger (-40 excellent, -85 weak).",
        ["Ch"] = "Channel — the number of the primary 20 MHz channel the AP beacons on.",
        ["Width (MHz)"] = "Total bonded channel width: 20/40/80/160, or up to 320 on WiFi 7 (802.11be).",
        ["Max rate (Mbps)"] = "Theoretical top PHY rate from the AP's advertised MCS map, spatial streams and operating width (0.8 µs GI). Real throughput is roughly half.",
        ["Freq (MHz)"] = "Center frequency of the primary channel.",
        ["Band (GHz)"] = "Frequency band: 2.4, 5 or 6 GHz.",
        ["802.11"] = "Supported 802.11 generations: b/g/a legacy, n = WiFi 4, ac = WiFi 5, ax = WiFi 6, be = WiFi 7.",
        ["Generation"] = "Friendly name of the newest supported standard: WiFi 4 = n, 5 = ac, 6 = ax (6E on the 6 GHz band), 7 = be. WiFi 1-3 are informal retro-names for b/a/g.",
        ["Security"] = "Authentication method (WPA2/WPA3 Personal or Enterprise, Open, WEP...).",
        ["Cipher"] = "Encryption cipher. AES-CCMP/GCMP are current; TKIP and WEP are legacy and weak.",
        ["WPS"] = "WiFi Protected Setup version advertised by the AP, or \"-\" when not supported.",
        ["Legacy rates (Mbps)"] = "802.11a/b/g compatibility rates from the beacon's Supported Rates elements — modern rates are expressed as MCS instead; see Max rate.",
        ["BSSID"] = "MAC address of this access point radio (one SSID can have several).",
        ["Vendor"] = "Manufacturer resolved from the MAC prefix (embedded IEEE OUI registry).",
        ["Adapter"] = "The WLAN adapter that heard this reading, as [index] and name — one row per adapter when scanning with several.",
        ["Last seen (s)"] = "Seconds since last seen; faded rows are stale.",
        ["Name"] = "Device name advertised over WiFi Direct.",
        ["Paired"] = "Whether this device is paired with Windows.",
        ["Type"] = "Device category from the WPS Primary Device Type attribute.",
        ["MAC"] = "MAC address of the device's WiFi Direct interface.",
    };

    public static void Setup(DataGrid grid, bool compact)
    {
        var menu = BuildColumnChooserMenu(grid);
        var baseStyle = (Style)grid.FindResource(typeof(DataGridColumnHeader));
        foreach (var col in grid.Columns)
        {
            var style = new Style(typeof(DataGridColumnHeader), baseStyle);
            style.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, menu));
            if (compact)
                style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            if (col.Header is string header &&
                ColumnDescriptions.TryGetValue(header, out var description))
                style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, description));
            col.HeaderStyle = style;
        }
    }

    private static ContextMenu BuildColumnChooserMenu(DataGrid grid)
    {
        var menu = new ContextMenu();
        foreach (var column in grid.Columns)
        {
            var col = column;
            var item = new MenuItem
            {
                Header = col.Header?.ToString(),
                IsCheckable = true,
                IsChecked = col.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Checked += (_, _) => col.Visibility = Visibility.Visible;
            item.Unchecked += (_, _) => col.Visibility = Visibility.Collapsed;
            menu.Items.Add(item);
        }
        return menu;
    }
}
