using System.Runtime.InteropServices;

namespace SideBar.Native;

static class WindowFrame
{
    const int GWL_EXSTYLE = -20;
    const nint WS_EX_TOOLWINDOW = 0x80;
    const uint WM_NCCALCSIZE = 0x83;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(nint hwnd, nint proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint msg, nint w, nint l);

    static readonly HashSet<nint> _stripped = [];

    public static uint GetDpi(nint hwnd) => GetDpiForWindow(hwnd);

    public static void MoveTo(nint hwnd, int x, int y) =>
        SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Не в Alt-Tab; без WS_EX_LAYERED (слоистое окно ломает акрил).</summary>
    public static void MakeTool(nint hwnd) =>
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

    // Окно без заголовка всё ещё имеет 3-px non-client рамку: WM_NCCALCSIZE -> 0 делает клиентской всю область.
    [UnmanagedCallersOnly]
    static nint NcProc(nint h, uint msg, nint w, nint l, nuint id, nuint data)
    {
        if (msg == WM_NCCALCSIZE && w != 0) return 0;
        return DefSubclassProc(h, msg, w, l);
    }

    /// <summary>Вызывать после первого Show.</summary>
    public static unsafe void Strip(nint hwnd)
    {
        if (!_stripped.Add(hwnd)) return;
        SetWindowSubclass(hwnd, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&NcProc, 1, 0);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }
}
