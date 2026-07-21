namespace WavyFi.Models;

public static class CsvFormat
{
    /// <summary>RFC-4180-style field escaping: quote when the value contains
    /// a comma, quote or newline; double embedded quotes.</summary>
    public static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
