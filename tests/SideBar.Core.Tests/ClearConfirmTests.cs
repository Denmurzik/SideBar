using SideBar.Core.Shelf;

namespace SideBar.Core.Tests;

public class ClearConfirmTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_press_arms()
    {
        var c = new ClearConfirm();
        Assert.False(c.IsArmed(T0));
        Assert.Equal(ClearPress.Armed, c.Press(T0));
        Assert.True(c.IsArmed(T0));
    }

    [Fact]
    public void Second_press_within_window_clears_and_disarms()
    {
        var c = new ClearConfirm();
        c.Press(T0);
        Assert.Equal(ClearPress.Cleared, c.Press(T0 + ClearConfirm.Window));
        Assert.False(c.IsArmed(T0 + ClearConfirm.Window));
        Assert.Equal(ClearPress.Armed, c.Press(T0 + ClearConfirm.Window));
    }

    [Fact]
    public void Second_press_after_window_only_rearms()
    {
        var c = new ClearConfirm();
        c.Press(T0);
        var late = T0 + ClearConfirm.Window + TimeSpan.FromMilliseconds(1);
        Assert.False(c.IsArmed(late));
        Assert.Equal(ClearPress.Armed, c.Press(late));
        Assert.Equal(ClearPress.Cleared, c.Press(late + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Reset_disarms()
    {
        var c = new ClearConfirm();
        c.Press(T0);
        c.Reset();
        Assert.Equal(ClearPress.Armed, c.Press(T0));
    }

    [Fact]
    public void Window_is_three_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), ClearConfirm.Window);
    }
}
