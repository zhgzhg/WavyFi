namespace WifiOptimizer.Models;

public class WifiNetwork
{
    public string Ssid { get; init; } = "";
    public string Bssid { get; init; } = "";
    /// <summary>AP manufacturer resolved from the BSSID's OUI.</summary>
    public string Vendor { get; init; } = "";
    public int Rssi { get; init; }
    public int SignalPercent { get; init; }
    public int Channel { get; init; }
    public int ChannelWidthMhz { get; init; } = 20;
    /// <summary>Center of the full bonded span; equals Channel at 20 MHz.</summary>
    public int CenterChannel { get; init; }
    public int FrequencyMhz { get; init; }
    public string Band { get; init; } = "";
    /// <summary>802.11 generations the AP supports, e.g. "b/g/n/ax".</summary>
    public string Standards { get; init; } = "";
    public string Security { get; init; } = "";
    public string Cipher { get; init; } = "";
    public string WpsVersion { get; init; } = "-";
    public string RatesCsv { get; init; } = "";
    public bool IsConnected { get; init; }

    public string DisplayName => string.IsNullOrEmpty(Ssid) ? "(hidden)" : Ssid;
}
