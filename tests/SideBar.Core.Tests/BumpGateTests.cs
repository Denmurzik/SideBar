using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class BumpGateTests
{
    [Fact]
    public void First_bump_is_allowed_even_at_time_zero()
    {
        Assert.True(new BumpGate().TryBump(0));
    }

    [Fact]
    public void Bumps_closer_than_3_seconds_are_suppressed_and_do_not_extend_the_window()
    {
        var g = new BumpGate();
        Assert.True(g.TryBump(10_000));
        Assert.False(g.TryBump(11_000));
        Assert.False(g.TryBump(12_999));
        Assert.True(g.TryBump(13_000));
        Assert.False(g.TryBump(14_000));
    }
}
