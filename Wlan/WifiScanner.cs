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
                var ie = ParseIes(entryPtr, entry.IeOffset, entry.IeSize, channel);
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

    private readonly record struct BssIeInfo(
        string? WpsVersion, int WidthMhz, int CenterChannel,
        bool Ht, bool Vht, bool He, bool Eht, double MaxRateMbps);

    /// <summary>Single pass over the beacon information elements, extracting:
    /// WPS version (vendor IE 221 / OUI 00-50-F2 type 4; Version2 in the WFA
    /// vendor extension wins over the legacy Version attribute), and the real
    /// channel width + bonded-span center from HT Operation (61),
    /// VHT Operation (192) and HE Operation 6 GHz info (255 ext 36).</summary>
    private static BssIeInfo ParseIes(IntPtr entryPtr, uint ieOffset, uint ieSize, int primaryChannel)
    {
        string? wps = null;
        bool wpsV2Found = false;
        int width = 20, center = primaryChannel;
        bool wideOpSeen = false; // VHT/HE info overrides HT
        bool ehtOpSeen = false;  // EHT (WiFi 7) info overrides everything
        bool ht = false, vht = false, he = false, eht = false;
        bool htSgi20 = false, htSgi40 = false, vhtSgi80 = false, vhtSgi160 = false;
        int htMaxMcsIndex = -1;              // 0..31 across 1-4 spatial streams
        int vhtMaxNss = 0, vhtMaxMcs = 0;
        int heMaxNss = 0, heMaxMcs = 0;
        int ehtMaxNss = 0, ehtMaxMcs = 0;
        long end = ieOffset + ieSize, pos = ieOffset;

        byte B(long offset) => Marshal.ReadByte(entryPtr, (int)offset);

        while (pos + 2 <= end)
        {
            byte id = B(pos), len = B(pos + 1);
            long val = pos + 2;
            if (val + len > end) break;

            switch (id)
            {
                case 45: // HT Capabilities -> 802.11n
                    ht = true;
                    if (len >= 7)
                    {
                        htSgi20 |= (B(val) & 0x20) != 0;
                        htSgi40 |= (B(val) & 0x40) != 0;
                        // RX MCS bitmap starts at offset 3; MCS 0-31 = 1-4 streams.
                        for (int bit = 31; bit >= 0; bit--)
                            if ((B(val + 3 + bit / 8) & (1 << (bit % 8))) != 0)
                            {
                                htMaxMcsIndex = bit;
                                break;
                            }
                    }
                    break;
                case 191: // VHT Capabilities -> 802.11ac
                    vht = true;
                    if (len >= 6)
                    {
                        vhtSgi80 |= (B(val) & 0x20) != 0;
                        vhtSgi160 |= (B(val) & 0x40) != 0;
                        int vhtMap = B(val + 4) | (B(val + 5) << 8);
                        (vhtMaxNss, vhtMaxMcs) = MaxFromMcsMap(vhtMap, 7, 8, 9);
                    }
                    break;
                case 61 when len >= 2 && !wideOpSeen: // HT Operation
                {
                    int secondaryOffset = B(val + 1) & 0x03;
                    bool wide = (B(val + 1) & 0x04) != 0;
                    if (wide && secondaryOffset == 1) { width = 40; center = primaryChannel + 2; }
                    else if (wide && secondaryOffset == 3) { width = 40; center = primaryChannel - 2; }
                    break;
                }
                case 192 when len >= 3 && !ehtOpSeen: // VHT Operation
                {
                    int cw = B(val);
                    int seg0 = B(val + 1), seg1 = B(val + 2);
                    if (cw == 1)
                    {
                        wideOpSeen = true;
                        if (seg1 != 0 && Math.Abs(seg1 - seg0) == 8) { width = 160; center = seg1; }
                        else { width = 80; center = seg0; }
                    }
                    else if (cw is 2 or 3) // deprecated 160 / 80+80 signaling
                    {
                        wideOpSeen = true;
                        width = 160;
                        center = seg0;
                    }
                    break;
                }
                case 255 when len >= 1: // Element ID Extension
                {
                    byte ext = B(val);
                    if (ext == 35) // HE Capabilities -> 802.11ax
                    {
                        he = true;
                        // ext(1) + MAC caps(6) + PHY caps(11), then RX HE-MCS map (<=80 MHz).
                        if (len >= 20)
                        {
                            int heMap = B(val + 18) | (B(val + 19) << 8);
                            (heMaxNss, heMaxMcs) = MaxFromMcsMap(heMap, 7, 9, 11);
                        }
                    }
                    else if (ext == 108) // EHT Capabilities -> 802.11be
                    {
                        eht = true;
                        // ext(1) + EHT MAC caps(2) + EHT PHY caps(9), then the
                        // EHT-MCS map (<=80 MHz): one byte per MCS group, low
                        // nibble = max RX spatial streams (valid 1-8).
                        if (len >= 15)
                        {
                            int nss09 = B(val + 12) & 0x0F;
                            int nss1011 = B(val + 13) & 0x0F;
                            int nss1213 = B(val + 14) & 0x0F;
                            if (nss1213 is >= 1 and <= 8) (ehtMaxNss, ehtMaxMcs) = (nss1213, 13);
                            else if (nss1011 is >= 1 and <= 8) (ehtMaxNss, ehtMaxMcs) = (nss1011, 11);
                            else if (nss09 is >= 1 and <= 8) (ehtMaxNss, ehtMaxMcs) = (nss09, 9);
                        }
                    }
                    else if (ext == 106 && len >= 9 && (B(val + 1) & 0x01) != 0) // EHT Operation
                    {
                        // EHT Operation Information present: Control(1) CCFS0(1) CCFS1(1).
                        int cwEht = B(val + 6) & 0x07;
                        int ccfs0 = B(val + 7), ccfs1 = B(val + 8);
                        ehtOpSeen = true;
                        wideOpSeen = true;
                        width = cwEht switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, _ => 320 };
                        center = width >= 160 && ccfs1 != 0 ? ccfs1 : ccfs0;
                    }
                    else if (ext == 36 && len >= 7 && !ehtOpSeen) // HE Operation
                    {
                        // 24-bit HE Operation Parameters at val+1..3 (little-endian).
                        bool vhtInfoPresent = (B(val + 2) & 0x40) != 0; // bit 14
                        bool coHostedBss = (B(val + 2) & 0x80) != 0;    // bit 15
                        bool sixGhzInfo = (B(val + 3) & 0x02) != 0;     // bit 17
                        if (sixGhzInfo)
                        {
                            long o = val + 7 + (vhtInfoPresent ? 3 : 0) + (coHostedBss ? 1 : 0);
                            if (o + 5 <= val + len)
                            {
                                int cw6 = B(o + 1) & 0x03;
                                int seg0 = B(o + 2), seg1 = B(o + 3);
                                wideOpSeen = true;
                                width = cw6 switch { 0 => 20, 1 => 40, 2 => 80, _ => 160 };
                                center = cw6 == 0 ? B(o)
                                    : cw6 == 3 && seg1 != 0 ? seg1
                                    : seg0;
                            }
                        }
                    }
                    break;
                }
                case 221 when len >= 4 &&
                              B(val) == 0x00 && B(val + 1) == 0x50 &&
                              B(val + 2) == 0xF2 && B(val + 3) == 0x04: // WPS
                {
                    wps ??= "1.0"; // WPS present even if no version attribute found
                    long tlv = val + 4, tlvEnd = val + len;
                    while (tlv + 4 <= tlvEnd)
                    {
                        int type = (B(tlv) << 8) | B(tlv + 1);
                        int tlen = (B(tlv + 2) << 8) | B(tlv + 3);
                        long tval = tlv + 4;
                        if (tval + tlen > tlvEnd) break;

                        if (type == 0x104A && tlen >= 1 && !wpsV2Found)
                        {
                            byte v = B(tval);
                            wps = $"{v >> 4}.{v & 0xF}";
                        }
                        else if (type == 0x1049 && tlen >= 5 &&
                                 B(tval) == 0x00 && B(tval + 1) == 0x37 && B(tval + 2) == 0x2A)
                        {
                            long sub = tval + 3, subEnd = tval + tlen;
                            while (sub + 2 <= subEnd)
                            {
                                byte sid = B(sub), slen = B(sub + 1);
                                if (sub + 2 + slen > subEnd) break;
                                if (sid == 0x00 && slen >= 1)
                                {
                                    byte v2 = B(sub + 2);
                                    wps = $"{v2 >> 4}.{v2 & 0xF}";
                                    wpsV2Found = true;
                                    break;
                                }
                                sub += 2 + slen;
                            }
                        }
                        tlv += 4 + tlen;
                    }
                    break;
                }
            }
            pos += 2 + len;
        }

        // Top PHY rate from the newest advertised generation at the operating
        // width. EHT reuses HE numerology (same symbol time and tone counts),
        // adding MCS 12/13 (4096-QAM).
        bool shortGi = width switch
        {
            >= 160 => vhtSgi160,
            >= 80 => vhtSgi80,
            >= 40 => htSgi40,
            _ => htSgi20,
        };
        double maxRate = 0;
        if (ehtMaxNss > 0)
            maxRate = PhyRate(ehtMaxMcs, ehtMaxNss, width, isHe: true, shortGi: false);
        else if (heMaxNss > 0)
            maxRate = PhyRate(heMaxMcs, heMaxNss, width, isHe: true, shortGi: false);
        else if (vhtMaxNss > 0)
            maxRate = PhyRate(vhtMaxMcs, vhtMaxNss, width, isHe: false, shortGi);
        else if (htMaxMcsIndex >= 0)
            maxRate = PhyRate(htMaxMcsIndex % 8, htMaxMcsIndex / 8 + 1,
                Math.Min(width, 40), isHe: false, shortGi);

        return new BssIeInfo(wps, width, center, ht, vht, he, eht, maxRate);
    }

    /// <summary>Highest spatial-stream count an MCS map supports, with its MCS
    /// ceiling. Both VHT and HE maps pack 2 bits per stream (1..8); the value
    /// selects an MCS ceiling, 3 = stream count not supported.</summary>
    private static (int Nss, int Mcs) MaxFromMcsMap(int map, int mcs0, int mcs1, int mcs2)
    {
        int bestNss = 0, bestMcs = 0;
        for (int nss = 1; nss <= 8; nss++)
        {
            int v = (map >> ((nss - 1) * 2)) & 3;
            if (v == 3) continue;
            bestNss = nss;
            bestMcs = v switch { 0 => mcs0, 1 => mcs1, _ => mcs2 };
        }
        return (bestNss, bestMcs);
    }

    /// <summary>Data bits carried per subcarrier per symbol for MCS 0-13
    /// (modulation order × coding rate), BPSK 1/2 through 4096-QAM 5/6.</summary>
    private static readonly double[] McsDataBits =
        { 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 4.5, 5.0, 6.0, 20.0 / 3.0, 7.5, 25.0 / 3.0, 9.0, 10.0 };

    /// <summary>PHY rate in Mbps = bits/subcarrier × data subcarriers × streams
    /// / symbol duration. HE uses 12.8 µs symbols (+0.8 µs GI) and ~4× the
    /// subcarriers of HT/VHT (3.2 µs symbols + 0.8/0.4 µs GI).</summary>
    private static double PhyRate(int mcs, int nss, int widthMhz, bool isHe, bool shortGi)
    {
        if (mcs < 0 || mcs >= McsDataBits.Length || nss <= 0) return 0;
        int subcarriers = isHe
            ? widthMhz switch { 40 => 468, 80 => 980, 160 => 1960, 320 => 3920, _ => 234 }
            : widthMhz switch { 40 => 108, 80 => 234, 160 => 468, _ => 52 };
        double symbolUs = isHe ? 13.6 : shortGi ? 3.6 : 4.0;
        return McsDataBits[mcs] * subcarriers * nss / symbolUs;
    }

    /// <summary>Legacy standards come from the rate set: DSSS rates
    /// (1/2/5.5/11) mean 802.11b, OFDM rates mean g (2.4 GHz) or a (5 GHz).</summary>
    private static string BuildStandards(string band, WlanRateSet rateSet, BssIeInfo ie)
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
