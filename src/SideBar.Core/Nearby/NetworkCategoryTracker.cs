namespace SideBar.Core.Nearby;

/// <summary>
/// Turns raw category lookups into the category the policy acts on. A failed lookup (COM or adapter enumeration threw,
/// null) is not NLM's real Unknown: the last good category is kept while the failures have lasted less than
/// <see cref="MaxFailureSpan"/>. A burst of failed evaluations (a VPN connecting fires several network events) does not count.
/// Time is passed in by the caller.
/// </summary>
public sealed class NetworkCategoryTracker
{
    public static readonly TimeSpan MaxFailureSpan = TimeSpan.FromSeconds(30);

    NetworkCategory? _lastGood;
    DateTimeOffset? _failingSince;

    public NetworkCategory Observe(NetworkCategory? lookup, DateTimeOffset now)
    {
        if (lookup is { } category)
        {
            _failingSince = null;
            _lastGood = category;
            return category;
        }
        _failingSince ??= now;
        return _lastGood is { } last && now - _failingSince.Value < MaxFailureSpan ? last : NetworkCategory.Unknown;
    }

    /// <summary>The network was not looked at (or there is no route): forget the history.</summary>
    public void Reset()
    {
        _lastGood = null;
        _failingSince = null;
    }
}
