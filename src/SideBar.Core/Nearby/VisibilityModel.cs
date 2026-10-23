namespace SideBar.Core.Nearby;

public enum Visibility { Hidden, OwnDevices, Everyone }

public enum NetworkCategory { Unknown, Public, Private, Domain }

/// <summary>
/// Quick Share visibility: Hidden, OwnDevices (default while the official app's certificates load) or Everyone for 10 minutes,
/// after which the previous mode returns. Without certificates OwnDevices degrades to Hidden (and comes back with them).
/// Time is passed in by the caller. Receiving is never allowed on a Public or Unknown network, whatever the visibility.
/// </summary>
public sealed class VisibilityModel
{
    public static readonly TimeSpan EveryoneDuration = TimeSpan.FromMinutes(10);

    // the mode the user wants outside Everyone: Hidden or OwnDevices (the latter may be unavailable)
    Visibility _base;
    bool _everyone;
    bool _available;

    /// <param name="ownDevicesAvailable">The certificates were loaded.</param>
    /// <param name="saved">The persisted mode; null or Everyone means no choice yet, so OwnDevices is wanted.</param>
    public VisibilityModel(bool ownDevicesAvailable = false, Visibility? saved = null)
    {
        _available = ownDevicesAvailable;
        _base = saved == Visibility.Hidden ? Visibility.Hidden : Visibility.OwnDevices;
    }

    public Visibility Current => _everyone ? Visibility.Everyone : _base == Visibility.OwnDevices && _available ? Visibility.OwnDevices : Visibility.Hidden;
    public DateTimeOffset? EveryoneUntil { get; private set; }

    /// <summary>The certificates are usable, so "Own devices" can be chosen.</summary>
    public bool OwnDevicesAvailable => _available;

    /// <summary>OwnDevices is wanted but there are no certificates: the mode works as Hidden.</summary>
    public bool OwnDevicesDegraded => _base == Visibility.OwnDevices && !_available;

    /// <summary>What to persist: the mode outside Everyone (Everyone is never saved).</summary>
    public Visibility Persisted => _base;

    /// <summary>OwnDevices accepts only verified peers; Everyone and Hidden ask the user as before.</summary>
    public bool RejectsUnverified => Current == Visibility.OwnDevices;

    public event Action? Changed;

    /// <summary>Chooses a mode. False when OwnDevices is chosen without certificates. Choosing Everyone again does not extend the 10 minutes.</summary>
    public bool Select(Visibility mode, DateTimeOffset now)
    {
        if (mode == Visibility.OwnDevices && !_available) return false;
        var before = Current;
        if (mode == Visibility.Everyone)
        {
            if (_everyone) return true;
            _everyone = true;
            EveryoneUntil = now + EveryoneDuration;
        }
        else
        {
            _everyone = false;
            EveryoneUntil = null;
            _base = mode;
        }
        if (Current != before) Changed?.Invoke();
        return true;
    }

    public void EnableEveryone(DateTimeOffset now) => Select(Visibility.Everyone, now);

    public void Disable() => Select(Visibility.Hidden, default);

    /// <summary>The certificates were (re)loaded or lost.</summary>
    public void SetOwnDevicesAvailable(bool available)
    {
        if (_available == available) return;
        var before = Current;
        _available = available;
        if (Current != before) Changed?.Invoke();
    }

    public void Tick(DateTimeOffset now)
    {
        if (!_everyone || EveryoneUntil is not { } until || now < until) return;
        var before = Current;
        _everyone = false;
        EveryoneUntil = null;
        if (Current != before) Changed?.Invoke();
    }

    public bool ShouldListen(NetworkCategory category) =>
        Current != Visibility.Hidden && category is NetworkCategory.Private or NetworkCategory.Domain;
}
