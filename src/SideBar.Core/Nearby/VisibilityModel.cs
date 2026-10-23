namespace SideBar.Core.Nearby;

public enum Visibility { Hidden, Everyone }

public enum NetworkCategory { Unknown, Public, Private, Domain }

/// <summary>
/// Quick Share visibility: Hidden by default, Everyone for 10 minutes. Time is passed in by the caller.
/// Receiving is never allowed on a Public or Unknown network, whatever the visibility.
/// </summary>
public sealed class VisibilityModel
{
    public static readonly TimeSpan EveryoneDuration = TimeSpan.FromMinutes(10);

    public Visibility Current { get; private set; } = Visibility.Hidden;
    public DateTimeOffset? EveryoneUntil { get; private set; }

    public event Action? Changed;

    public void EnableEveryone(DateTimeOffset now)
    {
        Current = Visibility.Everyone;
        EveryoneUntil = now + EveryoneDuration;
        Changed?.Invoke();
    }

    public void Disable()
    {
        if (Current == Visibility.Hidden) return;
        Current = Visibility.Hidden;
        EveryoneUntil = null;
        Changed?.Invoke();
    }

    public void Tick(DateTimeOffset now)
    {
        if (Current != Visibility.Everyone || EveryoneUntil is not { } until || now < until) return;
        Current = Visibility.Hidden;
        EveryoneUntil = null;
        Changed?.Invoke();
    }

    public bool ShouldListen(NetworkCategory category) =>
        Current == Visibility.Everyone && category is NetworkCategory.Private or NetworkCategory.Domain;
}
