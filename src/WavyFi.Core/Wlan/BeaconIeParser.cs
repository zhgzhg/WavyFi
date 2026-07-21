namespace WavyFi.Wlan;

/// <summary>Facts extracted from a beacon's information elements.</summary>
public readonly record struct BeaconInfo(
    string? WpsVersion, int WidthMhz, int CenterChannel,
    bool Ht, bool Vht, bool He, bool Eht, double MaxRateMbps);

/// <summary>
/// Single pass over a beacon's information elements, extracting:
/// WPS version (vendor IE 221 / OUI 00-50-F2 type 4; Version2 in the WFA
/// vendor extension wins over the legacy Version attribute), the real channel
/// width and bonded-span center (HT Operation 61, VHT Operation 192,
/// HE Operation 6 GHz info and EHT Operation via element 255), supported
/// generations (HT/VHT/HE/EHT capabilities), and the top PHY rate computed
/// from the newest advertised MCS map at the operating width.
/// </summary>
public static class BeaconIeParser
{
    public static BeaconInfo Parse(ReadOnlySpan<byte> ies, int primaryChannel)
    {
        string? wps = null;
        bool wpsV2Found = false;
        int width = 20, center = primaryChannel;
        bool wideOpSeen = false; // VHT/HE info overrides HT
        bool ehtOpSeen = false;  // EHT (WiFi 7) info overrides everything
        bool ht = false, vht = false, he = false, eht = false;
        bool htSgi20 = false, htSgi40 = false, vhtSgi80 = false, vhtSgi160 = false;
        int htMaxMcsIndex = -1;              // 0..31 across 1-4 spatial streams
        int vhtMaxNss = 0, vhtMaxMcs = 0;
        int heMaxNss = 0, heMaxMcs = 0;
        int ehtMaxNss = 0, ehtMaxMcs = 0;

        int pos = 0;
        while (pos + 2 <= ies.Length)
        {
            byte id = ies[pos], len = ies[pos + 1];
            int val = pos + 2;
            if (val + len > ies.Length) break;

            switch (id)
            {
                case 45: // HT Capabilities -> 802.11n
                    ht = true;
                    if (len >= 7)
                    {
                        htSgi20 |= (ies[val] & 0x20) != 0;
                        htSgi40 |= (ies[val] & 0x40) != 0;
                        // RX MCS bitmap starts at offset 3; MCS 0-31 = 1-4 streams.
                        for (int bit = 31; bit >= 0; bit--)
                            if ((ies[val + 3 + bit / 8] & (1 << (bit % 8))) != 0)
                            {
                                htMaxMcsIndex = bit;
                                break;
                            }
                    }
                    break;
                case 191: // VHT Capabilities -> 802.11ac
                    vht = true;
                    if (len >= 6)
                    {
                        vhtSgi80 |= (ies[val] & 0x20) != 0;
                        vhtSgi160 |= (ies[val] & 0x40) != 0;
                        int vhtMap = ies[val + 4] | (ies[val + 5] << 8);
                        (vhtMaxNss, vhtMaxMcs) = MaxFromMcsMap(vhtMap, 7, 8, 9);
                    }
                    break;
                case 61 when len >= 2 && !wideOpSeen: // HT Operation
                {
                    int secondaryOffset = ies[val + 1] & 0x03;
                    bool wide = (ies[val + 1] & 0x04) != 0;
                    if (wide && secondaryOffset == 1) { width = 40; center = primaryChannel + 2; }
                    else if (wide && secondaryOffset == 3) { width = 40; center = primaryChannel - 2; }
                    break;
                }
                case 192 when len >= 3 && !ehtOpSeen: // VHT Operation
                {
                    int cw = ies[val];
                    int seg0 = ies[val + 1], seg1 = ies[val + 2];
                    if (cw == 1)
                    {
                        wideOpSeen = true;
                        if (seg1 != 0 && Math.Abs(seg1 - seg0) == 8) { width = 160; center = seg1; }
                        else { width = 80; center = seg0; }
                    }
                    else if (cw is 2 or 3) // deprecated 160 / 80+80 signaling
                    {
                        wideOpSeen = true;
                        width = 160;
                        center = seg0;
                    }
                    break;
                }
                case 255 when len >= 1: // Element ID Extension
                {
                    byte ext = ies[val];
                    if (ext == 35) // HE Capabilities -> 802.11ax
                    {
                        he = true;
                        // ext(1) + MAC caps(6) + PHY caps(11), then RX HE-MCS map (<=80 MHz).
                        if (len >= 20)
                        {
                            int heMap = ies[val + 18] | (ies[val + 19] << 8);
                            (heMaxNss, heMaxMcs) = MaxFromMcsMap(heMap, 7, 9, 11);
                        }
                    }
                    else if (ext == 108) // EHT Capabilities -> 802.11be
                    {
                        eht = true;
                        // ext(1) + EHT MAC caps(2) + EHT PHY caps(9), then the
                        // EHT-MCS map (<=80 MHz): one byte per MCS group, low
                        // nibble = max RX spatial streams (valid 1-8).
                        if (len >= 15)
                        {
                            int nss09 = ies[val + 12] & 0x0F;
                            int nss1011 = ies[val + 13] & 0x0F;
                            int nss1213 = ies[val + 14] & 0x0F;
                            if (nss1213 is >= 1 and <= 8) (ehtMaxNss, ehtMaxMcs) = (nss1213, 13);
                            else if (nss1011 is >= 1 and <= 8) (ehtMaxNss, ehtMaxMcs) = (nss1011, 11);
                            else if (nss09 is >= 1 and <= 8) (ehtMaxNss, ehtMaxMcs) = (nss09, 9);
                        }
                    }
                    else if (ext == 106 && len >= 9 && (ies[val + 1] & 0x01) != 0) // EHT Operation
                    {
                        // EHT Operation Information present: Control(1) CCFS0(1) CCFS1(1).
                        int cwEht = ies[val + 6] & 0x07;
                        int ccfs0 = ies[val + 7], ccfs1 = ies[val + 8];
                        ehtOpSeen = true;
                        wideOpSeen = true;
                        width = cwEht switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, _ => 320 };
                        center = width >= 160 && ccfs1 != 0 ? ccfs1 : ccfs0;
                    }
                    else if (ext == 36 && len >= 7 && !ehtOpSeen) // HE Operation
                    {
                        // 24-bit HE Operation Parameters at val+1..3 (little-endian).
                        bool vhtInfoPresent = (ies[val + 2] & 0x40) != 0; // bit 14
                        bool coHostedBss = (ies[val + 2] & 0x80) != 0;    // bit 15
                        bool sixGhzInfo = (ies[val + 3] & 0x02) != 0;     // bit 17
                        if (sixGhzInfo)
                        {
                            int o = val + 7 + (vhtInfoPresent ? 3 : 0) + (coHostedBss ? 1 : 0);
                            if (o + 5 <= val + len)
                            {
                                int cw6 = ies[o + 1] & 0x03;
                                int seg0 = ies[o + 2], seg1 = ies[o + 3];
                                wideOpSeen = true;
                                width = cw6 switch { 0 => 20, 1 => 40, 2 => 80, _ => 160 };
                                center = cw6 == 0 ? ies[o]
                                    : cw6 == 3 && seg1 != 0 ? seg1
                                    : seg0;
                            }
                        }
                    }
                    break;
                }
                case 221 when len >= 4 &&
                              ies[val] == 0x00 && ies[val + 1] == 0x50 &&
                              ies[val + 2] == 0xF2 && ies[val + 3] == 0x04: // WPS
                {
                    wps ??= "1.0"; // WPS present even if no version attribute found
                    int tlv = val + 4, tlvEnd = val + len;
                    while (tlv + 4 <= tlvEnd)
                    {
                        int type = (ies[tlv] << 8) | ies[tlv + 1];
                        int tlen = (ies[tlv + 2] << 8) | ies[tlv + 3];
                        int tval = tlv + 4;
                        if (tval + tlen > tlvEnd) break;

                        if (type == 0x104A && tlen >= 1 && !wpsV2Found)
                        {
                            byte v = ies[tval];
                            wps = $"{v >> 4}.{v & 0xF}";
                        }
                        else if (type == 0x1049 && tlen >= 5 &&
                                 ies[tval] == 0x00 && ies[tval + 1] == 0x37 && ies[tval + 2] == 0x2A)
                        {
                            int sub = tval + 3, subEnd = tval + tlen;
                            while (sub + 2 <= subEnd)
                            {
                                byte sid = ies[sub], slen = ies[sub + 1];
                                if (sub + 2 + slen > subEnd) break;
                                if (sid == 0x00 && slen >= 1)
                                {
                                    byte v2 = ies[sub + 2];
                                    wps = $"{v2 >> 4}.{v2 & 0xF}";
                                    wpsV2Found = true;
                                    break;
                                }
                                sub += 2 + slen;
                            }
                        }
                        tlv += 4 + tlen;
                    }
                    break;
                }
            }
            pos += 2 + len;
        }

