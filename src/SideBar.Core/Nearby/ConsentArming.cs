namespace SideBar.Core.Nearby;

/// <summary>
/// Guards the consent buttons against a tap that was not meant for them: a button counts only when the card is Open and settled,
/// and the press came at least <see cref="Delay"/> after the rows (or the tile content) were last placed or moved, or the card settled (a new consent, a removed one,
/// the card arriving). Time is passed in by the caller.
/// </summary>
public sealed class ConsentArming
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);

    DateTimeOffset? _placedAt;

    /// <summary>The rows were placed or moved just now.</summary>
    public void Placed(DateTimeOffset now) => _placedAt = now;

    public bool CanAccept(DateTimeOffset pressedAt, bool cardSettledOpen) =>
        cardSettledOpen && _placedAt is { } placed && pressedAt - placed >= Delay;
}
