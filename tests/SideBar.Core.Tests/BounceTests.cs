using SideBar.Core.Motion;

namespace SideBar.Core.Tests;

public class BounceTests
{
    static double Riseend(int hop) => Bounce.HopStart(hop) + Bounce.HopLength(hop) * Bounce.RiseShare;

    [Fact]
    public void Starts_and_ends_at_zero()
    {
        Assert.Equal(0, Bounce.OffsetFraction(0));
        Assert.Equal(0, Bounce.OffsetFraction(Bounce.Duration.TotalSeconds));
        Assert.Equal(0, Bounce.OffsetFraction(Bounce.Duration.TotalSeconds + 1));
        Assert.Equal(0, Bounce.OffsetFraction(-1));
    }

    [Theory]
    [InlineData(0, 1.4)]
    [InlineData(1, 0.75)]
    [InlineData(2, 0.35)]
    public void Hops_peak_at_the_expected_height(int hop, double expected)
    {
        Assert.Equal(expected, Bounce.OffsetFraction(Riseend(hop)), 2);
        double max = 0;
        for (double t = Bounce.HopStart(hop); t < Bounce.HopStart(hop) + Bounce.HopLength(hop); t += 0.001)
            max = Math.Max(max, Bounce.OffsetFraction(t));
        Assert.InRange(max, expected * 0.98, expected * 1.02);
    }

    [Fact]
    public void Each_hop_rises_then_falls_monotonically()
    {
        for (int hop = 0; hop < 3; hop++)
        {
            double start = Bounce.HopStart(hop), peak = Riseend(hop), end = start + Bounce.HopLength(hop);
            double prev = Bounce.OffsetFraction(start);
            for (double t = start; t <= peak; t += 0.002)
            {
                var v = Bounce.OffsetFraction(t);
                Assert.True(v >= prev - 1e-9);
                prev = v;
            }
            prev = Bounce.OffsetFraction(peak);
            for (double t = peak; t <= end - 0.0001; t += 0.002)
            {
                var v = Bounce.OffsetFraction(t);
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
            Assert.InRange(Bounce.OffsetFraction(t - 1e-6), 0, 0.01);
            Assert.InRange(Bounce.OffsetFraction(t + 1e-6), 0, 0.01);
        }
    }

    [Fact]
    public void Never_negative_and_hops_get_lower()
    {
        for (double t = 0; t < 1.4; t += 0.003) Assert.True(Bounce.OffsetFraction(t) >= 0);
        Assert.True(Bounce.Peaks[0] > Bounce.Peaks[1] && Bounce.Peaks[1] > Bounce.Peaks[2]);
    }
}
