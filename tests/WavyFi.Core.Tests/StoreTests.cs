using WavyFi.Models;
using WavyFi.WifiDirect;

namespace WavyFi.Core.Tests;

public class NetworkStoreTests
{
    private static WifiNetwork Net(string bssid, int rssi, Guid adapter = default) => new()
    {
        Ssid = "X",
        Bssid = bssid,
        Rssi = rssi,
        Band = "2.4 GHz",
        Channel = 6,
        CenterChannel = 6,
        AdapterGuid = adapter,
    };

    [Fact]
    public void SameBssid_UpdatesInPlace()
    {
        var store = new NetworkStore();
        var t = DateTime.Now;
        store.ApplyScan(new[] { Net("AA:BB:CC:00:00:01", -50) }, t);
        store.ApplyScan(new[] { Net("AA:BB:CC:00:00:01", -60) }, t.AddSeconds(5));

        var entry = Assert.Single(store.Entries);
        Assert.Equal(-60, entry.Rssi);
        Assert.Equal(2, entry.History.Count);
    }

    [Fact]
    public void DuplicateKeysInOneScan_DoNotPoisonTheStore()
    {
        var store = new NetworkStore();
        var t = DateTime.Now;
        var dup = new[] { Net("AA:BB:CC:00:00:01", -50), Net("AA:BB:CC:00:00:01", -55) };
        store.ApplyScan(dup, t);
        store.ApplyScan(dup, t.AddSeconds(5)); // second pass must not throw

        Assert.Single(store.Entries);
    }

    [Fact]
    public void SameBssidOnTwoAdapters_KeepsSeparateRows()
    {
        var store = new NetworkStore();
        var t = DateTime.Now;
        store.ApplyScan(new[]
        {
            Net("AA:BB:CC:00:00:01", -50, Guid.NewGuid()),
            Net("AA:BB:CC:00:00:01", -60, Guid.NewGuid()),
        }, t);

        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void UnseenEntries_GoStaleThenExpire()
    {
        var store = new NetworkStore();
        var t = DateTime.Now;
        store.ApplyScan(new[] { Net("AA:BB:CC:00:00:01", -50) }, t);

        store.ApplyScan(Array.Empty<WifiNetwork>(), t.AddSeconds(30));
        Assert.True(Assert.Single(store.Entries).IsStale);

        store.ApplyScan(Array.Empty<WifiNetwork>(), t.AddSeconds(121));
        Assert.Empty(store.Entries);
    }
}

public class PeerStoreTests
{
    private static WifiDirectPeer Peer(string id, int? rssi, DateTime seen) =>
        new(id, "Phone", false, "AA:BB:CC:DD:EE:FF", rssi, "Phone", "", seen);

    [Fact]
    public void ApplyMergesByIdAndRemovesAbsent()
    {
        var store = new PeerStore();
        var t = DateTime.Now;
        store.Apply(new[] { Peer("a", -50, t), Peer("b", null, t) }, t);
        Assert.Equal(2, store.Entries.Count);

        store.Apply(new[] { Peer("a", -55, t.AddSeconds(10)) }, t.AddSeconds(10));
        var remaining = Assert.Single(store.Entries);
        Assert.Equal("a", remaining.Id);
        Assert.Equal(-55, remaining.SignalDbm);
    }

    [Fact]
    public void SignalReadings_AccumulateAsSparseHistory()
    {
        var store = new PeerStore();
        var t = DateTime.Now;
        store.Apply(new[] { Peer("a", -50, t) }, t);
        store.Apply(new[] { Peer("a", -52, t.AddSeconds(15)) }, t.AddSeconds(15));
        store.Apply(new[] { Peer("a", null, t.AddSeconds(30)) }, t.AddSeconds(30)); // no reading

        Assert.Equal(2, Assert.Single(store.Entries).History.Count);
    }
}
