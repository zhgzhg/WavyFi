namespace WavyFi.Wlan;

/// <summary>
/// 802.11 PHY data-rate arithmetic: rate = data bits per subcarrier-symbol
/// × data subcarriers × spatial streams ÷ symbol duration.
/// </summary>
public static class PhyRates
{
    /// <summary>Data bits carried per subcarrier per symbol for MCS 0-13
    /// (modulation order × coding rate), BPSK 1/2 through 4096-QAM 5/6.</summary>
    private static readonly double[] McsDataBits =
        { 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 4.5, 5.0, 6.0, 20.0 / 3.0, 7.5, 25.0 / 3.0, 9.0, 10.0 };

    /// <summary>PHY rate in Mbps. HE/EHT use 12.8 µs symbols (+0.8 µs GI) and
    /// ~4× the subcarriers of HT/VHT (3.2 µs symbols + 0.8/0.4 µs GI).</summary>
    public static double DataRateMbps(int mcs, int spatialStreams, int widthMhz, bool highEfficiency, bool shortGi)
    {
        if (mcs < 0 || mcs >= McsDataBits.Length || spatialStreams <= 0) return 0;
        int subcarriers = highEfficiency
            ? widthMhz switch { 40 => 468, 80 => 980, 160 => 1960, 320 => 3920, _ => 234 }
            : widthMhz switch { 40 => 108, 80 => 234, 160 => 468, _ => 52 };
        double symbolMicroseconds = highEfficiency ? 13.6 : shortGi ? 3.6 : 4.0;
        return McsDataBits[mcs] * subcarriers * spatialStreams / symbolMicroseconds;
    }
}
