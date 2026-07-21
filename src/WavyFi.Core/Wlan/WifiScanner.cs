using System.Runtime.InteropServices;
using WavyFi.Models;

namespace WavyFi.Wlan;

public record WlanAdapter(Guid Guid, string Description, int Index);

public sealed class WifiScanner : IDisposable
{
    private readonly IntPtr _handle;
    // Held in a field so the GC never collects the marshaled callback thunk
    // while the native side can still invoke it.
    private readonly WlanInterop.WlanNotificationCallback _notificationCallback;
    // Swapped atomically as a whole list — read from the notification thread.
    private List<WlanAdapter> _selected = new();

    public IReadOnlyList<WlanAdapter> SelectedAdapters => _selected;

    public string InterfaceDescription => _selected.Count switch
    {
        0 => "(no WiFi adapter)",
        1 => _selected[0].Description,
        _ => $"{_selected.Count} adapters",
    };

    /// <summary>Raised (on a native worker thread) when a selected adapter
    /// finishes a scan sweep — its BSS cache is fresh at that moment.
    /// Carries the adapter's interface GUID.</summary>
    public event Action<Guid>? ScanCompleted;

    public WifiScanner()
    {
        var result = WlanInterop.WlanOpenHandle(
            WlanInterop.ClientVersion, IntPtr.Zero, out _, out _handle);
        if (result != 0)
            throw new InvalidOperationException($"WlanOpenHandle failed: {result}");

        SelectFirstAdapter();

        _notificationCallback = OnNotification;
        WlanInterop.WlanRegisterNotification(
            _handle, WlanInterop.NotificationSourceAcm, true,
            _notificationCallback, IntPtr.Zero, IntPtr.Zero, out _);
    }

    private void OnNotification(ref WlanNotificationData data, IntPtr context)
    {
        // Scan-fail still means the sweep ended — read whatever landed.
        if (data.NotificationSource == WlanInterop.NotificationSourceAcm &&
            data.NotificationCode is WlanInterop.AcmScanComplete or WlanInterop.AcmScanFail)
        {
            var guid = data.InterfaceGuid;
            if (_selected.Any(a => a.Guid == guid))
                ScanCompleted?.Invoke(guid);
        }
    }

