using SideBar.Core;
using SideBar.Core.Edge;
using SideBar.Core.Layout;

namespace SideBar.Core.Tests;

public class EdgeStateMachineTests
{
    static readonly PxRect Monitor = new(0, 0, 1920, 1080);

    static EdgeStateMachine Create(EdgeMode mode = EdgeMode.Slide) => new(new CardLayout(Monitor, 1.0), mode);

    static EdgeInput At(int x, int y, long t, bool left = false, bool fs = false) => new(x, y, left, fs, t);

    static EdgeStateMachine Opened()
    {
        var m = Create();
        Assert.Equal(PanelState.Open, m.Update(At(1919, 540, 0)));
        return m;
    }

    [Fact]
    public void Hidden_to_Peek_in_band()
    {
        var m = Create();
        Assert.Equal(PanelState.Peek, m.Update(At(1890, 540, 0)));
        Assert.Equal(PanelState.Peek, m.State);
    }

    [Fact]
    public void Touch_edge_opens_from_Hidden_or_Peek()
    {
        var m = Create();
        Assert.Equal(PanelState.Open, m.Update(At(1919, 540, 0)));

        var p = Create();
        p.Update(At(1890, 540, 0));
        Assert.Equal(PanelState.Open, p.Update(At(1919, 540, 10)));
    }

    [Fact]
    public void Outside_card_band_does_nothing()
    {
        var m = Create();
        Assert.Null(m.Update(At(1919, 100, 0)));
        Assert.Equal(PanelState.Hidden, m.State);
    }

    [Fact]
    public void Peek_back_to_Hidden_when_leaving()
    {
        var m = Create();
        m.Update(At(1890, 540, 0));
        Assert.Equal(PanelState.Hidden, m.Update(At(1700, 540, 10)));
    }

    [Fact]
    public void Open_hides_300ms_after_leaving_card()
    {
        var m = Opened();
        Assert.Null(m.Update(At(1000, 540, 1000)));
        Assert.Null(m.Update(At(1000, 540, 1299)));
        Assert.Equal(PanelState.Hidden, m.Update(At(1000, 540, 1300)));
    }

    [Fact]
    public void Open_stays_while_left_button_held()
    {
        var m = Opened();
        Assert.Null(m.Update(At(1000, 540, 2000, left: true)));
        Assert.Equal(PanelState.Open, m.State);
    }

    [Fact]
    public void Returning_to_card_cancels_hide()
    {
        var m = Opened();
        Assert.Null(m.Update(At(1000, 540, 1000)));
        Assert.Null(m.Update(At(1800, 540, 1200)));
        Assert.Null(m.Update(At(1000, 540, 1250)));
        Assert.Null(m.Update(At(1000, 540, 1549)));
        Assert.Equal(PanelState.Hidden, m.Update(At(1000, 540, 1550)));
    }

    [Fact]
    public void Leaving_through_card_vertical_extent_counts_as_outside()
    {
        var m = Opened();
        Assert.Null(m.Update(At(1800, 100, 1000)));
        Assert.Equal(PanelState.Hidden, m.Update(At(1800, 100, 1300)));
    }

    [Fact]
    public void Fullscreen_suppresses_opening()
    {
        var m = Create();
        Assert.Null(m.Update(At(1919, 540, 0, fs: true)));
        Assert.Null(m.Update(At(1890, 540, 10, fs: true)));
        Assert.Equal(PanelState.Hidden, m.State);
    }

    [Fact]
    public void Fullscreen_allows_opening_during_drag()
    {
        var m = Create();
        Assert.Equal(PanelState.Open, m.Update(At(1919, 540, 0, left: true, fs: true)));
    }

    [Fact]
    public void Fullscreen_does_not_close_open_panel()
    {
        var m = Opened();
        Assert.Null(m.Update(At(1800, 540, 10, fs: true)));
        Assert.Equal(PanelState.Open, m.State);
    }

    [Fact]
    public void InPlace_opens_after_dwell_without_touch()
    {
        var m = Create(EdgeMode.InPlace);
        Assert.Null(m.Update(At(1890, 540, 0)));
        Assert.Null(m.Update(At(1890, 540, 149)));
        Assert.Equal(PanelState.Hidden, m.State);
        Assert.Equal(PanelState.Open, m.Update(At(1890, 540, 150)));
    }

    [Fact]
    public void InPlace_never_peeks_and_touch_still_needs_dwell()
    {
        var m = Create(EdgeMode.InPlace);
        Assert.Null(m.Update(At(1919, 540, 0)));
        Assert.Equal(PanelState.Hidden, m.State);
    }

    [Fact]
    public void InPlace_dwell_resets_on_leaving_band()
    {
        var m = Create(EdgeMode.InPlace);
        Assert.Null(m.Update(At(1890, 540, 0)));
        Assert.Null(m.Update(At(1000, 540, 100)));
        Assert.Null(m.Update(At(1890, 540, 200)));
        Assert.Null(m.Update(At(1890, 540, 349)));
        Assert.Equal(PanelState.Open, m.Update(At(1890, 540, 350)));
    }

    [Fact]
    public void InPlace_close_works_like_Slide()
    {
        var m = Create(EdgeMode.InPlace);
        m.Update(At(1890, 540, 0));
        m.Update(At(1890, 540, 150));
        Assert.Null(m.Update(At(1000, 540, 1000)));
        Assert.Equal(PanelState.Hidden, m.Update(At(1000, 540, 1300)));
    }

    [Fact]
    public void Relayout_resets_to_Hidden_and_clears_timers()
    {
        var m = Opened();
        m.Update(At(1000, 540, 1000));
        m.Relayout(new CardLayout(Monitor, 1.0), EdgeMode.Slide);
        Assert.Equal(PanelState.Hidden, m.State);
        Assert.Equal(PanelState.Open, m.Update(At(1919, 540, 1100)));
        Assert.Null(m.Update(At(1000, 540, 1200)));
        Assert.Null(m.Update(At(1000, 540, 1499)));
    }

    static EdgeStateMachine OpenedInPlace()
    {
        var m = Create(EdgeMode.InPlace);
        m.Update(At(1890, 540, 0));
        Assert.Equal(PanelState.Open, m.Update(At(1890, 540, 150)));
        return m;
    }

    [Fact]
    public void InPlace_cursor_on_right_neighbor_does_not_keep_card_open()
    {
        var m = OpenedInPlace();
        Assert.Null(m.Update(At(2500, 540, 1000)));
        Assert.Equal(PanelState.Hidden, m.Update(At(2500, 540, 1300)));
    }

    [Fact]
    public void InPlace_parked_in_band_outside_card_does_not_loop()
    {
        var m = OpenedInPlace();
        Assert.Null(m.Update(At(1900, 285, 1000)));
        Assert.Equal(PanelState.Hidden, m.Update(At(1900, 285, 1300)));
        for (long t = 1400; t <= 2400; t += 100) Assert.Null(m.Update(At(1900, 285, t)));
        Assert.Equal(PanelState.Hidden, m.State);

        Assert.Null(m.Update(At(1000, 540, 2500)));
        Assert.Null(m.Update(At(1900, 285, 2600)));
        Assert.Null(m.Update(At(1900, 285, 2749)));
        Assert.Equal(PanelState.Open, m.Update(At(1900, 285, 2750)));
    }
}
