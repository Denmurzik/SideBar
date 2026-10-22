namespace SideBar.Core.Edge;

public static class MonitorTopology
{
    /// <summary>Есть ли монитор вплотную справа от основного, пересекающийся по Y с полосой [bandTop, bandBottom].</summary>
    public static bool HasRightNeighbor(PxRect primary, IReadOnlyList<PxRect> all, int bandTop, int bandBottom)
    {
        foreach (var m in all)
        {
            if (m.Left == primary.Right && m.Top <= bandBottom && m.Bottom >= bandTop) return true;
        }
        return false;
    }
}
