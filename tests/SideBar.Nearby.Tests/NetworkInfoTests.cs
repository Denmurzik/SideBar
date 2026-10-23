using System.Net.NetworkInformation;
using SideBar.Core.Nearby;
using SideBar.Nearby.Discovery;
using Xunit;

namespace SideBar.Nearby.Tests;

public class NetworkInfoTests
{
    [Fact]
    public void Default_route_interface_is_consistent_or_null()
    {
        var route = NetworkInfo.DefaultRouteInterface();
        if (route is null) return;
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, route.Value.Address.AddressFamily);
        Assert.True(route.Value.InterfaceIndex > 0);
        Assert.True(Enum.IsDefined(NetworkInfo.CategoryOf(route.Value.InterfaceIndex)));
    }

    [Fact]
    public void Category_of_bogus_interface_is_unknown()
    {
        Assert.Equal(NetworkCategory.Unknown, NetworkInfo.CategoryOf(-1));
    }

    static Func<NetworkInfo.AdapterInfo> Fake(int index, string ip, bool gateway = true, NetworkInterfaceType type = NetworkInterfaceType.Ethernet) =>
        () => new(Guid.NewGuid().ToString("B"), System.Net.NetworkInformation.OperationalStatus.Up, type, index, System.Net.IPAddress.Parse(ip), gateway);

    static Func<NetworkInfo.AdapterInfo> Throwing() => () => throw new System.Net.NetworkInformation.NetworkInformationException();

    [Fact]
    public void Throwing_adapter_does_not_hide_the_others()
    {
        var route = NetworkInfo.DefaultRouteInterface([Throwing(), Fake(7, "192.168.1.5")], () => null);
        Assert.Equal(7, route!.Value.InterfaceIndex);
    }

    [Fact]
    public void Best_metric_interface_wins_over_first_with_gateway()
    {
        var route = NetworkInfo.DefaultRouteInterface([Fake(3, "10.0.0.2"), Fake(9, "192.168.1.5")], () => 9);
        Assert.Equal(9, route!.Value.InterfaceIndex);
    }

    [Fact]
    public void Tunnel_is_skipped_and_fallback_uses_gateway_when_best_fails()
    {
        var route = NetworkInfo.DefaultRouteInterface(
            [Fake(2, "10.1.1.1", type: NetworkInterfaceType.Tunnel), Fake(4, "172.16.0.2", gateway: false), Fake(5, "192.168.1.5")], () => null);
        Assert.Equal(5, route!.Value.InterfaceIndex);
    }

    [Fact]
    public void Adapter_guid_lookup_survives_throwing_adapter()
    {
        var g = Guid.NewGuid();
        var ok = () => new NetworkInfo.AdapterInfo(g.ToString("B"), OperationalStatus.Up, NetworkInterfaceType.Ethernet, 6, null, false);
        Assert.Equal(g, NetworkInfo.AdapterGuid(6, [Throwing(), ok]));
    }

    static Func<int, NetworkCategory> Cats(params (int Index, NetworkCategory Category)[] map) =>
        i => map.First(m => m.Index == i).Category;

    [Fact]
    public void Vpn_tunnel_default_route_is_skipped_for_private_wifi()
    {
        var route = NetworkInfo.DefaultRouteInterface(
            [Fake(5, "10.8.0.2", gateway: false, type: (NetworkInterfaceType)53), Fake(14, "192.168.3.7", type: NetworkInterfaceType.Wireless80211)],
            () => 5, Cats((5, NetworkCategory.Public), (14, NetworkCategory.Private)));
        Assert.Equal(14, route!.Value.InterfaceIndex);
    }

    [Fact]
    public void Private_lan_preferred_over_public_lan_even_if_best()
    {
        var route = NetworkInfo.DefaultRouteInterface(
            [Fake(3, "10.0.0.2"), Fake(9, "192.168.1.5")], () => 3, Cats((3, NetworkCategory.Public), (9, NetworkCategory.Domain)));
        Assert.Equal(9, route!.Value.InterfaceIndex);
    }

    [Fact]
    public void Best_index_wins_within_same_category()
    {
        var route = NetworkInfo.DefaultRouteInterface(
            [Fake(3, "10.0.0.2"), Fake(9, "192.168.1.5")], () => 9, Cats((3, NetworkCategory.Private), (9, NetworkCategory.Private)));
        Assert.Equal(9, route!.Value.InterfaceIndex);
    }

    [Fact]
    public void Adapter_without_gateway_is_not_picked()
    {
        var route = NetworkInfo.DefaultRouteInterface(
            [Fake(4, "172.20.0.1", gateway: false)], () => 4, Cats((4, NetworkCategory.Private)));
        Assert.Null(route);
    }

    [Fact]
    public void Only_tunnel_returns_null()
    {
        var route = NetworkInfo.DefaultRouteInterface(
            [Fake(2, "10.1.1.1", type: NetworkInterfaceType.Tunnel), Fake(5, "10.8.0.2", type: (NetworkInterfaceType)53)],
            () => 5, Cats((2, NetworkCategory.Private), (5, NetworkCategory.Private)));
        Assert.Null(route);
    }

    [Fact]
    public void Throwing_category_lookup_is_unknown_not_fatal()
    {
        var route = NetworkInfo.DefaultRouteInterface(
            [Fake(3, "10.0.0.2"), Fake(9, "192.168.1.5")], () => 3,
            i => i == 3 ? throw new InvalidOperationException() : NetworkCategory.Private);
        Assert.Equal(9, route!.Value.InterfaceIndex);
        var only = NetworkInfo.DefaultRouteInterface([Fake(3, "10.0.0.2")], () => 3, _ => throw new InvalidOperationException());
        Assert.Equal(3, only!.Value.InterfaceIndex);
    }
}