    public IReadOnlyList<WlanAdapter> EnumerateAdapters()
    {
        var adapters = new List<WlanAdapter>();
        if (WlanInterop.WlanEnumInterfaces(_handle, IntPtr.Zero, out var listPtr) != 0)
            return adapters;

        try
        {
            // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, InterfaceInfo[]
            int count = Marshal.ReadInt32(listPtr);
            int entrySize = Marshal.SizeOf<WlanInterfaceInfo>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(listPtr + 8 + i * entrySize);
                adapters.Add(new WlanAdapter(info.InterfaceGuid, info.InterfaceDescription, i));
            }
        }
        finally
        {
            WlanInterop.WlanFreeMemory(listPtr);
        }
        return adapters;
    }

    public void SelectAdapters(IEnumerable<WlanAdapter> adapters)
    {
        _selected = adapters.ToList();
    }

    private void SelectFirstAdapter()
    {
        if (EnumerateAdapters().FirstOrDefault() is { } first)
            _selected = new List<WlanAdapter> { first };
    }

    /// <summary>Asks every selected adapter to start a fresh scan. Results land
    /// in each adapter's cache a few seconds later, picked up by GetNetworks.</summary>
    public void TriggerScan()
    {
        if (_selected.Count == 0)
        {
            SelectFirstAdapter();
            if (_selected.Count == 0) return;
        }
        foreach (var adapter in _selected)
        {
            var guid = adapter.Guid;
            WlanInterop.WlanScan(_handle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>The BSSID (and real SSID) of the adapter's current association,
    /// straight from the driver. Matching "connected" by BSSID is exact — SSID
    /// name matching fails when the connected AP beacons a hidden SSID.</summary>
    private (string Bssid, string Ssid) GetCurrentConnection(Guid interfaceGuid)
    {
        var result = WlanInterop.WlanQueryInterface(
            _handle, ref interfaceGuid, WlanInterop.OpcodeCurrentConnection,
            IntPtr.Zero, out _, out var ptr, IntPtr.Zero);
        if (result != 0)
            return ("", "");

        try
        {
            var attrs = Marshal.PtrToStructure<WlanConnectionAttributes>(ptr);
            if (attrs.State != WlanInterop.InterfaceStateConnected)
                return ("", "");
            return (
                string.Join(":", attrs.Association.Bssid.Select(b => b.ToString("X2"))),
                attrs.Association.Ssid.ToString());
        }
        finally
        {
            WlanInterop.WlanFreeMemory(ptr);
        }
    }

    /// <summary>Union of the selected adapters' scan results, one row per
    /// (adapter, BSSID). Adapters that vanished are dropped from the selection
    /// silently as long as at least one other adapter still delivers.</summary>
    public IReadOnlyList<WifiNetwork> GetNetworks()
    {
        if (_selected.Count == 0)
        {
            SelectFirstAdapter();
            if (_selected.Count == 0) return Array.Empty<WifiNetwork>();
        }

        var results = new List<WifiNetwork>();
        List<WlanAdapter>? vanished = null;

        foreach (var adapter in _selected)
        {
            var batch = GetNetworksFor(adapter);
            if (batch is null)
                (vanished ??= new List<WlanAdapter>()).Add(adapter);
            else
                results.AddRange(batch);
        }

        if (vanished is not null)
        {
            _selected = _selected.Except(vanished).ToList();
            if (_selected.Count == 0)
            {
                SelectFirstAdapter();
                throw new InvalidOperationException(
                    "The selected WiFi adapter is no longer available — scanning will fall back to the next available adapter.");
            }
        }

        return results;
    }

    /// <summary>One adapter's scan results, or null if the adapter vanished.</summary>
    private IReadOnlyList<WifiNetwork>? GetNetworksFor(WlanAdapter adapter)
    {
        var interfaceGuid = adapter.Guid;
        var securityBySsid = ReadSecurityInfo(interfaceGuid);
        var (connectedBssid, connectedSsid) = GetCurrentConnection(interfaceGuid);
        var networks = new List<WifiNetwork>();

        var result = WlanInterop.WlanGetNetworkBssList(
            _handle, ref interfaceGuid, IntPtr.Zero,
            Dot11BssType.Any, false, IntPtr.Zero, out var listPtr);
        if (result != 0)
        {
            if (result == 5)
                throw new InvalidOperationException(
                    "Access denied reading scan results. Check that Location access is enabled in Windows Settings > Privacy & security > Location.");

            if (EnumerateAdapters().All(a => a.Guid != adapter.Guid))
                return null; // unplugged / disabled

            throw new InvalidOperationException(
                $"Could not read scan results ({adapter.Description}): {new System.ComponentModel.Win32Exception((int)result).Message}");
        }

        try
        {
            // WLAN_BSS_LIST: dwTotalSize, dwNumberOfItems, wlanBssEntries[]
            int count = Marshal.ReadInt32(listPtr, 4);
            int entrySize = Marshal.SizeOf<WlanBssEntry>();
            var entriesPtr = listPtr + 8;

            for (int i = 0; i < count; i++)
            {
                var entryPtr = entriesPtr + i * entrySize;
                var entry = Marshal.PtrToStructure<WlanBssEntry>(entryPtr);
                var ssid = entry.Ssid.ToString();
                int freqMhz = (int)(entry.ChCenterFrequencyKhz / 1000);
                int channel = FrequencyToChannel(freqMhz);
                // Copy the IE blob out of native memory once so the parser
                // works on managed bytes (and is unit-testable offline).
                var ie = default(BeaconInfo) with { WidthMhz = 20, CenterChannel = channel };
                if (entry.IeSize is > 0 and < 8192)
                {
                    var ieBytes = new byte[entry.IeSize];
                    Marshal.Copy(entryPtr + (int)entry.IeOffset, ieBytes, 0, (int)entry.IeSize);
                    ie = BeaconIeParser.Parse(ieBytes, channel);
                }
                var band = FrequencyToBand(freqMhz);
                var bssid = string.Join(":", entry.Bssid.Select(b => b.ToString("X2")));
                bool isConnected = connectedBssid.Length > 0 && bssid == connectedBssid;

                // A hidden-SSID beacon from our own AP: we still know its name.
                if (ssid.Length == 0 && isConnected)
                    ssid = connectedSsid;

                var (auth, cipher) = securityBySsid.GetValueOrDefault(ssid, ("?", "?"));

                networks.Add(new WifiNetwork
                {
                    Ssid = ssid,
                    Bssid = bssid,
                    AdapterGuid = adapter.Guid,
                    AdapterIndex = adapter.Index,
                    AdapterName = adapter.Description,
                    Vendor = Data.OuiDatabase.Lookup(bssid),
                    Rssi = entry.Rssi,
                    SignalPercent = RssiToPercent(entry.Rssi),
                    FrequencyMhz = freqMhz,
                    Channel = channel,
                    ChannelWidthMhz = ie.WidthMhz,
                    CenterChannel = ie.CenterChannel,
                    Band = band,
                    Standards = BuildStandards(band, entry.RateSet, ie),
                    Security = auth,
                    Cipher = cipher,
                    WpsVersion = ie.WpsVersion ?? "-",
                    RatesCsv = FormatRates(entry.RateSet),
                    MaxRateMbps = ie.MaxRateMbps > 0 ? ie.MaxRateMbps : MaxLegacyRate(entry.RateSet),
                    IsConnected = isConnected,
                });
            }
        }
        finally
        {
            WlanInterop.WlanFreeMemory(listPtr);
        }

        // The BSS list can report the same BSSID more than once (per-PHY
        // entries); keep one per BSSID or downstream keying breaks.
        return networks
            .GroupBy(n => n.Bssid)
            .Select(g => g.OrderByDescending(n => n.IsConnected).ThenByDescending(n => n.Rssi).First())
            .ToList();
    }

    /// <summary>Legacy standards come from the rate set: DSSS rates
    /// (1/2/5.5/11) mean 802.11b, OFDM rates mean g (2.4 GHz) or a (5 GHz).</summary>
    private static string BuildStandards(string band, WlanRateSet rateSet, BeaconInfo ie)
    {
        bool dsss = false, ofdm = false;
        int count = Math.Min((int)rateSet.RateSetLength, rateSet.RateSet?.Length ?? 0);
        for (int i = 0; i < count; i++)
        {
            double mbps = (rateSet.RateSet![i] & 0x7FFF) * 0.5;
            if (mbps is 1 or 2 or 5.5 or 11) dsss = true;
            else if (mbps >= 6) ofdm = true;
        }

        var parts = new List<string>();
        switch (band)
        {
            case "2.4 GHz":
                if (dsss) parts.Add("b");
                if (ofdm) parts.Add("g");
                if (ie.Ht) parts.Add("n");
                if (ie.He) parts.Add("ax");
                if (ie.Eht) parts.Add("be");
                break;
            case "5 GHz":
                if (ofdm) parts.Add("a");
                if (ie.Ht) parts.Add("n");
                if (ie.Vht) parts.Add("ac");
                if (ie.He) parts.Add("ax");
                if (ie.Eht) parts.Add("be");
                break;
            default: // 6 GHz is ax/be only
                if (ie.He) parts.Add("ax");
                if (ie.Eht) parts.Add("be");
                break;
        }
        return string.Join("/", parts);
    }

    /// <summary>Rate set entries are in 0.5 Mbps units; bit 15 marks basic rates.</summary>
    private static string FormatRates(WlanRateSet rateSet)
    {
        // Observed drivers report the entry count here (not bytes as documented);
        // the zero-rate filter below keeps either interpretation harmless.
        int count = Math.Min((int)rateSet.RateSetLength, rateSet.RateSet?.Length ?? 0);
        var speeds = new SortedSet<double>();
        for (int i = 0; i < count; i++)
        {
            double mbps = (rateSet.RateSet![i] & 0x7FFF) * 0.5;
            if (mbps > 0) speeds.Add(mbps);
        }
        return string.Join(", ", speeds.Select(s =>
            s.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static double MaxLegacyRate(WlanRateSet rateSet)
    {
        int count = Math.Min((int)rateSet.RateSetLength, rateSet.RateSet?.Length ?? 0);
        double max = 0;
        for (int i = 0; i < count; i++)
            max = Math.Max(max, (rateSet.RateSet![i] & 0x7FFF) * 0.5);
        return max;
    }

    private Dictionary<string, (string Auth, string Cipher)> ReadSecurityInfo(Guid interfaceGuid)
    {
        var security = new Dictionary<string, (string, string)>();

        var result = WlanInterop.WlanGetAvailableNetworkList(
            _handle, ref interfaceGuid, 0, IntPtr.Zero, out var listPtr);
        if (result != 0)
            return security;

        try
        {
            // WLAN_AVAILABLE_NETWORK_LIST: dwNumberOfItems, dwIndex, Network[]
            int count = Marshal.ReadInt32(listPtr);
            int entrySize = Marshal.SizeOf<WlanAvailableNetwork>();
            var entriesPtr = listPtr + 8;

            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WlanAvailableNetwork>(entriesPtr + i * entrySize);
                var ssid = entry.Ssid.ToString();
                security[ssid] = (DescribeAuth(entry.AuthAlgorithm), DescribeCipher(entry.CipherAlgorithm));
            }
        }
        finally
        {
            WlanInterop.WlanFreeMemory(listPtr);
        }

        return security;
    }

    private static string DescribeAuth(Dot11AuthAlgorithm auth) => auth switch
    {
        Dot11AuthAlgorithm.Open => "Open",
        Dot11AuthAlgorithm.SharedKey => "WEP",
        Dot11AuthAlgorithm.Wpa => "WPA-Enterprise",
        Dot11AuthAlgorithm.WpaPsk => "WPA-Personal",
        Dot11AuthAlgorithm.WpaNone => "WPA-None",
        Dot11AuthAlgorithm.Rsna => "WPA2-Enterprise",
        Dot11AuthAlgorithm.RsnaPsk => "WPA2-Personal",
        Dot11AuthAlgorithm.Wpa3Enterprise192 => "WPA3-Enterprise 192-bit",
        Dot11AuthAlgorithm.Wpa3Sae => "WPA3-Personal",
        Dot11AuthAlgorithm.Owe => "OWE (Enhanced Open)",
        Dot11AuthAlgorithm.Wpa3Enterprise => "WPA3-Enterprise",
        _ => auth.ToString(),
    };

    private static string DescribeCipher(Dot11CipherAlgorithm cipher) => cipher switch
    {
        Dot11CipherAlgorithm.None => "None",
        Dot11CipherAlgorithm.Wep40 => "WEP-40",
        Dot11CipherAlgorithm.Tkip => "TKIP",
        Dot11CipherAlgorithm.Ccmp => "AES-CCMP",
        Dot11CipherAlgorithm.Wep104 => "WEP-104",
        Dot11CipherAlgorithm.Bip => "BIP",
        Dot11CipherAlgorithm.Gcmp => "GCMP-128",
        Dot11CipherAlgorithm.Gcmp256 => "GCMP-256",
        Dot11CipherAlgorithm.Ccmp256 => "CCMP-256",
        Dot11CipherAlgorithm.WpaUseGroup => "Group",
        Dot11CipherAlgorithm.Wep => "WEP",
        _ => cipher.ToString(),
    };

    public static int FrequencyToChannel(int freqMhz) => freqMhz switch
    {
        2484 => 14,
        >= 2412 and <= 2472 => (freqMhz - 2407) / 5,
        >= 5150 and <= 5895 => (freqMhz - 5000) / 5,
        >= 5955 and <= 7115 => (freqMhz - 5950) / 5,
        _ => 0,
    };

    public static string FrequencyToBand(int freqMhz) => freqMhz switch
    {
        < 3000 => "2.4 GHz",
        < 5950 => "5 GHz",
        _ => "6 GHz",
    };

    private static int RssiToPercent(int rssi) =>
        Math.Clamp((rssi + 100) * 2, 0, 100);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            WlanInterop.WlanRegisterNotification(
                _handle, WlanInterop.NotificationSourceNone, true,
                null, IntPtr.Zero, IntPtr.Zero, out _);
            WlanInterop.WlanCloseHandle(_handle, IntPtr.Zero);
        }
    }
}
