using System.Runtime.InteropServices;

namespace SideBar.Native;

static class ZOrder
{
    const int GWL_EXSTYLE = -20;
    const nint WS_EX_TOPMOST = 0x8;
    const nint HWND_TOPMOST = -1;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;

    delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint mod, nint proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")] static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32.dll")] static extern bool KillTimer(nint hwnd, nuint id);

    const uint ReraiseDelayMs = 150;

    static nint _card, _hook;
    static nuint _timer;
    static bool _visible;

    /// <summary>Пока карточка видима, смена foreground-окна возвращает её на вершину (иначе позже активированное topmost-окно перекроет статичную карточку).</summary>
    public static bool Visible { set => _visible = value; }

    // OUTOFCONTEXT: колбэк приходит на поток, установивший хук (UI-поток с циклом сообщений).
    [UnmanagedCallersOnly]
    static unsafe void OnForeground(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_visible || hwnd == _card) return;
        RaiseToTop(_card);
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
        if (_visible) RaiseToTop(_card);
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
    }

    public static bool IsTopmost(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>Поднять на вершину topmost-полосы, не активируя и не меняя положение/размер/видимость.</summary>
    public static void RaiseToTop(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
