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

    public static bool IsTopmost(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>Поднять на вершину topmost-полосы, не активируя и не меняя положение/размер/видимость.</summary>
    public static void RaiseToTop(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
