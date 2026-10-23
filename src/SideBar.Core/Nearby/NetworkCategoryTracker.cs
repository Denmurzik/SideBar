namespace SideBar.Core.Nearby;

/// <summary>
/// Turns raw category lookups into the category the policy acts on. A failed lookup (COM or adapter enumeration threw,
/// null) is not NLM's real Unknown: the last good category is kept for the first failures, so one transient error
/// does not cancel an accepted transfer; <see cref="MaxFailures"/> consecutive failures count as Unknown.
/// </summary>
public sealed class NetworkCategoryTracker
{
    public const int MaxFailures = 2;

    NetworkCategory? _lastGood;
    int _failures;

    public NetworkCategory Observe(NetworkCategory? lookup)
    {
        if (lookup is { } category)
        {
            _failures = 0;
            _lastGood = category;
            return category;
        }
        _failures++;
        return _lastGood is { } last && _failures < MaxFailures ? last : NetworkCategory.Unknown;
    }

    /// <summary>The network was not looked at (or there is no route): forget the history.</summary>
    public void Reset()
    {
        _lastGood = null;
        _failures = 0;
    }
}
