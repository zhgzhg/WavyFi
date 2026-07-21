using System.Collections.ObjectModel;
using WavyFi.WifiDirect;

namespace WavyFi.Models;

/// <summary>
/// Merges WiFi Direct watcher snapshots into a persistent collection keyed by
/// device id, updating entries in place so grid sorting and selection survive.
/// Must be updated on the UI thread (the collection is bound to the grid).
/// </summary>
public class PeerStore
{
    public ObservableCollection<PeerEntry> Entries { get; } = new();

    public void Apply(IReadOnlyList<WifiDirectPeer> peers, DateTime now)
    {
        var byId = new Dictionary<string, PeerEntry>();
        foreach (var entry in Entries)
            byId[entry.Id] = entry;

        var seen = new HashSet<string>();
        foreach (var peer in peers)
        {
            seen.Add(peer.Id);
            if (byId.TryGetValue(peer.Id, out var entry))
            {
                entry.UpdateFrom(peer, now);
            }
            else
            {
                var created = new PeerEntry(peer, now);
                byId[peer.Id] = created;
                Entries.Add(created);
            }
        }

        for (int i = Entries.Count - 1; i >= 0; i--)
            if (!seen.Contains(Entries[i].Id))
                Entries.RemoveAt(i);
    }

    /// <summary>Refresh ages/staleness without new data.</summary>
    public void Tick(DateTime now)
    {
        foreach (var entry in Entries)
            entry.Tick(now);
    }
}
