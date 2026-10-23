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

    [Fact]
    public void Visibility_default_is_own_devices_when_certs_available()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        Assert.Equal(Visibility.OwnDevices, m.Current);
        Assert.True(m.RejectsUnverified);
    }

    [Fact]
    public void Saved_hidden_is_respected_even_when_certs_are_available()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true, saved: Visibility.Hidden);
        Assert.Equal(Visibility.Hidden, m.Current);
    }

    [Fact]
    public void Own_devices_unavailable_falls_back_to_hidden()
    {
        var m = new VisibilityModel(ownDevicesAvailable: false);
        Assert.Equal(Visibility.Hidden, m.Current);
        Assert.True(m.OwnDevicesDegraded);
        Assert.False(m.Select(Visibility.OwnDevices, T0));
        Assert.Equal(Visibility.Hidden, m.Current);

        var saved = new VisibilityModel(ownDevicesAvailable: false, saved: Visibility.OwnDevices);
        Assert.Equal(Visibility.Hidden, saved.Current);
    }

    [Fact]
    public void Own_devices_lost_at_runtime_degrades_and_comes_back_when_certs_return()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        var changes = 0;
        m.Changed += () => changes++;

        m.SetOwnDevicesAvailable(false);
        Assert.Equal(Visibility.Hidden, m.Current);
        Assert.True(m.OwnDevicesDegraded);

        m.SetOwnDevicesAvailable(true);
        Assert.Equal(Visibility.OwnDevices, m.Current);
        Assert.False(m.OwnDevicesDegraded);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void An_explicit_hidden_choice_survives_certs_appearing()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        m.Select(Visibility.Hidden, T0);
        m.SetOwnDevicesAvailable(false);
        m.SetOwnDevicesAvailable(true);
        Assert.Equal(Visibility.Hidden, m.Current);
        Assert.False(m.OwnDevicesDegraded);
    }

    [Theory]
    [InlineData(Visibility.OwnDevices)]
    [InlineData(Visibility.Hidden)]
    public void Everyone_returns_to_previous_after_10_min(Visibility previous)
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        m.Select(previous, T0);

        m.Select(Visibility.Everyone, T0);
        Assert.Equal(Visibility.Everyone, m.Current);
        Assert.False(m.RejectsUnverified);
        Assert.Equal(previous, m.Persisted);   // Everyone is never persisted

        m.Tick(T0.AddMinutes(10).AddTicks(-1));
        Assert.Equal(Visibility.Everyone, m.Current);
        m.Tick(T0.AddMinutes(10));
        Assert.Equal(previous, m.Current);
        Assert.Null(m.EveryoneUntil);
    }

    [Fact]
    public void Choosing_another_mode_leaves_everyone_immediately()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        m.Select(Visibility.Everyone, T0);
        m.Select(Visibility.Hidden, T0.AddMinutes(1));
        Assert.Equal(Visibility.Hidden, m.Current);
        m.Tick(T0.AddMinutes(20));
        Assert.Equal(Visibility.Hidden, m.Current);
    }

    [Fact]
    public void Reselecting_everyone_does_not_extend_the_window()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        m.Select(Visibility.Everyone, T0);
        m.Select(Visibility.Everyone, T0.AddMinutes(5));
        Assert.Equal(T0.AddMinutes(10), m.EveryoneUntil);
    }

    [Fact]
    public void Own_devices_listens_only_on_private_networks()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        Assert.True(m.ShouldListen(NetworkCategory.Private));
        Assert.False(m.ShouldListen(NetworkCategory.Public));
        Assert.False(m.ShouldListen(NetworkCategory.Unknown));
    }

    [Fact]
    public void Certificates_are_used_in_own_devices_and_everyone_but_only_own_devices_rejects_unverified()
    {
        var m = new VisibilityModel(ownDevicesAvailable: true);
        Assert.True(m.UsesCertificates);
        Assert.True(m.RejectsUnverified);
        Assert.False(m.AdvertisesName);

        m.Select(Visibility.Everyone, T0);
        Assert.True(m.UsesCertificates);
        Assert.False(m.RejectsUnverified);
        Assert.True(m.AdvertisesName);

        m.Select(Visibility.Hidden, T0);
        Assert.False(m.UsesCertificates);
    }

    [Fact]
    public void Everyone_without_certificates_is_anonymous_and_asks()
    {
        var m = new VisibilityModel(ownDevicesAvailable: false);
        m.Select(Visibility.Everyone, T0);
        Assert.False(m.UsesCertificates);
        Assert.False(m.RejectsUnverified);
        Assert.True(m.AdvertisesName);
    }
}
