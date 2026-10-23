using System.Runtime.InteropServices;

namespace SideBar.Official;

/// <summary>Win32 для управления официальным Quick Share и замера простоя. Только blittable-интероп (AOT).</summary>
internal static unsafe class Native
{
    public const uint STARTF_USESHOWWINDOW = 1;
    public const ushort SW_HIDE = 0;
    public const uint WM_QUIT = 0x0012;
    const uint WAIT_OBJECT_0 = 0, THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize, dwTime;
    }

    public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    public const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int CreateProcessW(char* app, char* cmd, nint pa, nint ta, int inherit, uint flags, nint env,
        char* cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern int CloseHandle(nint h);
    [DllImport("kernel32.dll", ExactSpelling = true)] static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32.dll", ExactSpelling = true)] static extern uint GetTickCount();
    [DllImport("kernel32.dll", ExactSpelling = true)] static extern nint OpenThread(uint access, int inherit, uint tid);
    [DllImport("kernel32.dll", ExactSpelling = true)] static extern uint GetProcessIdOfThread(nint hThread);

    [DllImport("user32.dll", ExactSpelling = true)] static extern int GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll", ExactSpelling = true)] static extern int EnumWindows(delegate* unmanaged<nint, nint, int> cb, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", ExactSpelling = true)] static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] static extern int GetClassNameW(nint hwnd, char* buf, int max);
    [DllImport("user32.dll", ExactSpelling = true)] static extern int PostThreadMessageW(uint tid, uint msg, nint w, nint l);

    public readonly record struct WindowRec(nint Hwnd, uint ThreadId, string ClassName, bool Visible);

    /// <summary>Сколько пользователь не касался клавиатуры/мыши. Ошибка вызова — ноль (считаем «активен»).</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        if (GetLastInputInfo(ref info) == 0) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked(GetTickCount() - info.dwTime));
    }

    /// <summary>Процесс завершился (по дескриптору, который мы получили от CreateProcess, — PID не переиспользуется).</summary>
    public static bool HasExited(nint hProcess) => WaitForSingleObject(hProcess, 0) == WAIT_OBJECT_0;

    /// <summary>Окна верхнего уровня процесса <paramref name="pid"/>.</summary>
    public static List<WindowRec> WindowsOf(int pid)
    {
        var list = new List<(nint, uint, string, bool)>();
        var handle = GCHandle.Alloc((list, pid));
        try { EnumWindows(&EnumCallback, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return list.ConvertAll(w => new WindowRec(w.Item1, w.Item2, w.Item3, w.Item4));
    }

    [UnmanagedCallersOnly]
    static int EnumCallback(nint hwnd, nint state)
    {
        var (list, pid) = ((List<(nint, uint, string, bool)>, int))GCHandle.FromIntPtr(state).Target!;
        var tid = GetWindowThreadProcessId(hwnd, out var owner);
        if (owner != (uint)pid) return 1;
        var buf = stackalloc char[256];
        var n = GetClassNameW(hwnd, buf, 256);
        list.Add((hwnd, tid, new string(buf, 0, Math.Max(n, 0)), IsWindowVisible(hwnd) != 0));
        return 1;
    }

    /// <summary>
    /// WM_QUIT потоку, только если он принадлежит <paramref name="pid"/>: открытый дескриптор не даёт TID переиспользоваться
    /// между проверкой и отправкой.
    /// </summary>
    public static bool PostQuitToThread(uint threadId, int pid)
    {
        var h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, 0, threadId);
        if (h == 0) return false;
        try { return GetProcessIdOfThread(h) == (uint)pid && PostThreadMessageW(threadId, WM_QUIT, 0, 0) != 0; }
        finally { CloseHandle(h); }
    }
}
