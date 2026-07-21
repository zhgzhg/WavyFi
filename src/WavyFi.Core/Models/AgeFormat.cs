using System.Globalization;

namespace WavyFi.Models;

public static class AgeFormat
{
    /// <summary>Compact age: "42s", "1.5m", "2.3h" (invariant decimal point
    /// so the values stay CSV-friendly).</summary>
    public static string Short(int seconds) => seconds switch
    {
        < 60 => $"{seconds}s",
        < 3600 => (seconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + "m",
        _ => (seconds / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + "h",
    };
}
