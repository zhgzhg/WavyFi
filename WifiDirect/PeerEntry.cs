using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WifiOptimizer.WifiDirect;

/// <summary>
/// A WiFi Direct peer tracked for display, updated in place so the grid can
/// sort live without losing selection. Peers re-advertise roughly once per
/// watcher restart cycle (~15 s), hence the generous stale threshold.
/// </summary>
public class PeerEntry : INotifyPropertyChanged
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    private string _name = "";
    private bool _isPaired;
    private int? _signalDbm;
    private string _deviceType = "";
    private string _vendor = "";
    private string _address = "";
    private bool _isStale;
    private string _lastSeenText = "now";

    public PeerEntry(WifiDirectPeer peer, DateTime now)
    {
        Id = peer.Id;
        UpdateFrom(peer, now);
    }

    public string Id { get; }
    public DateTime LastSeen { get; private set; }

    public string Name { get => _name; private set => Set(ref _name, value); }

    public bool IsPaired
    {
        get => _isPaired;
        private set { if (Set(ref _isPaired, value)) OnPropertyChanged(nameof(PairedText)); }
    }

    public string PairedText => IsPaired ? "yes" : "";

    public int? SignalDbm
    {
        get => _signalDbm;
        private set { if (Set(ref _signalDbm, value)) OnPropertyChanged(nameof(SignalText)); }
    }

    public string SignalText => SignalDbm is int s ? $"{s} dBm" : "";

    public string DeviceType { get => _deviceType; private set => Set(ref _deviceType, value); }
    public string Vendor { get => _vendor; private set => Set(ref _vendor, value); }
    public string Address { get => _address; private set => Set(ref _address, value); }
    public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }
    public string LastSeenText { get => _lastSeenText; private set => Set(ref _lastSeenText, value); }

    public void UpdateFrom(WifiDirectPeer peer, DateTime now)
    {
        Name = peer.Name;
        IsPaired = peer.IsPaired;
        SignalDbm = peer.SignalDbm;
        DeviceType = peer.DeviceType;
        Vendor = peer.Vendor;
        Address = peer.Address;
        LastSeen = peer.LastSeen;
        Tick(now);
    }

    public void Tick(DateTime now)
    {
        var age = now - LastSeen;
        LastSeenText = age.TotalSeconds < 15 ? "now"
            : age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds}s ago"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : $"{(int)age.TotalHours}h ago";
        IsStale = age >= StaleAfter;
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
