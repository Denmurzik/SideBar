using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class ReceivePolicyTests
{
    [Fact]
    public void Everyone_on_private_network_listens_and_cancels_nothing()
    {
        var d = ReceivePolicy.Decide(Visibility.Everyone, NetworkCategory.Private);
        Assert.True(d.Listen);
        Assert.False(d.CancelUnaccepted);
        Assert.False(d.CancelAccepted);
    }

    [Fact]
    public void Hidden_on_private_network_cancels_only_unaccepted_sessions()
    {
        var d = ReceivePolicy.Decide(Visibility.Hidden, NetworkCategory.Domain);
        Assert.False(d.Listen);
        Assert.True(d.CancelUnaccepted);
        Assert.False(d.CancelAccepted);
    }

    [Theory]
    [InlineData(Visibility.Hidden, NetworkCategory.Public)]
    [InlineData(Visibility.Hidden, NetworkCategory.Unknown)]
    [InlineData(Visibility.Everyone, NetworkCategory.Public)]
    [InlineData(Visibility.Everyone, NetworkCategory.Unknown)]
    public void Public_or_unknown_network_cancels_everything_whatever_the_visibility(Visibility v, NetworkCategory c)
    {
        var d = ReceivePolicy.Decide(v, c);
        Assert.False(d.Listen);
        Assert.True(d.CancelUnaccepted);
        Assert.True(d.CancelAccepted);
    }

    [Theory]
    [InlineData(Visibility.Hidden)]
    [InlineData(Visibility.Everyone)]
    public void No_route_counts_as_a_bad_network(Visibility v)
    {
        var d = ReceivePolicy.Decide(v, null);
        Assert.False(d.Listen);
        Assert.True(d.CancelAccepted);
    }

    [Fact]
    public void Unchecked_network_never_cancels_accepted_transfers()
    {
        var d = ReceivePolicy.Decide(Visibility.Hidden, null, networkChecked: false);
        Assert.False(d.Listen);
        Assert.True(d.CancelUnaccepted);
        Assert.False(d.CancelAccepted);
    }

    [Fact]
    public void Own_devices_on_private_network_listens_and_on_public_cancels_everything()
    {
        Assert.True(ReceivePolicy.Decide(Visibility.OwnDevices, NetworkCategory.Private).Listen);
        var d = ReceivePolicy.Decide(Visibility.OwnDevices, NetworkCategory.Public);
        Assert.False(d.Listen);
        Assert.True(d.CancelAccepted);
        Assert.Equal(ReceiveBlock.PublicNetwork, ReceiveBlockReason.Of(Visibility.OwnDevices, true, NetworkCategory.Public));
        Assert.Equal(ReceiveBlock.None, ReceiveBlockReason.Of(Visibility.OwnDevices, true, NetworkCategory.Private));
    }
}
