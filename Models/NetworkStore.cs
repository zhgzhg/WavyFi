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
        // Built tolerantly (no ToDictionary) so a duplicate BSSID can never
        // poison the store into throwing on every subsequent scan.
        var byBssid = new Dictionary<string, NetworkEntry>();
        foreach (var e in Entries)
            byBssid[e.Bssid] = e;

        foreach (var n in scan)
        {
            if (byBssid.TryGetValue(n.Bssid, out var existing))
            {
                existing.UpdateFrom(n, now);
            }
            else
            {
                var entry = new NetworkEntry(n, now);
                byBssid[n.Bssid] = entry;
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
