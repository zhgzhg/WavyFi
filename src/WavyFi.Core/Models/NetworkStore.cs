using System.Collections.ObjectModel;

namespace WavyFi.Models;

/// <summary>
/// Merges each scan into a persistent collection keyed by BSSID.
/// Must be updated on the UI thread (the collection is bound to the grid).
/// </summary>
public class NetworkStore
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RemoveAfter = TimeSpan.FromSeconds(120);

    public ObservableCollection<NetworkEntry> Entries { get; } = new();

    public void ApplyScan(IReadOnlyList<WifiNetwork> scan, DateTime now)
    {
        // Keyed by (adapter, BSSID) — each selected adapter contributes its
        // own row per network. Built tolerantly (no ToDictionary) so a
        // duplicate key can never poison the store into throwing every scan.
        var byKey = new Dictionary<string, NetworkEntry>();
        foreach (var e in Entries)
            byKey[e.Key] = e;

        foreach (var n in scan)
        {
            var key = $"{n.Bssid}|{n.AdapterGuid}";
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.UpdateFrom(n, now);
            }
            else
            {
                var entry = new NetworkEntry(n, now);
                byKey[key] = entry;
                Entries.Add(entry);
            }
        }

        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            e.Tick(now, StaleAfter);
            if (now - e.LastSeen > RemoveAfter)
                Entries.RemoveAt(i);
        }
    }
}
