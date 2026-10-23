namespace SideBar.Core.Shelf;

public enum ClearPress { Armed, Cleared }

/// <summary>Двойной клик по корзине: первый вооружает на <see cref="Window"/>, второй в этот срок очищает полку. Только UI-поток.</summary>
public sealed class ClearConfirm
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    DateTimeOffset? _armedAt;

    public bool IsArmed(DateTimeOffset now) => _armedAt is { } t && now - t <= Window;

    public ClearPress Press(DateTimeOffset now)
    {
        if (IsArmed(now))
        {
            _armedAt = null;
            return ClearPress.Cleared;
        }
        _armedAt = now;
        return ClearPress.Armed;
    }

    public void Reset() => _armedAt = null;
}
