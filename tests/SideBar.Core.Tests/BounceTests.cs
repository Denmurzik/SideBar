using SideBar.Core.Nearby;
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

public class ConsentArmingTests
{
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_armed_before_the_rows_were_placed()
        => Assert.False(new ConsentArming().CanAccept(T0, true));

    [Fact]
    public void A_press_counts_only_after_350ms_from_the_last_placement()
    {
        var a = new ConsentArming();
        a.Placed(T0);
        Assert.False(a.CanAccept(T0.AddMilliseconds(349), true));
        Assert.True(a.CanAccept(T0.AddMilliseconds(350), true));
        a.Placed(T0.AddSeconds(5));   // next consent slid under the cursor
        Assert.False(a.CanAccept(T0.AddSeconds(5.2), true));
        Assert.True(a.CanAccept(T0.AddSeconds(5.4), true));
    }

    [Fact]
    public void A_press_never_counts_while_the_card_is_not_settled_open()
    {
        var a = new ConsentArming();
        a.Placed(T0);
        Assert.False(a.CanAccept(T0.AddMinutes(1), false));
    }
}

public class AttentionPulseTests
{
    [Fact]
    public void Starts_and_ends_at_zero_with_two_peaks()
    {
        Assert.Equal(0, AttentionPulse.Opacity(0));
        Assert.Equal(0, AttentionPulse.Opacity(AttentionPulse.Duration.TotalSeconds));
        double total = AttentionPulse.Duration.TotalSeconds;
        Assert.Equal(AttentionPulse.Peak, AttentionPulse.Opacity(total * 0.25), 3);
        Assert.Equal(AttentionPulse.Peak, AttentionPulse.Opacity(total * 0.75), 3);
        Assert.InRange(AttentionPulse.Opacity(total * 0.5), 0, 0.01);
    }
}
