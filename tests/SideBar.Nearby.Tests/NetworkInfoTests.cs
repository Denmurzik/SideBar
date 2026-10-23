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
}