        // Top PHY rate from the newest advertised generation at the operating
        // width. EHT reuses HE numerology (same symbol time and tone counts),
        // adding MCS 12/13 (4096-QAM).
        bool shortGi = width switch
        {
            >= 160 => vhtSgi160,
            >= 80 => vhtSgi80,
            >= 40 => htSgi40,
            _ => htSgi20,
        };
        double maxRate = 0;
        if (ehtMaxNss > 0)
            maxRate = PhyRates.DataRateMbps(ehtMaxMcs, ehtMaxNss, width, highEfficiency: true, shortGi: false);
        else if (heMaxNss > 0)
            maxRate = PhyRates.DataRateMbps(heMaxMcs, heMaxNss, width, highEfficiency: true, shortGi: false);
        else if (vhtMaxNss > 0)
            maxRate = PhyRates.DataRateMbps(vhtMaxMcs, vhtMaxNss, width, highEfficiency: false, shortGi);
        else if (htMaxMcsIndex >= 0)
            maxRate = PhyRates.DataRateMbps(htMaxMcsIndex % 8, htMaxMcsIndex / 8 + 1,
                Math.Min(width, 40), highEfficiency: false, shortGi);

        return new BeaconInfo(wps, width, center, ht, vht, he, eht, maxRate);
    }

    /// <summary>Highest spatial-stream count an MCS map supports, with its MCS
    /// ceiling. Both VHT and HE maps pack 2 bits per stream (1..8); the value
    /// selects an MCS ceiling, 3 = stream count not supported.</summary>
    private static (int Nss, int Mcs) MaxFromMcsMap(int map, int mcs0, int mcs1, int mcs2)
    {
        int bestNss = 0, bestMcs = 0;
        for (int nss = 1; nss <= 8; nss++)
        {
            int v = (map >> ((nss - 1) * 2)) & 3;
            if (v == 3) continue;
            bestNss = nss;
            bestMcs = v switch { 0 => mcs0, 1 => mcs1, _ => mcs2 };
        }
        return (bestNss, bestMcs);
    }
}
