using SideBar.Core;
using SideBar.Core.Edge;

namespace SideBar.Core.Tests;

public class MonitorTopologyTests
{
    static readonly PxRect Primary = new(0, 0, 1920, 1080);

    [Fact]
    public void Neighbor_to_the_right_overlapping_band()
    {
        var other = new PxRect(1920, 0, 3840, 1080);
        Assert.True(MonitorTopology.HasRightNeighbor(Primary, [Primary, other], 300, 780));
    }

    [Fact]
    public void Neighbor_right_but_outside_band()
    {
        var other = new PxRect(1920, 1200, 3840, 2280);
        Assert.False(MonitorTopology.HasRightNeighbor(Primary, [Primary, other], 300, 780));
    }

    [Fact]
    public void Neighbor_on_the_left_is_ignored()
    {
        var other = new PxRect(-1920, 0, 0, 1080);
        Assert.False(MonitorTopology.HasRightNeighbor(Primary, [Primary, other], 300, 780));
    }
}
