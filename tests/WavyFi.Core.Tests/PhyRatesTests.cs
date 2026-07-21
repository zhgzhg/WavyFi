using WavyFi.Wlan;

namespace WavyFi.Core.Tests;

public class PhyRatesTests
{
    [Theory]
    // HT/VHT numerology (3.2 µs symbols + 0.8/0.4 µs GI)
    [InlineData(7, 1, 20, false, false, 65.0)]     // classic 802.11n baseline
    [InlineData(7, 1, 20, false, true, 72.2)]      // ... with short GI
    [InlineData(7, 2, 40, false, true, 300.0)]     // 2x2 N router
    [InlineData(9, 2, 80, false, true, 866.7)]     // 2x2 AC wave2
    [InlineData(9, 4, 80, false, true, 1733.3)]    // 4x4 AC
    // HE/EHT numerology (12.8 µs symbols + 0.8 µs GI)
    [InlineData(11, 2, 80, true, false, 1201.0)]   // 2x2 AX
    [InlineData(11, 2, 160, true, false, 2402.0)]  // 2x2 AX 160
    [InlineData(13, 2, 160, true, false, 2882.4)]  // 2x2 BE 160 (4096-QAM)
    [InlineData(13, 2, 320, true, false, 5764.7)]  // 2x2 BE 320
    public void KnownRatePoints(int mcs, int nss, int width, bool he, bool sgi, double expected)
    {
        Assert.Equal(expected, PhyRates.DataRateMbps(mcs, nss, width, he, sgi), 1);
    }

    [Theory]
    [InlineData(-1, 1, 80, false, false)]
    [InlineData(14, 1, 80, false, false)]
    [InlineData(7, 0, 80, false, false)]
    public void InvalidInputs_YieldZero(int mcs, int nss, int width, bool he, bool sgi)
    {
        Assert.Equal(0, PhyRates.DataRateMbps(mcs, nss, width, he, sgi));
    }
}
