using System.Runtime.InteropServices;
using SideBar.Core;

namespace SideBar.Edge;

static unsafe class FullscreenProbe
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint h, char* buf, int max);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    // QUNS_BUSY=2, QUNS_RUNNING_D3D_FULL_SCREEN=3, QUNS_PRESENTATION_MODE=4.
    // QUNS_BUSY сам по себе не доверяем (бывает без полноэкранных приложений): нужно ещё,
    // чтобы окно переднего плана (не рабочий стол/оболочка) покрывало весь монитор.
    public static bool IsBusy(PxRect primary)
    {
        if (SHQueryUserNotificationState(out int state) != 0) return false;
        if (state is 3 or 4) return true;
        return state == 2 && ForegroundCovers(primary);
    }

    static bool ForegroundCovers(PxRect m)
    {
        var h = GetForegroundWindow();
        if (h == 0) return false;
        var buf = stackalloc char[128];
        string cls = new(buf, 0, GetClassNameW(h, buf, 128));
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        if (!GetWindowRect(h, out var r)) return false;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
