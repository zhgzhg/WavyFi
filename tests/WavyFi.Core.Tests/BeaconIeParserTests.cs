using WavyFi.Wlan;

namespace WavyFi.Core.Tests;

public class BeaconIeParserTests
{
    private static byte[] Ie(byte id, params byte[] body)
    {
        var ie = new byte[2 + body.Length];
        ie[0] = id;
        ie[1] = (byte)body.Length;
        body.CopyTo(ie, 2);
        return ie;
    }

    private static byte[] Concat(params byte[][] ies) =>
        ies.SelectMany(x => x).ToArray();

    // ----- WPS ------------------------------------------------------------

    [Fact]
    public void NoIes_YieldsDefaults()
    {
        var info = BeaconIeParser.Parse(Array.Empty<byte>(), primaryChannel: 6);
        Assert.Null(info.WpsVersion);
        Assert.Equal(20, info.WidthMhz);
        Assert.Equal(6, info.CenterChannel);
        Assert.False(info.Ht);
        Assert.Equal(0, info.MaxRateMbps);
    }

    [Fact]
    public void WpsIe_WithLegacyVersionAttribute_ParsesVersion()
    {
        // Vendor IE 221, OUI 00-50-F2 type 4, then TLV 0x104A len 1 value 0x10.
        var wps = Ie(221, 0x00, 0x50, 0xF2, 0x04, 0x10, 0x4A, 0x00, 0x01, 0x10);
        var info = BeaconIeParser.Parse(wps, 1);
        Assert.Equal("1.0", info.WpsVersion);
    }

    [Fact]
    public void WpsIe_WfaVendorExtensionVersion2_WinsOverLegacy()
    {
        var wps = Ie(221, 0x00, 0x50, 0xF2, 0x04,
            0x10, 0x4A, 0x00, 0x01, 0x10,             // Version = 1.0
            0x10, 0x49, 0x00, 0x06,                    // Vendor Extension, len 6
            0x00, 0x37, 0x2A,                          // WFA OUI
            0x00, 0x01, 0x20);                         // subelement 0 (Version2) = 0x20
        var info = BeaconIeParser.Parse(wps, 1);
        Assert.Equal("2.0", info.WpsVersion);
    }

    [Fact]
    public void WpsIe_WithoutVersionAttribute_DefaultsTo10()
    {
        var wps = Ie(221, 0x00, 0x50, 0xF2, 0x04);
        var info = BeaconIeParser.Parse(wps, 1);
        Assert.Equal("1.0", info.WpsVersion);
    }

    [Fact]
    public void NonWpsVendorIe_IsIgnored()
    {
        var vendor = Ie(221, 0x00, 0x50, 0xF2, 0x01, 0x01, 0x02); // WPA IE, not WPS
        var info = BeaconIeParser.Parse(vendor, 1);
        Assert.Null(info.WpsVersion);
    }

    // ----- channel width / center ------------------------------------------

    [Fact]
    public void HtOperation_SecondaryAbove_Gives40MhzCenterPlus2()
    {
        var htOp = Ie(61, /*primary*/ 6, /*info: offset=1|wide=0x04*/ 0x05, 0, 0, 0);
        var info = BeaconIeParser.Parse(htOp, 6);
        Assert.Equal(40, info.WidthMhz);
        Assert.Equal(8, info.CenterChannel);
    }

    [Fact]
    public void HtOperation_SecondaryBelow_Gives40MhzCenterMinus2()
    {
        var htOp = Ie(61, 13, /*offset=3|wide*/ 0x07, 0, 0, 0);
        var info = BeaconIeParser.Parse(htOp, 13);
        Assert.Equal(40, info.WidthMhz);
        Assert.Equal(11, info.CenterChannel);
    }

    [Fact]
    public void VhtOperation_80Mhz_UsesSegment0Center()
    {
        var vhtOp = Ie(192, /*cw*/ 1, /*seg0*/ 42, /*seg1*/ 0);
        var info = BeaconIeParser.Parse(vhtOp, 36);
        Assert.Equal(80, info.WidthMhz);
        Assert.Equal(42, info.CenterChannel);
    }

    [Fact]
    public void VhtOperation_160Mhz_Segment1EightApart_UsesSegment1Center()
    {
        var vhtOp = Ie(192, 1, 42, 50);
        var info = BeaconIeParser.Parse(vhtOp, 36);
        Assert.Equal(160, info.WidthMhz);
        Assert.Equal(50, info.CenterChannel);
    }

    [Fact]
    public void VhtOverridesHtOperation_RegardlessOfOrder()
    {
        var htOp = Ie(61, 36, 0x05, 0, 0, 0);
        var vhtOp = Ie(192, 1, 42, 0);
        foreach (var ies in new[] { Concat(htOp, vhtOp), Concat(vhtOp, htOp) })
        {
            var info = BeaconIeParser.Parse(ies, 36);
            Assert.Equal(80, info.WidthMhz);
            Assert.Equal(42, info.CenterChannel);
        }
    }

