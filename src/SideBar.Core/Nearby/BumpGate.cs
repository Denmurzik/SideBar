namespace SideBar.Core.Nearby;

/// <summary>Limits the card's "bump" on incoming transfers to one per interval; time is passed in by the caller.</summary>
public sealed class BumpGate(long minIntervalMs = 3000)
{
    long? _last;

    public bool TryBump(long nowMs)
    {
        if (_last is { } last && nowMs - last < minIntervalMs) return false;
        _last = nowMs;
        return true;
    }
}
