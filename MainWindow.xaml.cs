using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WifiOptimizer.Analysis;
using WifiOptimizer.Models;
using WifiOptimizer.WifiDirect;
using WifiOptimizer.Wlan;

namespace WifiOptimizer;

public partial class MainWindow : Window
{
    private readonly WifiScanner _scanner;
    private readonly WifiDirectWatcher _wifiDirect;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _peerTimer;
    private readonly NetworkStore _store = new();
    private readonly ListCollectionView _view;
    private readonly System.Collections.ObjectModel.ObservableCollection<PeerEntry> _peerEntries = new();
    private readonly ListCollectionView _peersView;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_store.Entries);
        _view.Filter = FilterPredicate;
        _view.SortDescriptions.Add(new SortDescription(nameof(NetworkEntry.IsConnected), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(NetworkEntry.Rssi), ListSortDirection.Descending));
        _view.IsLiveSorting = true;
        _view.LiveSortingProperties.Add(nameof(NetworkEntry.IsConnected));
        _view.LiveSortingProperties.Add(nameof(NetworkEntry.Rssi));
        NetworksGrid.ItemsSource = _view;

        _peersView = (ListCollectionView)CollectionViewSource.GetDefaultView(_peerEntries);
        _peersView.Filter = PeerFilterPredicate;
        _peersView.SortDescriptions.Add(new SortDescription(nameof(PeerEntry.SignalDbm), ListSortDirection.Descending));
        PeersGrid.ItemsSource = _peersView;

        SetupGridHeaders(NetworksGrid, compact: false);
        SetupGridHeaders(PeersGrid, compact: true);

        _scanner = new WifiScanner();
        // Read results the moment a sweep finishes instead of waiting for the
        // next timer tick — first data lands ~2-4 s after launch. Read-only:
        // triggering another scan here would loop scan -> complete -> scan.
        _scanner.ScanCompleted += () => Dispatcher.BeginInvoke(async () =>
        {
            if (ScanToggle.IsChecked == true)
                await RefreshAsync(triggerScan: false);
        });
        ReloadAdapterList();
        AdapterCombo.IsEnabled = ScanToggle.IsChecked != true;
        _wifiDirect = new WifiDirectWatcher();
        _wifiDirect.PeersChanged += () => Dispatcher.BeginInvoke(UpdatePeersList);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync();

        // Peer ages keep counting regardless of the scan toggle — WiFi Direct
        // discovery runs independently of the WLAN scan loop.
        _peerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _peerTimer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            foreach (var p in _peerEntries) p.Tick(now);
            _peersView.Refresh();
        };
        _peerTimer.Start();

        Loaded += async (_, _) =>
        {
            _wifiDirect.Start();
            if (ScanToggle.IsChecked == true)
            {
                await RefreshAsync();
                _timer.Start();
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _peerTimer.Stop();
            _wifiDirect.Dispose();
            _scanner.Dispose();
        };
    }

    private async Task RefreshAsync(bool triggerScan = true)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var networks = await Task.Run(() =>
            {
                if (triggerScan) _scanner.TriggerScan();
                return _scanner.GetNetworks();
            });

            _store.ApplyScan(networks, DateTime.Now);
            _view.Refresh();
            UpdateGraphs();
            // Advise from the persistent store, not the raw scan — single scans
            // can come back near-empty right after the BSS cache is flushed.
            // Skip the update while the user is selecting/copying from the box.
            if (!AdviceText.IsKeyboardFocusWithin)
                AdviceText.Text = ChannelAdvisor.BuildReport(_store.Entries.ToList());
            StatusText.Text = "Scanning every 5 s.";
            StatsText.Text = $"{networks.Count} in range  ·  {_store.Entries.Count} tracked  ·  " +
                             $"updated {DateTime.Now:HH:mm:ss}";

            // The scanner may have fallen back to another adapter (unplug).
            if ((AdapterCombo.SelectedItem as WlanAdapter)?.Guid != _scanner.CurrentAdapterGuid)
                ReloadAdapterList();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ReloadAdapterList(); // reflect adapters that vanished or reappeared
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateGraphs()
    {
        var visible = _view.Cast<NetworkEntry>().ToList();
        Graph24.SetEntries(visible);
        Graph5.SetEntries(visible);
        Graph6.SetEntries(visible);
        UpdateSignalGraph();
    }

    private void UpdateSignalGraph()
    {
        var selected = NetworksGrid.SelectedItems.Cast<NetworkEntry>().ToList();
        SignalGraphView.SetEntries(selected);

        var bssids = selected.Select(n => n.Bssid).ToList();
        Graph24.SetSelection(bssids);
        Graph5.SetSelection(bssids);
        Graph6.SetSelection(bssids);
    }

    private void NetworksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSignalGraph();
    }

    /// <summary>Plain click toggles: clicking an already-selected row unselects
    /// it, clicking empty grid space clears the selection. Shared by both grids.</summary>
    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || Keyboard.Modifiers != ModifierKeys.None) return;

        var source = e.OriginalSource as DependencyObject;
        var row = FindParent<DataGridRow>(source);
        if (row is null)
        {
            if (FindParent<DataGridColumnHeader>(source) is null &&
                FindParent<ScrollBar>(source) is null)
                grid.UnselectAll();
            return;
        }

        if (row.IsSelected)
        {
            grid.SelectedItems.Remove(row.Item);
            e.Handled = true;
        }
    }

    private string _rightClickedCellText = "";
    private NetworkEntry? _rightClickedRow;

    private void NetworksGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        _rightClickedCellText = (cell?.Content as TextBlock)?.Text ?? "";
        _rightClickedRow = cell?.DataContext as NetworkEntry;
        CopyCellItem.IsEnabled = _rightClickedCellText.Length > 0;
    }

    private void CopyCell_Click(object sender, RoutedEventArgs e)
    {
        if (_rightClickedCellText.Length > 0)
            TrySetClipboard(_rightClickedCellText);
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e)
    {
        var rows = NetworksGrid.SelectedItems.Cast<NetworkEntry>().ToList();
        // Right-clicking a row outside the selection copies that row, matching
        // "Copy cell" semantics; otherwise the selection wins.
        if (_rightClickedRow is not null && !rows.Contains(_rightClickedRow))
            rows = new List<NetworkEntry> { _rightClickedRow };
        if (rows.Count == 0) return;

        var columns = VisibleColumnsInOrder();
        var lines = rows.Select(r =>
            string.Join("\t", columns.Select(c => CellText(c, r))));
        TrySetClipboard(string.Join(Environment.NewLine, lines));
    }

    private void CopyTable_Click(object sender, RoutedEventArgs e)
    {
        var columns = VisibleColumnsInOrder();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => Csv(c.Header?.ToString() ?? ""))));
        foreach (var item in _view.Cast<NetworkEntry>())
            sb.AppendLine(string.Join(",", columns.Select(c => Csv(CellText(c, item)))));
        TrySetClipboard(sb.ToString());
    }

    private List<DataGridColumn> VisibleColumnsInOrder() =>
        NetworksGrid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

    private static string CellText(DataGridColumn column, object item) =>
        column.OnCopyingCellClipboardContent(item)?.ToString() ?? "";

    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            StatusText.Text = "Clipboard is busy — try copying again.";
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T)
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        return child as T;
    }

    private bool FilterPredicate(object item)
    {
        if (item is not NetworkEntry e) return false;

        var query = SearchBox.Text.Trim();
        if (query.Length > 0 &&
            !e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !e.Bssid.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !e.Vendor.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        if (BandFilter.SelectedIndex > 0 &&
            BandFilter.SelectedItem is ComboBoxItem band &&
            e.Band != (string)band.Content)
            return false;

        if (e.Rssi < MinRssiSlider.Value)
            return false;

        return true;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null || _peersView is null) return; // fires during InitializeComponent
        MinRssiLabel.Text = $"{(int)MinRssiSlider.Value} dBm";
        _view.Refresh();
        _peersView.Refresh();
        UpdateGraphs();
    }

    private static readonly Dictionary<string, string> ColumnDescriptions = new()
    {
        ["(E)SSID"] = "Extended Service Set ID — the network name broadcast by the access point; \"(hidden)\" when suppressed.",
        ["Signal"] = "Signal quality as a percentage derived from RSSI.",
        ["RSSI"] = "Received signal strength in dBm — closer to 0 is stronger (-40 excellent, -85 weak).",
        ["Ch"] = "Primary 20 MHz channel number.",
        ["Width"] = "Total bonded channel width: 20/40/80/160 MHz, or up to 320 MHz on WiFi 7 (802.11be).",
        ["Freq"] = "Center frequency of the primary channel, in MHz.",
        ["Band"] = "Frequency band: 2.4, 5 or 6 GHz.",
        ["802.11"] = "Supported 802.11 generations: b/g/a legacy, n = WiFi 4, ac = WiFi 5, ax = WiFi 6, be = WiFi 7.",
        ["Security"] = "Authentication method (WPA2/WPA3 Personal or Enterprise, Open, WEP...).",
        ["Cipher"] = "Encryption cipher. AES-CCMP/GCMP are current; TKIP and WEP are legacy and weak.",
        ["WPS"] = "WiFi Protected Setup version advertised by the AP, or \"-\" when not supported.",
        ["Rates (Mbps)"] = "Legacy data rates advertised by the AP, in Mbps.",
        ["BSSID"] = "MAC address of this access point radio (one SSID can have several).",
        ["Vendor"] = "Manufacturer resolved from the MAC prefix (embedded IEEE OUI registry).",
        ["Last seen"] = "Time since last seen; faded rows are stale.",
        ["Name"] = "Device name advertised over WiFi Direct.",
        ["Paired"] = "Whether this device is paired with Windows.",
        ["Type"] = "Device category from the WPS Primary Device Type attribute.",
        ["MAC"] = "MAC address of the device's WiFi Direct interface.",
    };

    /// <summary>Gives every column header its tooltip and the grid's own
    /// column-chooser context menu (built per grid, since each has its own
    /// column set). Compact grids get tighter header padding.</summary>
    private void SetupGridHeaders(DataGrid grid, bool compact)
    {
        var menu = BuildColumnChooserMenu(grid);
        var baseStyle = (Style)FindResource(typeof(DataGridColumnHeader));
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

    private void PeersGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is not null && !row.IsSelected)
        {
            PeersGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    private void CopyPeer_Click(object sender, RoutedEventArgs e)
    {
        if (PeersGrid.SelectedItem is not PeerEntry p) return;
        var name = p.IsPaired ? $"{p.Name} (paired)" : p.Name;
        var details = new[] { p.DeviceType, p.Vendor, p.Address, p.SignalText, p.LastSeenText }
            .Where(s => s.Length > 0);
        TrySetClipboard($"{name}{Environment.NewLine}{string.Join("  ·  ", details)}");
    }

    private void CopyPeerList_Click(object sender, RoutedEventArgs e)
    {
        var peers = _peersView.Cast<PeerEntry>().ToList(); // filtered, sorted view
        if (peers.Count == 0) return;
        TrySetClipboard(string.Join(Environment.NewLine, peers.Select(p =>
            string.Join("\t", p.Name + (p.IsPaired ? " (paired)" : ""),
                p.SignalText, p.DeviceType, p.Vendor, p.Address, p.LastSeenText))));
    }

    /// <summary>Merges the watcher snapshot into the observable collection,
    /// updating entries in place so grid sorting and selection survive.</summary>
    private void UpdatePeersList()
    {
        var now = DateTime.Now;
        var byId = _peerEntries.ToDictionary(p => p.Id);
        var seen = new HashSet<string>();

        foreach (var peer in _wifiDirect.GetPeers())
        {
            seen.Add(peer.Id);
            if (byId.TryGetValue(peer.Id, out var entry))
                entry.UpdateFrom(peer, now);
            else
                _peerEntries.Add(new PeerEntry(peer, now));
        }

        for (int i = _peerEntries.Count - 1; i >= 0; i--)
            if (!seen.Contains(_peerEntries[i].Id))
                _peerEntries.RemoveAt(i);

        _peersView.Refresh();
    }

    private bool PeerFilterPredicate(object item)
    {
        if (item is not PeerEntry p) return false;

        var query = SearchBox.Text.Trim();
        if (query.Length > 0 &&
            !p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !p.Address.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !p.Vendor.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !p.DeviceType.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        // With the slider raised, peers with no signal reading can't qualify.
        if (MinRssiSlider.Value > MinRssiSlider.Minimum &&
            (p.SignalDbm is not int signal || signal < MinRssiSlider.Value))
            return false;

        return true;
    }

    private bool _reloadingAdapters;

    private void ReloadAdapterList()
    {
        _reloadingAdapters = true;
        try
        {
            var adapters = _scanner.EnumerateAdapters();
            AdapterCombo.ItemsSource = adapters;
            AdapterCombo.SelectedItem =
                adapters.FirstOrDefault(a => a.Guid == _scanner.CurrentAdapterGuid);
        }
        finally
        {
            _reloadingAdapters = false;
        }
    }

    private void AdapterCombo_DropDownOpened(object sender, EventArgs e)
    {
        ReloadAdapterList(); // pick up USB adapters plugged in since startup
    }

    private void AdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only reachable while scanning is off — the combo is disabled otherwise.
        if (_reloadingAdapters ||
            AdapterCombo.SelectedItem is not WlanAdapter adapter ||
            adapter.Guid == _scanner.CurrentAdapterGuid)
            return;

        _scanner.SelectAdapter(adapter);
        ClearAccumulatedData();
        StatusText.Text = $"Switched to {adapter.Description} — turn scanning on to start.";
    }

    private async void ScanToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // initial IsChecked assignment during parse

        if (ScanToggle.IsChecked == true)
        {
            AdapterCombo.IsEnabled = false;
            ScanToggle.Content = "Scanning: On";
            ClearAccumulatedData();
            await RefreshAsync();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            AdapterCombo.IsEnabled = true;
            ScanToggle.Content = "Scanning: Off";
            StatusText.Text = "Scanning paused — table and graphs frozen; adapter can be switched.";
        }
    }

    private void ClearAccumulatedData()
    {
        _store.Entries.Clear();
        _view.Refresh();
        UpdateGraphs();
        AdviceText.Text = "Waiting for first scan...";
    }
}
