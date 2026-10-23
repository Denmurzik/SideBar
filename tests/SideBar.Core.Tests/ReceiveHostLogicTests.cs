using System.Net.Sockets;
using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class AcceptFailureTests
{
    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.TooManyOpenSockets)]
    [InlineData(SocketError.ConnectionAborted)]
    public void Failed_accept_of_one_connection_keeps_the_loop_going(SocketError code)
        => Assert.False(AcceptFailure.ListenerStopped(new SocketException((int)code)));

    [Theory]
    [InlineData(SocketError.OperationAborted)]
    [InlineData(SocketError.Interrupted)]
    public void Aborted_accept_means_the_listener_was_stopped(SocketError code)
        => Assert.True(AcceptFailure.ListenerStopped(new SocketException((int)code)));

    [Fact]
    public void Disposed_or_cancelled_means_stopped()
    {
        Assert.True(AcceptFailure.ListenerStopped(new ObjectDisposedException("x")));
        Assert.True(AcceptFailure.ListenerStopped(new OperationCanceledException()));
    }

    [Fact]
    public void Unknown_exception_is_retried()
        => Assert.False(AcceptFailure.ListenerStopped(new IOException()));
}

public class NetworkCategoryTrackerTests
{
    [Fact]
    public void Good_lookups_pass_through()
    {
        var t = new NetworkCategoryTracker();
        Assert.Equal(NetworkCategory.Private, t.Observe(NetworkCategory.Private));
        Assert.Equal(NetworkCategory.Public, t.Observe(NetworkCategory.Public));
    }

    [Fact]
    public void Real_unknown_is_unknown_immediately()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Private);
        Assert.Equal(NetworkCategory.Unknown, t.Observe(NetworkCategory.Unknown));
    }

    [Fact]
    public void One_failed_lookup_keeps_the_last_good_category()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Domain);
        Assert.Equal(NetworkCategory.Domain, t.Observe(null));
    }

    [Fact]
    public void Two_consecutive_failures_become_unknown()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Private);
        t.Observe(null);
        Assert.Equal(NetworkCategory.Unknown, t.Observe(null));
    }

    [Fact]
    public void A_good_lookup_resets_the_failure_count()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Private);
        t.Observe(null);
        t.Observe(NetworkCategory.Private);
        Assert.Equal(NetworkCategory.Private, t.Observe(null));
    }

    [Fact]
    public void Failure_without_history_is_unknown_and_reset_forgets_history()
    {
        var t = new NetworkCategoryTracker();
        Assert.Equal(NetworkCategory.Unknown, t.Observe(null));
        t.Observe(NetworkCategory.Private);
        t.Reset();
        Assert.Equal(NetworkCategory.Unknown, t.Observe(null));
    }
}

public class ReceiveBlockReasonTests
{
    [Fact]
    public void Hidden_never_explains()
        => Assert.Equal(ReceiveBlock.None, ReceiveBlockReason.Of(Visibility.Hidden, true, NetworkCategory.Public));

    [Fact]
    public void Everyone_on_private_listens_so_no_note()
        => Assert.Equal(ReceiveBlock.None, ReceiveBlockReason.Of(Visibility.Everyone, true, NetworkCategory.Private));

    [Theory]
    [InlineData(NetworkCategory.Public)]
    [InlineData(NetworkCategory.Unknown)]
    public void Everyone_on_public_or_unknown_is_public_network(NetworkCategory c)
        => Assert.Equal(ReceiveBlock.PublicNetwork, ReceiveBlockReason.Of(Visibility.Everyone, true, c));

    [Fact]
    public void Everyone_without_route_is_no_lan()
        => Assert.Equal(ReceiveBlock.NoLan, ReceiveBlockReason.Of(Visibility.Everyone, false, null));
}
