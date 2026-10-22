using System.Runtime.InteropServices;

namespace SideBar.Tray;

/// <summary>
/// Скрытое обычное окно (не HWND_MESSAGE — такие не получают широковещательный TaskbarCreated) на UI-потоке.
/// Пересылает смену экрана/DPI в onDisplaysChanged с задержкой 250 мс (WM_SETTINGCHANGE идёт пачками),
/// остальные сообщения — в событие Message.
/// </summary>
sealed unsafe class MessageWindow : IDisposable
{
    const uint WM_DISPLAYCHANGE = 0x7E, WM_SETTINGCHANGE = 0x1A, WM_DPICHANGED = 0x2E0, WM_TIMER = 0x113;
    const uint WS_EX_TOOLWINDOW = 0x80, WS_POPUP = 0x80000000;
    const nuint DebounceTimerId = 1;
    const uint DebounceMs = 250;
    const string ClassName = "SideBar.MessageWindow";

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [DllImport("user32.dll")] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll")] static extern bool UnregisterClassW(char* name, nint hInstance);
    [DllImport("user32.dll")] static extern nint CreateWindowExW(uint exStyle, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32.dll")] static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(string? name);

    // WndProc — статический [UnmanagedCallersOnly] (AOT), поэтому экземпляр один
    static MessageWindow? s_instance;

    readonly Action _onDisplaysChanged;
    nint _hwnd;

    public MessageWindow(Action onDisplaysChanged)
    {
        _onDisplaysChanged = onDisplaysChanged;
        s_instance = this;
        nint inst = GetModuleHandleW(null);
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = inst,
                lpszClassName = (nint)cls,
            };
            RegisterClassExW(&wc);
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, cls, cls, WS_POPUP, 0, 0, 0, 0, 0, 0, inst, 0);
        }
    }

    public nint Handle => _hwnd;

    /// <summary>Любое сообщение окна (msg, wParam, lParam) — для трея.</summary>
    public event Action<uint, nint, nint>? Message;

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var w = s_instance;
        if (w is not null)
        {
            try { w.OnMessage(msg, wParam, lParam); }
            catch { /* исключение не должно пересекать границу unmanaged */ }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    void OnMessage(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE or WM_SETTINGCHANGE or WM_DPICHANGED:
                SetTimer(_hwnd, DebounceTimerId, DebounceMs, 0);   // повторный SetTimer перезапускает отсчёт
                break;
            case WM_TIMER when wParam == (nint)DebounceTimerId:
                KillTimer(_hwnd, DebounceTimerId);
                _onDisplaysChanged();
                break;
        }
        Message?.Invoke(msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd == 0) return;
        KillTimer(_hwnd, DebounceTimerId);
        DestroyWindow(_hwnd);
        _hwnd = 0;
        fixed (char* cls = ClassName) UnregisterClassW(cls, GetModuleHandleW(null));
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }
}
