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

    public PanelState State { get; private set; } = PanelState.Hidden;

    public void Relayout(CardLayout layout, EdgeMode mode)
    {
        _layout = layout;
        _mode = mode;
        State = PanelState.Hidden;
        _leftCardAt = None;
        _enteredBandAt = None;
    }

    /// <summary>Возвращает новое состояние, если оно изменилось, иначе null.</summary>
    public PanelState? Update(EdgeInput i)
    {
        var band = _layout.ActivationBand;
        bool inBandY = i.Y >= band.Top && i.Y <= band.Bottom;
        bool inBand = inBandY && i.X >= band.Left && i.X < band.Right;
        bool touching = inBandY && i.X >= band.Right - 1;
        bool overCard = i.X >= _layout.LeftFor(PanelState.Open)
            && i.Y >= _layout.Top && i.Y <= _layout.Top + _layout.CardHeightPx;

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
        if (!inBand) { _enteredBandAt = None; return null; }
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
        State = next;
        _leftCardAt = None;
        _enteredBandAt = None;
        return next;
    }
}
