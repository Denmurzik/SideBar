namespace SideBar.Core.Shelf;

/// <summary>Armed — нажатие вооружило; Cleared — очистить полку; Ignored — нажатие сразу после очистки (быстрый двойной клик).</summary>
public enum ClearPress { Armed, Cleared, Ignored }

/// <summary>Двойной клик по корзине: первый вооружает на <see cref="Window"/>, второй в этот срок очищает полку. Только UI-поток.</summary>
public sealed class ClearConfirm
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    /// <summary>После очистки нажатия игнорируются: Tapped и DoubleTapped одного быстрого клика не должны вооружить пустую полку заново.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(600);

    DateTimeOffset? _armedAt;
    DateTimeOffset? _clearedAt;

    public bool IsArmed(DateTimeOffset now) => _armedAt is { } t && now - t <= Window;

    public ClearPress Press(DateTimeOffset now)
    {
        if (_clearedAt is { } c && now - c < Cooldown) return ClearPress.Ignored;
        if (IsArmed(now))
        {
            _armedAt = null;
            _clearedAt = now;
            return ClearPress.Cleared;
        }
        _armedAt = now;
        return ClearPress.Armed;
    }

    public void Reset() => _armedAt = null;
}
