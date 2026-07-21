using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace WavyFi.WifiDirect;

public record WifiDirectPeer(
    string Id, string Name, bool IsPaired,
    string Address, int? SignalDbm, string DeviceType, string Vendor,
    DateTime LastSeen);

/// <summary>
/// Continuously discovers nearby WiFi Direct devices (phones, TVs, printers,
/// Miracast receivers...) that are advertising themselves.
/// </summary>
public sealed class WifiDirectWatcher : IDisposable
{
    private static readonly string[] RequestedProperties =
    {
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.SignalStrength",
        "System.Devices.WiFiDirect.InformationElements",
    };

    private readonly DeviceWatcher _watcher;
    private readonly Dictionary<string, (DeviceInformation Info, DateTime LastSeen)> _devices = new();
    private readonly object _lock = new();
    private bool _disposed;

    public event Action? PeersChanged;

    /// <summary>Raised when a discovery sweep finishes (before the automatic
    /// restart) — the peer list is as complete as this sweep gets.</summary>
    public event Action? SweepCompleted;

    public WifiDirectWatcher()
    {
        var selector = WiFiDirectDevice.GetDeviceSelector(
            WiFiDirectDeviceSelectorType.AssociationEndpoint);

        _watcher = DeviceInformation.CreateWatcher(selector, RequestedProperties);
        _watcher.Added += OnAdded;
        _watcher.Updated += OnUpdated;
        // Removed is deliberately not handled: peers persist for the session
        // (persistence of vision) — the UI shows their age and fades them.
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Stopped += OnStopped;
    }

    public void Start() => _watcher.Start();

    public IReadOnlyList<WifiDirectPeer> GetPeers()
    {
        lock (_lock)
            return _devices.Values.Select(v => ToPeer(v.Info, v.LastSeen)).OrderBy(p => p.Name).ToList();
    }

    private static WifiDirectPeer ToPeer(DeviceInformation info, DateTime lastSeen)
    {
        info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var addressObj);
        info.Properties.TryGetValue("System.Devices.Aep.SignalStrength", out var signalObj);
        info.Properties.TryGetValue("System.Devices.WiFiDirect.InformationElements", out var iesObj);

        int? signal = null;
        try
        {
            if (signalObj is not null) signal = Convert.ToInt32(signalObj);
        }
        catch { /* unexpected property type from the driver — omit signal */ }
        var (deviceType, wpsDeviceName) = ParseP2pInfo(iesObj as byte[]);
        var name = !string.IsNullOrWhiteSpace(info.Name) ? info.Name
            : wpsDeviceName.Length > 0 ? wpsDeviceName
            : "(unnamed)";

        var address = (addressObj as string ?? "").ToUpperInvariant();
        return new WifiDirectPeer(
            info.Id, name,
            info.Pairing?.IsPaired ?? false,
            address, signal, deviceType,
            Data.OuiDatabase.Lookup(address),
            lastSeen);
    }

    /// <summary>The advertised information elements contain a WPS IE whose
    /// attributes describe the device: Primary Device Type (0x1054) and
    /// Device Name (0x1011).</summary>
    private static (string DeviceType, string DeviceName) ParseP2pInfo(byte[]? ies)
    {
        if (ies is null) return ("", "");
        string type = "", name = "";
        int pos = 0;

        while (pos + 2 <= ies.Length)
        {
            byte id = ies[pos];
            byte len = ies[pos + 1];
            int val = pos + 2;
            if (val + len > ies.Length) break;

            if (id == 221 && len >= 4 &&
                ies[val] == 0x00 && ies[val + 1] == 0x50 &&
                ies[val + 2] == 0xF2 && ies[val + 3] == 0x04)
            {
                int tlv = val + 4, tlvEnd = val + len;
                while (tlv + 4 <= tlvEnd)
                {
                    int attrType = (ies[tlv] << 8) | ies[tlv + 1];
                    int attrLen = (ies[tlv + 2] << 8) | ies[tlv + 3];
                    int attrVal = tlv + 4;
                    if (attrVal + attrLen > tlvEnd) break;

                    if (attrType == 0x1054 && attrLen >= 2)
                        type = DescribeDeviceCategory((ies[attrVal] << 8) | ies[attrVal + 1]);
                    else if (attrType == 0x1011 && attrLen > 0)
                        name = System.Text.Encoding.UTF8.GetString(ies, attrVal, attrLen).TrimEnd('\0');

                    tlv += 4 + attrLen;
                }
            }
            pos += 2 + len;
        }
        return (type, name);
    }

    private static string DescribeDeviceCategory(int category) => category switch
    {
        1 => "Computer",
        2 => "Input device",
        3 => "Printer/Scanner",
        4 => "Camera",
        5 => "Storage",
        6 => "Network device",
        7 => "Display/TV",
        8 => "Multimedia device",
        9 => "Gaming device",
        10 => "Phone",
        11 => "Audio device",
        12 => "Dock",
        _ => "Other device",
    };

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        lock (_lock)
            _devices[info.Id] = (info, DateTime.Now);
        PeersChanged?.Invoke();
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(update.Id, out var existing))
            {
                existing.Info.Update(update);
                _devices[update.Id] = (existing.Info, DateTime.Now);
            }
        }
        PeersChanged?.Invoke();
    }

    private async void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        SweepCompleted?.Invoke();

        // The watcher goes idle after the initial sweep; restart it so
        // discovery stays live while the app is open.
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (_disposed) return;
        try
        {
            _watcher.Stop();
        }
        catch { /* already stopping */ }
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        if (_disposed) return;
        try
        {
            _watcher.Start();
        }
        catch { /* shutting down */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher.Added -= OnAdded;
        _watcher.Updated -= OnUpdated;
        _watcher.EnumerationCompleted -= OnEnumerationCompleted;
        _watcher.Stopped -= OnStopped;
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            _watcher.Stop();
    }
}
