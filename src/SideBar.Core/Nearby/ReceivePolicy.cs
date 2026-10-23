namespace SideBar.Core.Nearby;

public readonly record struct ReceiveDecision(bool Listen, bool CancelUnaccepted, bool CancelAccepted);

/// <summary>
/// What the receiver does for a visibility and network category (null: no usable route).
/// OwnDevices and Everyone listen; Hidden stops listening and drops sessions still waiting for consent, but an accepted transfer keeps running;
/// a Public/Unknown network or a lost route ends everything, whatever the visibility.
/// </summary>
public static class ReceivePolicy
{
    /// <param name="networkChecked">false when the network was not looked at (Hidden, no accepted transfer at that moment): nothing accepted may be cancelled then.</param>
    public static ReceiveDecision Decide(Visibility visibility, NetworkCategory? category, bool networkChecked = true)
    {
        var networkOk = category is NetworkCategory.Private or NetworkCategory.Domain;
        var listen = visibility != Visibility.Hidden && networkOk;
        return new ReceiveDecision(listen, CancelUnaccepted: !listen, CancelAccepted: networkChecked && !networkOk);
    }
}
