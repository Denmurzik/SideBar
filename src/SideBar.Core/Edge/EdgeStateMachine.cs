using SideBar.Core.Layout;

namespace SideBar.Core.Edge;

/// <summary>Автомат состояний края: чистая функция от <see cref="EdgeInput"/>, без Win32.</summary>
public sealed class EdgeStateMachine(CardLayout layout, EdgeMode mode)
{
    public const int HideDelayMs = 300;
    public const int InPlaceDwellMs = 150;

    const long None = -1;

    CardLayout _layout = layout;
    EdgeMode _mode = mode;
    long _leftCardAt = None;
    long _enteredBandAt = None;
    bool _mustLeaveBand;
    PxRect? _keepOpen;

    public PanelState State { get; private set; } = PanelState.Hidden;

    /// <summary>Пока Open, курсор внутри этого прямоугольника (окно-веер) считается «над карточкой»; null — обычное поведение.</summary>
    public void SetKeepOpenRect(PxRect? rect) => _keepOpen = rect;

    public void Relayout(CardLayout layout, EdgeMode mode)
    {
        _layout = layout;
        _mode = mode;
        State = PanelState.Hidden;
        _leftCardAt = None;
        _enteredBandAt = None;
        _mustLeaveBand = false;
        _keepOpen = null;
    }

    /// <summary>Возвращает новое состояние, если оно изменилось, иначе null.</summary>
    public PanelState? Update(EdgeInput i)
    {
        var band = _layout.ActivationBand;
        bool inBandY = i.Y >= band.Top && i.Y <= band.Bottom;
        bool inBand = inBandY && i.X >= band.Left && i.X < band.Right;
        bool touching = inBandY && i.X >= band.Right - 1;
        // Slide: Y — по всей полосе, как при открытии, иначе курсор у края в поле над/под карточкой гоняет Open/Hidden по кругу.
        // InPlace от этого защищает _mustLeaveBand, там «над карточкой» — именно карточка.
        bool inCardY = _mode == EdgeMode.Slide ? inBandY : i.Y >= _layout.Top && i.Y <= _layout.Top + _layout.CardHeightPx;
        bool overCard = (inCardY && i.X >= _layout.LeftFor(PanelState.Open) && i.X < band.Right)
            || (_keepOpen is { } k && k.Contains(i.X, i.Y));

        switch (State)
        {
            case PanelState.Hidden:
                if (_mode == EdgeMode.InPlace) return HiddenInPlace(i, inBand);
                if (touching) return Go(PanelState.Open, i);
                if (inBand) return Go(PanelState.Peek, i);
                return null;
            case PanelState.Peek:
                if (touching) return Go(PanelState.Open, i);
                if (!inBand) return Go(PanelState.Hidden, i);
                return null;
            default:
                // ЛКМ зажата — идёт перетаскивание (возможно, из карточки): остаёмся открытыми до его конца
                if (overCard || i.LeftButton) _leftCardAt = None;
                else if (_leftCardAt == None) _leftCardAt = i.NowMs;
                else if (i.NowMs - _leftCardAt >= HideDelayMs) return Go(PanelState.Hidden, i);
                return null;
        }
    }

    PanelState? HiddenInPlace(EdgeInput i, bool inBand)
    {
        if (!inBand) { _enteredBandAt = None; _mustLeaveBand = false; return null; }
        if (_mustLeaveBand) return null;
        if (_enteredBandAt == None) _enteredBandAt = i.NowMs;
        if (i.NowMs - _enteredBandAt < InPlaceDwellMs) return null;
        return Go(PanelState.Open, i);
    }

    PanelState? Go(PanelState next, EdgeInput i)
    {
        if (State == PanelState.Hidden && next != PanelState.Hidden && i.FullscreenBusy && !i.LeftButton)
        {
            _enteredBandAt = None;
            return null;
        }
        _mustLeaveBand = _mode == EdgeMode.InPlace && next == PanelState.Hidden;
        State = next;
        _leftCardAt = None;
        _enteredBandAt = None;
        return next;
    }
}
