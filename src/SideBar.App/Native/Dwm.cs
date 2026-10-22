using System.Runtime.InteropServices;

namespace SideBar.Native;

static class Dwm
{
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    const int DWMWCP_ROUND = 2;
    const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>Системное скругление (8 px) без DWM-границы.</summary>
    public static void RoundCornersNoBorder(nint hwnd)
    {
        int round = DWMWCP_ROUND, none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, 4);
    }
}
