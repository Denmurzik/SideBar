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
    public void Backoff_doubles_from_100ms_to_a_2s_cap()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), AcceptFailure.Backoff(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), AcceptFailure.Backoff(2));
        Assert.Equal(TimeSpan.FromMilliseconds(400), AcceptFailure.Backoff(3));
        Assert.Equal(TimeSpan.FromSeconds(2), AcceptFailure.Backoff(6));
        Assert.Equal(TimeSpan.FromSeconds(2), AcceptFailure.Backoff(1000));
    }

    [Fact]
    public void Unknown_exception_is_retried()
        => Assert.False(AcceptFailure.ListenerStopped(new IOException()));
}

public class NetworkCategoryTrackerTests
{
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Good_lookups_pass_through()
    {
        var t = new NetworkCategoryTracker();
        Assert.Equal(NetworkCategory.Private, t.Observe(NetworkCategory.Private, T0));
        Assert.Equal(NetworkCategory.Public, t.Observe(NetworkCategory.Public, T0));
    }

    [Fact]
    public void Real_unknown_is_unknown_immediately()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Private, T0);
        Assert.Equal(NetworkCategory.Unknown, t.Observe(NetworkCategory.Unknown, T0));
    }

    [Fact]
    public void Failed_lookups_keep_the_last_good_category_for_a_while()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Domain, T0);
        Assert.Equal(NetworkCategory.Domain, t.Observe(null, T0.AddSeconds(1)));
        Assert.Equal(NetworkCategory.Domain, t.Observe(null, T0.AddSeconds(20)));
    }

    [Fact]
    public void A_burst_of_failures_within_a_second_never_cancels()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Private, T0);
        for (int i = 0; i < 20; i++)
            Assert.Equal(NetworkCategory.Private, t.Observe(null, T0.AddMilliseconds(i * 50)));
    }

    [Fact]
    public void Failures_spanning_thirty_seconds_become_unknown()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Private, T0);
        t.Observe(null, T0.AddSeconds(1));
        Assert.Equal(NetworkCategory.Private, t.Observe(null, T0.AddSeconds(30.9)));
        Assert.Equal(NetworkCategory.Unknown, t.Observe(null, T0.AddSeconds(31)));
    }

    [Fact]
    public void A_good_lookup_restarts_the_failure_clock()
    {
        var t = new NetworkCategoryTracker();
        t.Observe(NetworkCategory.Private, T0);
        t.Observe(null, T0.AddSeconds(1));
        t.Observe(NetworkCategory.Private, T0.AddSeconds(20));
        Assert.Equal(NetworkCategory.Private, t.Observe(null, T0.AddSeconds(40)));
    }

    [Fact]
    public void Failure_without_history_is_unknown_and_reset_forgets_history()
    {
        var t = new NetworkCategoryTracker();
        Assert.Equal(NetworkCategory.Unknown, t.Observe(null, T0));
        t.Observe(NetworkCategory.Private, T0);
        t.Reset();
        Assert.Equal(NetworkCategory.Unknown, t.Observe(null, T0));
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
