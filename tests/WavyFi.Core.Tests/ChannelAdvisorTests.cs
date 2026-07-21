using WavyFi.Analysis;
using WavyFi.Models;

namespace WavyFi.Core.Tests;

public class ChannelAdvisorTests
{
    private static NetworkEntry Entry(
        string ssid, string bssid, int channel, int rssi,
        string band = "2.4 GHz", int width = 20, int? center = null,
        bool connected = false, Guid adapter = default)
    {
        return new NetworkEntry(new WifiNetwork
        {
            Ssid = ssid,
            Bssid = bssid,
            Channel = channel,
            CenterChannel = center ?? channel,
            ChannelWidthMhz = width,
            Rssi = rssi,
            Band = band,
            FrequencyMhz = band == "2.4 GHz" ? 2407 + channel * 5 : 5000 + channel * 5,
            Security = "WPA2-Personal",
            Cipher = "AES-CCMP",
            IsConnected = connected,
            AdapterGuid = adapter,
        }, DateTime.Now);
    }

    [Fact]
    public void EmptyInput_SaysNoData()
    {
        Assert.Equal("No scan data yet.", ChannelAdvisor.BuildReport(new List<NetworkEntry>()));
    }

    [Fact]
    public void RecommendsTheQuietNonOverlappingChannel()
    {
        var report = ChannelAdvisor.BuildReport(new List<NetworkEntry>
        {
            Entry("A", "AA:00:00:00:00:01", 1, -50),
            Entry("B", "AA:00:00:00:00:02", 6, -55),
        });
        Assert.Contains("best channel is 11", report);
    }

    [Fact]
    public void OwnRouterOtherRadio_IsNotCountedAsOverlap()
    {
        // Same middle four octets = same physical router (virtual BSSID).
        var report = ChannelAdvisor.BuildReport(new List<NetworkEntry>
        {
            Entry("mine5g", "62:C7:BF:07:11:B5", 36, -50, band: "5 GHz", width: 80, center: 42, connected: true),
            Entry("mine5", "50:C7:BF:07:11:B5", 36, -52, band: "5 GHz", width: 80, center: 42),
        });
        Assert.Contains("No overlapping neighbors", report);
    }

    [Fact]
    public void UnrelatedNeighborOnSameChannel_IsCountedAsOverlap()
    {
        var report = ChannelAdvisor.BuildReport(new List<NetworkEntry>
        {
            Entry("mine", "62:C7:BF:07:11:B5", 36, -50, band: "5 GHz", width: 80, center: 42, connected: true),
            Entry("them", "98:25:4A:5F:AE:E7", 36, -70, band: "5 GHz", width: 80, center: 42),
        });
        Assert.Contains("1 other network(s) overlap", report);
    }

    [Fact]
    public void SameBssidViaTwoAdapters_CountsOnce()
    {
        var report = ChannelAdvisor.BuildReport(new List<NetworkEntry>
        {
            Entry("X", "AA:00:00:00:00:01", 1, -50, adapter: Guid.NewGuid()),
            Entry("X", "AA:00:00:00:00:01", 1, -60, adapter: Guid.NewGuid()),
        });
        Assert.Contains("1 networks on 2.4 GHz", report);
    }

    [Fact]
    public void WeakSecurityAndWps_TriggerAdvice()
    {
        var own = new NetworkEntry(new WifiNetwork
        {
            Ssid = "old",
            Bssid = "AA:00:00:00:00:01",
            Channel = 6,
            CenterChannel = 6,
            ChannelWidthMhz = 20,
            Rssi = -40,
            Band = "2.4 GHz",
            Security = "WEP",
            Cipher = "WEP-104",
            WpsVersion = "1.0",
            IsConnected = true,
        }, DateTime.Now);

        var report = ChannelAdvisor.BuildReport(new List<NetworkEntry> { own });
        Assert.Contains("upgrade to WPA2 or WPA3", report);
        Assert.Contains("switch to AES-CCMP", report);
        Assert.Contains("WPS 1.0 is enabled", report);
    }
}
