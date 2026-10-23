using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class VisibilityModelTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_is_hidden()
    {
        var m = new VisibilityModel();
        Assert.Equal(Visibility.Hidden, m.Current);
        Assert.Null(m.EveryoneUntil);
    }

    [Fact]
    public void Disable_returns_to_hidden_and_notifies_once()
    {
        var m = new VisibilityModel();
        var changes = 0;
        m.Changed += () => changes++;

        m.Disable();
        Assert.Equal(0, changes);

        m.EnableEveryone(T0);
        m.Disable();
        Assert.Equal(Visibility.Hidden, m.Current);
        Assert.Null(m.EveryoneUntil);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Everyone_expires_after_10_minutes()
    {
        var m = new VisibilityModel();
        var changes = 0;
        m.Changed += () => changes++;

        m.EnableEveryone(T0);
        Assert.Equal(Visibility.Everyone, m.Current);
        Assert.Equal(T0.AddMinutes(10), m.EveryoneUntil);

        m.Tick(T0.AddMinutes(10).AddTicks(-1));
        Assert.Equal(Visibility.Everyone, m.Current);

        m.Tick(T0.AddMinutes(10));
        Assert.Equal(Visibility.Hidden, m.Current);
        Assert.Null(m.EveryoneUntil);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Public_network_never_listens()
    {
        var m = new VisibilityModel();
        m.EnableEveryone(T0);
        Assert.False(m.ShouldListen(NetworkCategory.Public));
    }

    [Fact]
    public void Unknown_network_never_listens()
    {
        var m = new VisibilityModel();
        m.EnableEveryone(T0);
        Assert.False(m.ShouldListen(NetworkCategory.Unknown));
    }

    [Theory]
    [InlineData(NetworkCategory.Private)]
    [InlineData(NetworkCategory.Domain)]
    public void Private_and_domain_listen_only_when_everyone(NetworkCategory category)
    {
        var m = new VisibilityModel();
        Assert.False(m.ShouldListen(category));
        m.EnableEveryone(T0);
        Assert.True(m.ShouldListen(category));
        m.Tick(T0.AddMinutes(11));
        Assert.False(m.ShouldListen(category));
    }
}
