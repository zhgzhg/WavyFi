using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WavyFi.Models;

/// <summary>
/// A network tracked across scans. Entries persist when they drop out of a
/// scan (marked stale and faded) instead of disappearing immediately.
/// </summary>
public class NetworkEntry : INotifyPropertyChanged
{
    private string _ssid = "";
    private string _vendor = "";
    private int _rssi;
    private int _signalPercent;
    private int _channel;
    private int _channelWidthMhz = 20;
    private int _centerChannel;
    private int _frequencyMhz;
    private string _band = "";
    private string _standards = "";
    private string _security = "";
    private string _cipher = "";
    private string _wpsVersion = "-";
    private string _ratesCsv = "";
    private double _maxRateMbps;
    private bool _isConnected;
    private bool _isStale;
    private int _lastSeenSeconds;

    public NetworkEntry(WifiNetwork n, DateTime now)
    {
        Bssid = n.Bssid;
        AdapterGuid = n.AdapterGuid;
        AdapterIndex = n.AdapterIndex;
        AdapterName = n.AdapterName;
        UpdateFrom(n, now);
    }

    public string Bssid { get; }
    public Guid AdapterGuid { get; }
    public int AdapterIndex { get; }
    public string AdapterName { get; }

    /// <summary>Identity across scans: one entry per (adapter, BSSID).</summary>
    public string Key => $"{Bssid}|{AdapterGuid}";

    public string AdapterText => $"[{AdapterIndex}] {AdapterName}";

    public DateTime LastSeen { get; private set; }

    /// <summary>RSSI samples from each scan the network appeared in (last 10 min).</summary>
    public List<(DateTime Time, int Rssi)> History { get; } = new();

    public string Ssid
    {
        get => _ssid;
        private set { if (Set(ref _ssid, value)) OnPropertyChanged(nameof(DisplayName)); }
    }

    public string DisplayName => string.IsNullOrEmpty(Ssid) ? "(hidden)" : Ssid;
    public string Vendor { get => _vendor; private set => Set(ref _vendor, value); }
    public int Rssi { get => _rssi; private set => Set(ref _rssi, value); }
    public int SignalPercent { get => _signalPercent; private set => Set(ref _signalPercent, value); }
    public int Channel { get => _channel; private set => Set(ref _channel, value); }
    public int ChannelWidthMhz { get => _channelWidthMhz; private set => Set(ref _channelWidthMhz, value); }
    public int CenterChannel { get => _centerChannel; private set => Set(ref _centerChannel, value); }
    public int FrequencyMhz { get => _frequencyMhz; private set => Set(ref _frequencyMhz, value); }
    public string Band
    {
        get => _band;
        private set
        {
            if (Set(ref _band, value))
            {
                OnPropertyChanged(nameof(BandText));
                OnPropertyChanged(nameof(Generation)); // WiFi 6E depends on band
            }
        }
    }

    /// <summary>Band without the unit — the column header carries "GHz".</summary>
    public string BandText => Band.Replace(" GHz", "");
    public string Standards
    {
        get => _standards;
        private set { if (Set(ref _standards, value)) OnPropertyChanged(nameof(Generation)); }
    }

    /// <summary>WiFi Alliance generation name for the newest supported
    /// standard. WiFi 1-3 were never officially certified names but are the
    /// common retro-names for b/a/g. ax on 6 GHz is marketed as WiFi 6E.</summary>
    public string Generation
    {
        get
        {
            var s = Standards.Split('/');
            return s.Contains("be") ? "WiFi 7"
                : s.Contains("ax") ? (Band == "6 GHz" ? "WiFi 6E" : "WiFi 6")
                : s.Contains("ac") ? "WiFi 5"
                : s.Contains("n") ? "WiFi 4"
                : s.Contains("g") ? "WiFi 3"
                : s.Contains("a") ? "WiFi 2"
                : s.Contains("b") ? "WiFi 1"
                : "";
        }
    }
    public string Security { get => _security; private set => Set(ref _security, value); }
    public string Cipher { get => _cipher; private set => Set(ref _cipher, value); }
    public string WpsVersion { get => _wpsVersion; private set => Set(ref _wpsVersion, value); }
    public string RatesCsv { get => _ratesCsv; private set => Set(ref _ratesCsv, value); }
    public double MaxRateMbps { get => _maxRateMbps; private set => Set(ref _maxRateMbps, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }

    public int LastSeenSeconds { get => _lastSeenSeconds; private set => Set(ref _lastSeenSeconds, value); }

    public void UpdateFrom(WifiNetwork n, DateTime now)
    {
        Ssid = n.Ssid;
        Vendor = n.Vendor;
        Rssi = n.Rssi;
        SignalPercent = n.SignalPercent;
        Channel = n.Channel;
        ChannelWidthMhz = n.ChannelWidthMhz;
        CenterChannel = n.CenterChannel;
        FrequencyMhz = n.FrequencyMhz;
        Band = n.Band;
        Standards = n.Standards;
        Security = n.Security;
        Cipher = n.Cipher;
        WpsVersion = n.WpsVersion;
        RatesCsv = n.RatesCsv;
        MaxRateMbps = n.MaxRateMbps;
        IsConnected = n.IsConnected;
        LastSeen = now;
        IsStale = false;
        LastSeenSeconds = 0;

        History.Add((now, n.Rssi));
        while (History.Count > 0 && now - History[0].Time > TimeSpan.FromMinutes(10))
            History.RemoveAt(0);
    }

    /// <summary>Refresh age display; entries not seen recently become stale.</summary>
    public void Tick(DateTime now, TimeSpan staleAfter)
    {
        var age = now - LastSeen;
        LastSeenSeconds = (int)age.TotalSeconds;
        IsStale = age >= staleAfter;
        if (IsStale) IsConnected = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
