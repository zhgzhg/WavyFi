using WavyFi.Models;
using WavyFi.WifiDirect;

namespace WavyFi.Demo;

/// <summary>
/// Synthetic scan feed behind the --demo flag: a fixed roster of made-up
/// networks and peers so screenshots expose no real environment. RSSI
/// jitters a little each sweep so the history graphs draw live-looking
/// traces, and entries occasionally miss a sweep so ages vary naturally.
/// </summary>
public sealed class DemoData
{
    public const string AdapterDisplay = "[0] Demo Wireless Adapter";

    private static readonly Guid AdapterGuid = new("00000000-0000-4000-8000-0000d3300001");
    private const string AdapterName = "Demo Wireless Adapter";

    private const string Rates24 = "1, 2, 5.5, 11, 6, 9, 12, 18, 24, 36, 48, 54";
    private const string Rates5 = "6, 9, 12, 18, 24, 36, 48, 54";

    private readonly Random _rng = new(20260724);
    private DateTime _printerSeen;

    private sealed record Ap(
        string Ssid, string Bssid, string Vendor, int BaseRssi,
        string Band, int Channel, int WidthMhz, int CenterChannel,
        string Standards, string Security, string Cipher, string Wps,
        double MaxRateMbps, bool Connected);

    // Channel math must stay self-consistent (band, primary, width, center)
    // or the channel graphs draw nonsense lobes.
    private static readonly Ap[] Roster =
    {
        new("Sunflower_Home", "A4:2B:B0:5C:11:38", "TP-Link", -46,
            "5 GHz", 36, 80, 42, "a/n/ac/ax", "WPA2/WPA3-Personal", "AES-CCMP/GCMP-256", "-", 1201.0, Connected: true),
        new("Sunflower_Home", "A4:2B:B0:5C:11:37", "TP-Link", -43,
            "2.4 GHz", 6, 20, 6, "b/g/n/ax", "WPA2/WPA3-Personal", "AES-CCMP/GCMP-256", "2.0", 286.8, Connected: false),
        new("Sunflower_Guest", "A6:2B:B0:5C:11:39", "TP-Link", -47,
            "5 GHz", 36, 80, 42, "a/n/ac/ax", "WPA3-Personal", "AES-GCMP-256", "-", 1201.0, Connected: false),
        new("FRITZ!Box 7590 KL", "3C:A6:2F:8E:04:D1", "AVM", -61,
            "2.4 GHz", 1, 20, 1, "b/g/n", "WPA2-Personal", "AES-CCMP", "2.0", 144.4, Connected: false),
        new("FRITZ!Box 7590 KL", "3C:A6:2F:8E:04:D2", "AVM", -64,
            "5 GHz", 52, 80, 58, "a/n/ac", "WPA2-Personal", "AES-CCMP", "-", 866.7, Connected: false),
        new("WLAN-482913", "44:CE:7D:AA:29:60", "Sagemcom", -72,
            "2.4 GHz", 11, 20, 11, "b/g/n/ax", "WPA2-Personal", "AES-CCMP", "2.0", 286.8, Connected: false),
        new("Office-Loft 5G", "78:8A:20:17:C3:44", "Ubiquiti", -58,
            "5 GHz", 149, 80, 155, "a/n/ac/ax", "WPA2-Enterprise", "AES-CCMP", "-", 1201.0, Connected: false),
        new("CoffeeCorner Free WiFi", "88:15:44:2B:9A:07", "Cisco Meraki", -74,
            "2.4 GHz", 6, 20, 6, "b/g/n", "Open", "-", "-", 144.4, Connected: false),
        new("", "9C:3D:CF:66:F0:2B", "Netgear", -67,
            "5 GHz", 44, 40, 46, "a/n/ac", "WPA2-Personal", "AES-CCMP", "-", 400.0, Connected: false),
        new("MeshPro-6G", "04:D9:F5:3B:72:E8", "ASUSTek Computer", -55,
            "6 GHz", 37, 160, 47, "ax/be", "WPA3-Personal", "AES-GCMP-256", "-", 2882.0, Connected: false),
        new("Milkyway", "48:8F:5A:D4:1E:90", "MikroTik", -70,
            "2.4 GHz", 11, 20, 11, "b/g/n", "WPA2-Personal", "AES-CCMP", "-", 144.4, Connected: false),
        new("Skynet_2G", "A0:E4:CB:19:77:5C", "Zyxel", -79,
            "2.4 GHz", 3, 20, 3, "b/g/n", "WPA2-Personal", "AES-CCMP", "1.0", 72.2, Connected: false),
    };

    public IReadOnlyList<WifiNetwork> NextScan()
    {
        var result = new List<WifiNetwork>(Roster.Length);
        foreach (var ap in Roster)
        {
            // A missed sweep now and then makes the age column vary and
            // eventually fades a row, like a real environment.
            if (!ap.Connected && _rng.Next(100) < 12) continue;

            int rssi = ap.BaseRssi + _rng.Next(-2, 3);
            result.Add(new WifiNetwork
            {
                Ssid = ap.Ssid,
                Bssid = ap.Bssid,
                AdapterGuid = AdapterGuid,
                AdapterIndex = 0,
                AdapterName = AdapterName,
                Vendor = ap.Vendor,
                Rssi = rssi,
                SignalPercent = Math.Clamp((rssi + 100) * 2, 0, 100),
                Channel = ap.Channel,
                ChannelWidthMhz = ap.WidthMhz,
                CenterChannel = ap.CenterChannel,
                FrequencyMhz = Freq(ap.Band, ap.Channel),
                Band = ap.Band,
                Standards = ap.Standards,
                Security = ap.Security,
                Cipher = ap.Cipher,
                WpsVersion = ap.Wps,
                RatesCsv = ap.Band == "2.4 GHz" ? Rates24 : Rates5,
                MaxRateMbps = ap.MaxRateMbps,
                IsConnected = ap.Connected,
            });
        }
        return result;
    }

    public IReadOnlyList<WifiDirectPeer> Peers(DateTime now)
    {
        // The printer re-advertises rarely, so its age counts up between
        // bursts — mirrors how real peers behave.
        if (_printerSeen == default || (now - _printerSeen).TotalSeconds > 40)
            _printerSeen = now;

        return new[]
        {
            new WifiDirectPeer("demo-peer-tv", "[TV] Living Room", IsPaired: true,
                "8C:79:F5:D2:66:1A", -54 + _rng.Next(-2, 3), "Display/TV", "Samsung Electronics", now),
            new WifiDirectPeer("demo-peer-printer", "DIRECT-7B-OfficeJet 9020", IsPaired: false,
                "F4:39:09:AC:52:7B", -63 + _rng.Next(-2, 3), "Printer/Scanner", "HP", _printerSeen),
            new WifiDirectPeer("demo-peer-phone", "Pixel 9 Pro", IsPaired: false,
                "94:EB:2C:31:5D:E0", -48 + _rng.Next(-3, 4), "Phone", "Google", now),
        };
    }

    private static int Freq(string band, int ch) => band switch
    {
        "2.4 GHz" => 2407 + ch * 5,
        "5 GHz" => 5000 + ch * 5,
        _ => 5950 + ch * 5, // 6 GHz
    };
}
