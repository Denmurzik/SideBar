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
    long _touchedAt = None;
    bool _mustLeaveBand;
    PxRect? _keepOpen;

    public PanelState State { get; private set; } = PanelState.Hidden;

    /// <summary>Пока Open, курсор внутри этого прямоугольника (окно-веер) считается «над карточкой»; null — обычное поведение.</summary>
    public void SetKeepOpenRect(PxRect? rect) => _keepOpen = rect;

    /// <summary>
    /// Задержка открытия у края, мс (настройка): InPlace — сколько курсор стоит в полосе, Slide — сколько касается края (Peek до этого).
    /// Перетаскивание с зажатой ЛКМ открывает без задержки. null — как раньше: InPlace 150 мс, Slide без задержки.
    /// </summary>
    public int? OpenDelayMs { get; set; }

    /// <summary>Действующая задержка открытия, мс (настройка либо умолчание режима).</summary>
    public int EffectiveOpenDelayMs => DelayMs;

    int DelayMs => OpenDelayMs ?? (_mode == EdgeMode.InPlace ? InPlaceDwellMs : 0);

    /// <summary>Пока true, Open не скрывается по уходу курсора (поиск устройств и передача из плитки Quick Share).</summary>
    public bool HoldOpen { get; set; }

    public void Relayout(CardLayout layout, EdgeMode mode)
    {
        _layout = layout;
        _mode = mode;
        State = PanelState.Hidden;
        _leftCardAt = None;
        _enteredBandAt = None;
        _touchedAt = None;
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
                if (TouchOpens(touching, i)) return Go(PanelState.Open, i);
                if (inBand) return Go(PanelState.Peek, i);
                return null;
            case PanelState.Peek:
                if (TouchOpens(touching, i)) return Go(PanelState.Open, i);
                if (!inBand) return Go(PanelState.Hidden, i);
                return null;
            default:
                // ЛКМ зажата — идёт перетаскивание (возможно, из карточки): остаёмся открытыми до его конца
                if (overCard || i.LeftButton || HoldOpen) _leftCardAt = None;
                else if (_leftCardAt == None) _leftCardAt = i.NowMs;
                else if (i.NowMs - _leftCardAt >= HideDelayMs) return Go(PanelState.Hidden, i);
                return null;
        }
    }

    // Slide: касание края открывает не раньше, чем через DelayMs непрерывного касания; ЛКМ зажата (перетаскивание) — сразу
    bool TouchOpens(bool touching, EdgeInput i)
    {
        if (!touching) { _touchedAt = None; return false; }
        if (i.LeftButton || DelayMs <= 0) return true;
        if (_touchedAt == None) _touchedAt = i.NowMs;
        return i.NowMs - _touchedAt >= DelayMs;
    }

    PanelState? HiddenInPlace(EdgeInput i, bool inBand)
    {
        if (!inBand) { _enteredBandAt = None; _mustLeaveBand = false; return null; }
        if (_mustLeaveBand) return null;
        if (_enteredBandAt == None) _enteredBandAt = i.NowMs;
        if (i.NowMs - _enteredBandAt < DelayMs) return null;
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
        if (next != PanelState.Peek) _touchedAt = None;   // Peek сохраняет отсчёт касания до открытия
        return next;
    }
}
