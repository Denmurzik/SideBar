using SideBar.Core.Motion;

namespace SideBar.Core.Tests;

public class BounceTests
{
    static double Riseend(int hop) => Bounce.HopStart(hop) + Bounce.HopLength(hop) * Bounce.RiseShare;

    [Fact]
    public void Starts_and_ends_at_zero()
    {
        Assert.Equal(0, Bounce.OffsetEpx(0));
        Assert.Equal(0, Bounce.OffsetEpx(Bounce.Duration.TotalSeconds));
        Assert.Equal(0, Bounce.OffsetEpx(Bounce.Duration.TotalSeconds + 1));
        Assert.Equal(0, Bounce.OffsetEpx(-1));
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(1, 22)]
    [InlineData(2, 10)]
    public void Hops_peak_at_the_expected_height(int hop, double expected)
    {
        Assert.Equal(expected, Bounce.OffsetEpx(Riseend(hop)), 2);
        double max = 0;
        for (double t = Bounce.HopStart(hop); t < Bounce.HopStart(hop) + Bounce.HopLength(hop); t += 0.001)
            max = Math.Max(max, Bounce.OffsetEpx(t));
        Assert.InRange(max, expected * 0.98, expected * 1.02);
    }

    [Fact]
    public void Each_hop_rises_then_falls_monotonically()
    {
        for (int hop = 0; hop < 3; hop++)
        {
            double start = Bounce.HopStart(hop), peak = Riseend(hop), end = start + Bounce.HopLength(hop);
            double prev = Bounce.OffsetEpx(start);
            for (double t = start; t <= peak; t += 0.002)
            {
                var v = Bounce.OffsetEpx(t);
                Assert.True(v >= prev - 1e-9);
                prev = v;
            }
            prev = Bounce.OffsetEpx(peak);
            for (double t = peak; t <= end - 0.0001; t += 0.002)
            {
                var v = Bounce.OffsetEpx(t);
                Assert.True(v <= prev + 1e-9);
                prev = v;
            }
        }
    }

    [Fact]
    public void Landings_are_continuous()
    {
        for (int hop = 1; hop < 3; hop++)
        {
            double t = Bounce.HopStart(hop);
            Assert.InRange(Bounce.OffsetEpx(t - 1e-6), 0, 0.3);
            Assert.InRange(Bounce.OffsetEpx(t + 1e-6), 0, 0.3);
        }
    }

    [Fact]
    public void Never_negative_and_hops_get_lower()
    {
        for (double t = 0; t < 1.4; t += 0.003) Assert.True(Bounce.OffsetEpx(t) >= 0);
        Assert.True(Bounce.Peaks[0] > Bounce.Peaks[1] && Bounce.Peaks[1] > Bounce.Peaks[2]);
    }
}
