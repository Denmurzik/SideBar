using System.Runtime.InteropServices;

namespace SideBar.Native;

static class ZOrder
{
    const nint HWND_TOPMOST = -1;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;

    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint mod, nint proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")] static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32.dll")] static extern bool KillTimer(nint hwnd, nuint id);

    const uint ReraiseDelayMs = 150;

    static nint _card, _hook, _follower;
    static nuint _timer;
    static bool _visible;

    /// <summary>Пока карточка видима, смена foreground-окна возвращает её на вершину (иначе позже активированное topmost-окно перекроет статичную карточку).</summary>
    public static bool Visible { set => _visible = value; }

    /// <summary>Окно, которое держится сразу под карточкой (веер): ставит его туда и при каждом подъёме карточки возвращает следом. 0 — отпустить.</summary>
    public static void Follow(nint hwnd)
    {
        _follower = hwnd;
        if (hwnd != 0 && _card != 0) PlaceBelow(hwnd, _card);
    }

    // OUTOFCONTEXT: колбэк приходит на поток, установивший хук (UI-поток с циклом сообщений).
    [UnmanagedCallersOnly]
    static unsafe void OnForeground(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_visible || hwnd == _card) return;
        RaiseCard();
        // Событие приходит до того, как активируемое окно закончит собственную перестановку по z-порядку,
        // поэтому повторяем подъём коротким одноразовым таймером.
        if (_timer != 0) KillTimer(0, _timer);
        _timer = SetTimer(0, 0, ReraiseDelayMs, (nint)(delegate* unmanaged<nint, uint, nuint, uint, void>)&OnReraise);
    }

    [UnmanagedCallersOnly]
    static void OnReraise(nint hwnd, uint msg, nuint id, uint time)
    {
        KillTimer(0, id);
        _timer = 0;
        if (_visible) RaiseCard();
    }

    static void RaiseCard()
    {
        RaiseToTop(_card);
        if (_follower != 0) PlaceBelow(_follower, _card);
    }

    public static unsafe void Watch(nint card)
    {
        if (_hook != 0) return;
        _card = card;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0,
            (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&OnForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void Unwatch()
    {
        if (_timer != 0) KillTimer(0, _timer);
        _timer = 0;
        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = 0;
        _visible = false;
        _follower = 0;
    }

    /// <summary>Поднять на вершину topmost-полосы, не активируя и не меняя положение/размер/видимость.</summary>
    public static void RaiseToTop(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Поставить <paramref name="hwnd"/> сразу под <paramref name="above"/> (оба topmost), не активируя.</summary>
    public static void PlaceBelow(nint hwnd, nint above) =>
        SetWindowPos(hwnd, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
