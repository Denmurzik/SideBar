using System.Runtime.InteropServices;

namespace SideBar.Native;

static class WorkingSet
{
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(nint process);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);

    public static void Trim()
    {
        nint p = GetCurrentProcess();
        EmptyWorkingSet(p);
        SetProcessWorkingSetSize(p, -1, -1);
    }
}
