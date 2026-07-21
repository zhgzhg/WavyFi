using WavyFi.Data;
using WavyFi.Models;
using WavyFi.Wlan;

namespace WavyFi.Core.Tests;

public class AgeFormatTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(90, "1.5m")]
    [InlineData(600, "10m")]
    [InlineData(3600, "1h")]
    [InlineData(5400, "1.5h")]
    [InlineData(604800, "168h")]
    public void Short_UsesCompactUnits(int seconds, string expected) =>
        Assert.Equal(expected, AgeFormat.Short(seconds));
}

public class CsvFormatTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with,comma", "\"with,comma\"")]
    [InlineData("with\"quote", "\"with\"\"quote\"")]
    [InlineData("with\nnewline", "\"with\nnewline\"")]
    [InlineData("", "")]
    public void Escape_QuotesOnlyWhenNeeded(string value, string expected) =>
        Assert.Equal(expected, CsvFormat.Escape(value));
}

public class FrequencyMappingTests
{
    [Theory]
    [InlineData(2412, 1)]
    [InlineData(2472, 13)]
    [InlineData(2484, 14)]   // the odd one out: 12 MHz above channel 13
    [InlineData(5180, 36)]
    [InlineData(5825, 165)]
    [InlineData(5955, 1)]    // 6 GHz channel numbering restarts
    [InlineData(6115, 33)]
    [InlineData(1000, 0)]    // out of any band
    public void FrequencyToChannel(int mhz, int expected) =>
        Assert.Equal(expected, WifiScanner.FrequencyToChannel(mhz));

    [Theory]
    [InlineData(2412, "2.4 GHz")]
    [InlineData(5180, "5 GHz")]
    [InlineData(5955, "6 GHz")]
    public void FrequencyToBand(int mhz, string expected) =>
        Assert.Equal(expected, WifiScanner.FrequencyToBand(mhz));
}

public class OuiDatabaseTests
{
    [Fact]
    public void KnownOui_ResolvesVendor()
    {
        // 00:00:01 has been Xerox since the dawn of the registry.
        Assert.Contains("Xerox", OuiDatabase.Lookup("00:00:01:AA:BB:CC"));
    }

    [Fact]
    public void LocallyAdministeredMac_IsUnknownVendor()
    {
        Assert.Equal("(unknown vendor)", OuiDatabase.Lookup("62:C7:BF:07:11:B5"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("ZZ:XX:YY:00:00:00")]
    public void MalformedInput_YieldsEmpty(string mac) =>
        Assert.Equal("", OuiDatabase.Lookup(mac));
}
