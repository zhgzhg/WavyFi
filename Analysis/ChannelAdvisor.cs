using System.Text;
using WifiOptimizer.Models;

namespace WifiOptimizer.Analysis;

/// <summary>
/// Scores channel congestion from scan results and produces plain-language
/// recommendations for the user's own network.
/// </summary>
public static class ChannelAdvisor
{
    // Non-overlapping 2.4 GHz channels — the only sensible choices there.
    private static readonly int[] Preferred24 = { 1, 6, 11 };

    // Common 20/40 MHz anchor channels; non-DFS listed first on purpose.
    private static readonly int[] NonDfs5 = { 36, 40, 44, 48, 149, 153, 157, 161, 165 };
    private static readonly int[] Dfs5 = { 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144 };

    public static string BuildReport(IReadOnlyList<NetworkEntry> networks)
    {
        if (networks.Count == 0)
            return "No scan data yet.";

        var sb = new StringBuilder();
        var own = networks.FirstOrDefault(n => n.IsConnected);

        var band24 = networks.Where(n => n.Band == "2.4 GHz").ToList();
        var band5 = networks.Where(n => n.Band == "5 GHz").ToList();
        var band6 = networks.Where(n => n.Band == "6 GHz").ToList();

        sb.AppendLine($"Environment: {band24.Count} networks on 2.4 GHz, {band5.Count} on 5 GHz, {band6.Count} on 6 GHz.");
        sb.AppendLine();

        // --- 2.4 GHz ---
        if (band24.Count > 0)
        {
            var best24 = Preferred24
                .OrderBy(ch => Congestion24(band24, ch, own))
                .First();
            sb.AppendLine($"2.4 GHz: best channel is {best24} " +
                          $"(least interference among 1/6/11; always avoid in-between channels).");
        }

        // --- 5 GHz ---
        if (band5.Count > 0 || band24.Count > 0)
        {
            var best5 = NonDfs5
                .OrderBy(ch => Congestion5(band5, ch, own))
                .First();
            var quietDfs = Dfs5
                .OrderBy(ch => Congestion5(band5, ch, own))
                .First();
            sb.AppendLine($"5 GHz: best non-DFS channel is {best5}.");
            if (Congestion5(band5, quietDfs, own) < Congestion5(band5, best5, own))
                sb.AppendLine($"       Channel {quietDfs} (DFS) is even quieter, if your router supports DFS.");
        }

        // --- 6 GHz ---
        if (band6.Count > 0)
            sb.AppendLine($"6 GHz: in use nearby and still sparse — if your router and devices support WiFi 6E/7, it's the cleanest option.");

        sb.AppendLine();

        // --- Advice about the user's own network ---
        if (own is not null)
        {
            sb.AppendLine($"Your network: \"{own.DisplayName}\" on channel {own.Channel} ({own.Band}), {own.Security}.");

            var competitors = networks.Count(n =>
                !IsOwnRouter(n, own) && n.Band == own.Band && ChannelsOverlap(own, n));
            if (competitors > 0)
                sb.AppendLine($"  - {competitors} other network(s) overlap your channel — consider moving to the recommendation above.");
            else
                sb.AppendLine("  - No overlapping neighbors on your channel. Good spot.");

            if (own.Band == "2.4 GHz" && band5.Count < band24.Count)
                sb.AppendLine("  - You're on the crowded 2.4 GHz band; prefer 5 GHz for devices that support it.");

            if (own.Security is "Open" or "WEP" or "WPA-Personal")
                sb.AppendLine($"  - Security is {own.Security}: upgrade to WPA2 or WPA3 in your router settings.");

            if (own.Cipher.Contains("TKIP") || own.Cipher.Contains("WEP"))
                sb.AppendLine($"  - Cipher is {own.Cipher} (legacy, slow, weak): switch to AES-CCMP in your router settings.");

            if (own.WpsVersion != "-")
                sb.AppendLine($"  - WPS {own.WpsVersion} is enabled: disable it if you don't use it — WPS PINs can be brute-forced.");
        }
        else
        {
            sb.AppendLine("Not connected to WiFi — connect to your own network to get personalized advice.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Weight neighbors by both spectral overlap (using their real
    /// bonded width) and signal strength. A network's energy reaches about
    /// width/10 channel numbers each side of its bonded center.</summary>
    private static double Congestion24(IEnumerable<NetworkEntry> nets, int channel, NetworkEntry? own)
    {
        double score = 0;
        foreach (var n in nets)
        {
            if (own is not null && IsOwnRouter(n, own)) continue;
            double reach = n.ChannelWidthMhz / 10.0 + 3; // their half-width + our half-width
            double overlap = Math.Max(0, 1.0 - Math.Abs(n.CenterChannel - channel) / reach);
            score += overlap * SignalWeight(n.Rssi);
        }
        return score;
    }

    /// <summary>5 GHz: competing when the candidate 20 MHz channel falls
    /// inside the neighbor's bonded span (plus its own half-width).</summary>
    private static double Congestion5(IEnumerable<NetworkEntry> nets, int channel, NetworkEntry? own)
    {
        double score = 0;
        foreach (var n in nets)
        {
            if (own is not null && IsOwnRouter(n, own)) continue;
            if (Math.Abs(n.CenterChannel - channel) <= n.ChannelWidthMhz / 10.0 + 2)
                score += SignalWeight(n.Rssi);
        }
        return score;
    }

    /// <summary>Heuristic: the same physical router's other radios and virtual
    /// BSSIDs share the middle four MAC octets — vendors derive per-radio MACs
    /// by tweaking the first and last octets. A 32-bit match makes accidental
    /// collisions with a true neighbor effectively impossible.</summary>
    private static bool IsOwnRouter(NetworkEntry n, NetworkEntry own)
    {
        if (n.Bssid == own.Bssid) return true;
        var a = n.Bssid.Split(':');
        var b = own.Bssid.Split(':');
        return a.Length == 6 && b.Length == 6 &&
               a[1] == b[1] && a[2] == b[2] && a[3] == b[3] && a[4] == b[4];
    }

    private static bool ChannelsOverlap(NetworkEntry a, NetworkEntry b) =>
        Math.Abs(a.CenterChannel - b.CenterChannel) <
        (a.ChannelWidthMhz + b.ChannelWidthMhz) / 10.0;

    /// <summary>A -50 dBm neighbor matters far more than a -90 dBm one.</summary>
    private static double SignalWeight(int rssi) =>
        Math.Clamp(rssi + 95, 0, 60) / 60.0;
}