    [Fact]
    public void EhtOperation_320Mhz_OverridesVht_AndUsesCcfs1()
    {
        var vhtOp = Ie(192, 1, 42, 0);
        // ext 106: params bit0 = op info present; MCS(4); control=4 (320); ccfs0; ccfs1.
        var ehtOp = Ie(255, 106, 0x01, 0, 0, 0, 0, /*control*/ 4, /*ccfs0*/ 47, /*ccfs1*/ 63);
        var info = BeaconIeParser.Parse(Concat(vhtOp, ehtOp), 37);
        Assert.Equal(320, info.WidthMhz);
        Assert.Equal(63, info.CenterChannel);
    }

    [Fact]
    public void HeOperation_6GhzInfo_Gives160Mhz()
    {
        // ext 36: params(3) with bit17 (6 GHz info present) = byte3 bit1;
        // color(1), mcs(2); then 6 GHz info: primary, control(cw=3), seg0, seg1, minrate.
        var heOp = Ie(255, 36, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
            /*primary*/ 37, /*control cw=3*/ 0x03, /*seg0*/ 39, /*seg1*/ 47, /*minrate*/ 0);
        var info = BeaconIeParser.Parse(heOp, 37);
        Assert.Equal(160, info.WidthMhz);
        Assert.Equal(47, info.CenterChannel);
    }

    // ----- capabilities / standards flags ----------------------------------

    [Fact]
    public void CapabilityElements_SetGenerationFlags()
    {
        var ies = Concat(
            Ie(45, 0, 0, 0, 0xFF, 0, 0, 0),  // HT caps
            Ie(191, 0, 0, 0, 0, 0xFA, 0xFF), // VHT caps
            Ie(255, 35, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFA, 0xFF), // HE caps
            Ie(255, 108, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2)); // EHT caps
        var info = BeaconIeParser.Parse(ies, 1);
        Assert.True(info.Ht);
        Assert.True(info.Vht);
        Assert.True(info.He);
        Assert.True(info.Eht);
    }

    // ----- max rate ---------------------------------------------------------

    [Fact]
    public void HtCaps_TwoStreams_ShortGi_40Mhz_Gives300Mbps()
    {
        var htCaps = Ie(45, /*SGI20|SGI40*/ 0x60, 0, 0, /*mcs 0-7*/ 0xFF, /*mcs 8-15*/ 0xFF, 0, 0);
        var htOp = Ie(61, 6, 0x05, 0, 0, 0); // 40 MHz
        var info = BeaconIeParser.Parse(Concat(htCaps, htOp), 6);
        Assert.Equal(300, info.MaxRateMbps, 1);
    }

    [Fact]
    public void VhtCaps_TwoStreamsMcs9_ShortGi80_Gives866Mbps()
    {
        // RX MCS map: nss1=2 (MCS0-9), nss2=2, rest unsupported -> 0xFFFA.
        var vhtCaps = Ie(191, /*SGI80*/ 0x20, 0, 0, 0, 0xFA, 0xFF);
        var vhtOp = Ie(192, 1, 42, 0); // 80 MHz
        var info = BeaconIeParser.Parse(Concat(vhtCaps, vhtOp), 36);
        Assert.Equal(866.7, info.MaxRateMbps, 1);
    }

    [Fact]
    public void HeCaps_TwoStreamsMcs11_80Mhz_Gives1201Mbps()
    {
        var heCaps = Ie(255, 35, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            /*rx map nss1..2 = MCS0-11*/ 0xFA, 0xFF);
        var vhtOp = Ie(192, 1, 42, 0); // 80 MHz
        var info = BeaconIeParser.Parse(Concat(heCaps, vhtOp), 36);
        Assert.Equal(1201, info.MaxRateMbps, 0);
    }

    [Fact]
    public void EhtCaps_TwoStreamsMcs13_BeatsHeRate()
    {
        var heCaps = Ie(255, 35, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFA, 0xFF);
        var ehtCaps = Ie(255, 108, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, /*mcs0-9*/ 2, /*10-11*/ 2, /*12-13*/ 2);
        var vhtOp = Ie(192, 1, 42, 0); // 80 MHz
        var info = BeaconIeParser.Parse(Concat(heCaps, ehtCaps, vhtOp), 36);
        Assert.Equal(1441.2, info.MaxRateMbps, 1); // MCS13 x 2SS x 80 MHz
    }

    // ----- robustness --------------------------------------------------------

    [Fact]
    public void TruncatedAndMalformedIes_DoNotThrow()
    {
        // Length byte exceeding the buffer, dangling header, zero-length IEs.
        var cases = new[]
        {
            new byte[] { 45 },
            new byte[] { 45, 200, 1, 2, 3 },
            new byte[] { 221, 4, 0x00, 0x50 },
            new byte[] { 255, 0 },
            new byte[] { 0, 0, 0, 0 },
        };
        foreach (var ies in cases)
        {
            var info = BeaconIeParser.Parse(ies, 11);
            Assert.Equal(11, info.CenterChannel);
            Assert.Equal(20, info.WidthMhz);
        }
    }
}
