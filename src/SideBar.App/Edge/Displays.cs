using System.Runtime.InteropServices;
using SideBar.Core;

namespace SideBar.Edge;

/// <summary>Мониторы в физических пикселях (процесс PerMonitorV2).</summary>
static unsafe class Displays
{
    const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint hdc, nint clip, nint proc, nint data);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint mon, int type, out uint x, out uint y);

    // EnumDisplayMonitors вызывает колбэк синхронно в вызывающем потоке
    [ThreadStatic] static List<PxRect>? t_list;

    static PxRect Rect(nint mon)
    {
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(mon, ref mi);
        return new PxRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
    }

    /// <summary>Рабочая область основного монитора (без панели задач), физические px.</summary>
    public static PxRect PrimaryWorkArea()
    {
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(PrimaryHandle(), ref mi);
        return new PxRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
    }

    static nint PrimaryHandle() => MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);

    public static PxRect Primary() => Rect(PrimaryHandle());

    public static double PrimaryScale()
    {
        if (GetDpiForMonitor(PrimaryHandle(), 0, out var dx, out _) != 0 || dx == 0) return 1.0;
        return dx / 96.0;
    }

    [UnmanagedCallersOnly]
    static int EnumProc(nint mon, nint hdc, RECT* rc, nint data)
    {
        t_list?.Add(new PxRect(rc->Left, rc->Top, rc->Right, rc->Bottom));
        return 1;
    }

    public static IReadOnlyList<PxRect> All()
    {
        var list = new List<PxRect>();
        t_list = list;
        try { EnumDisplayMonitors(0, 0, (nint)(delegate* unmanaged<nint, nint, RECT*, nint, int>)&EnumProc, 0); }
        finally { t_list = null; }
        return list;
    }
}
