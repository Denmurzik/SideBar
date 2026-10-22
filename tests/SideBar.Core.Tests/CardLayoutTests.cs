using SideBar.Core;
using SideBar.Core.Layout;

namespace SideBar.Core.Tests;

public class CardLayoutTests
{
    [Fact]
    public void Open_at_100pct_on_1920x1080()
    {
        var l = new CardLayout(new PxRect(0, 0, 1920, 1080), 1.0);
        Assert.Equal(216, l.CardWidthPx);
        Assert.Equal(469, l.CardHeightPx);
        Assert.Equal(1920 - 224, l.LeftFor(PanelState.Open));
        Assert.Equal(1910, l.LeftFor(PanelState.Peek));
        Assert.Equal(1920, l.LeftFor(PanelState.Hidden));
        Assert.Equal((1080 - 469) / 2, l.Top);
        Assert.Equal(38, l.PeekBandPx);
    }

    [Fact]
    public void Scales_at_125pct()
    {
        var l = new CardLayout(new PxRect(0, 0, 1920, 1080), 1.25);
        Assert.Equal(270, l.CardWidthPx);
        Assert.Equal(586, l.CardHeightPx);
        Assert.Equal(1920 - 280, l.LeftFor(PanelState.Open));
        Assert.Equal(1920 - 13, l.LeftFor(PanelState.Peek));
        Assert.Equal(47, l.PeekBandPx);
    }

    [Fact]
    public void Monitor_with_offset_origin()
    {
        var l = new CardLayout(new PxRect(-1920, 200, 0, 1280), 1.0);
        Assert.Equal(200 + (1080 - 469) / 2, l.Top);
        Assert.Equal(-224, l.LeftFor(PanelState.Open));
        Assert.Equal(0, l.LeftFor(PanelState.Hidden));
    }

    [Fact]
    public void ActivationBand_is_card_plus_one_cm()
    {
        var l = new CardLayout(new PxRect(0, 0, 1920, 1080), 1.0);
        var b = l.ActivationBand;
        Assert.Equal(l.Top - 38, b.Top);
        Assert.Equal(l.Top + 469 + 38, b.Bottom);
        Assert.Equal(1882, b.Left);
        Assert.Equal(1920, b.Right);
    }

    [Fact]
    public void CardRectAt_uses_card_size_and_top()
    {
        var l = new CardLayout(new PxRect(0, 0, 1920, 1080), 1.0);
        var r = l.CardRectAt(1696);
        Assert.Equal(new PxRect(1696, l.Top, 1696 + 216, l.Top + 469), r);
        Assert.Equal(l.Top + 469 / 2, l.CenterY);
    }
}
