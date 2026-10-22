using SideBar.Core.Motion;

namespace SideBar.Core.Tests;

public class SpringTests
{
    [Fact]
    public void Settles_on_target_within_1s()
    {
        var s = new Spring();
        s.Reset(1920);
        s.Retarget(1696, 0);
        s.Step(1.0);
        Assert.True(s.IsSettled);
        Assert.InRange(s.Position, 1696 - 0.5, 1696 + 0.5);
    }

    [Fact]
    public void Retarget_keeps_position_and_velocity()
    {
        var s = new Spring();
        s.Reset(1920);
        s.Retarget(1696, 0);
        s.Step(0.1);
        double p = s.Position, v = s.Velocity;
        s.Retarget(1920, 0.1);
        Assert.Equal(p, s.Position, 9);
        Assert.Equal(v, s.Velocity, 9);
        s.Step(0.1);
        Assert.Equal(p, s.Position, 9);
        Assert.Equal(v, s.Velocity, 9);
    }

    [Fact]
    public void Underdamped_overshoots_slightly()
    {
        var s = new Spring();
        s.Reset(1920);
        s.Retarget(1696, 0);
        double min = double.MaxValue;
        for (double t = 0; t <= 1.5; t += 0.001)
        {
            s.Step(t);
            min = Math.Min(min, s.Position);
        }
        Assert.True(min < 1696);
        Assert.True(min > 1696 - 15);
    }
}
