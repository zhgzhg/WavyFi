using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace WavyFi.Data;

/// <summary>
/// MAC-prefix → manufacturer lookup backed by an embedded, gzipped copy of
/// the Wireshark "manuf" file (IEEE OUI registry). Loaded lazily on first use.
///
/// Source / credit: the Wireshark project's curated registry snapshot,
/// https://www.wireshark.org/download/automated/data/manuf (data © IEEE
/// Registration Authority, compiled by Wireshark; updated weekly upstream).
/// To refresh the embedded copy, see "OUI vendor database" in README.md.
/// </summary>
public static class OuiDatabase
{
    private static readonly Lazy<Dictionary<uint, string>> Entries = new(Load);

    /// <summary>Resolve "AA:BB:CC:DD:EE:FF" to a vendor name. Locally
    /// administered addresses (randomized / virtual BSSIDs) never appear
    /// in the registry, so they resolve to "(unknown vendor)".</summary>
    public static string Lookup(string mac)
    {
        var parts = mac.Split(':');
        if (parts.Length < 3 ||
            !byte.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var b0) ||
            !byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var b1) ||
            !byte.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var b2))
            return "";

        if (Entries.Value.TryGetValue(((uint)b0 << 16) | ((uint)b1 << 8) | b2, out var vendor))
            return vendor;

        return (b0 & 0x02) != 0 ? "(unknown vendor)" : "";
    }

    private static Dictionary<uint, string> Load()
    {
        var result = new Dictionary<uint, string>(60000);
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("manuf.gz", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return result;

        using var raw = assembly.GetManifestResourceStream(resourceName)!;
        using var gz = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var cols = line.Split('\t', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2) continue;

            // Only plain 24-bit OUIs; skip the rarer /28 and /36 sub-blocks.
            var prefix = cols[0];
            if (prefix.Length != 8) continue;

            var hex = prefix.Replace(":", "");
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var key))
                continue;

            result[key] = cols.Length >= 3 ? cols[2] : cols[1];
        }
        return result;
    }
}
