namespace SideBar.Core.Nearby;

/// <summary>
/// Dock-style attention: while a consent is pending and the card is Hidden, bounce right away and then every
/// <see cref="Interval"/>. Never while the card is Open or Peek, never over a fullscreen app. Time is passed in by the caller.
/// </summary>
public sealed class AttentionBounce
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    long? _lastMs;

    /// <param name="fullscreen">Evaluated lazily, only when a bounce is otherwise due (the probe is not free).</param>
    public bool Due(long nowMs, bool consentPending, PanelState state, Func<bool> fullscreen)
    {
        if (!consentPending)
        {
            _lastMs = null;
            return false;
        }
        if (state != PanelState.Hidden) return false;
        if (_lastMs is { } last && nowMs - last < Interval.TotalMilliseconds) return false;
        if (fullscreen()) return false;
        _lastMs = nowMs;
        return true;
    }
}
