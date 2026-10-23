using System.Runtime.InteropServices;

namespace SideBar.Edge;

/// <summary>
/// WH_MOUSE_LL в своём потоке со своим циклом сообщений. Колбэк хука только сохраняет точку и будит рабочий поток;
/// onMove и onPrimaryDown (нажатие основной кнопки, с координатами) вызываются на рабочем потоке, так что хук никогда не ждёт UI или I/O.
/// </summary>
sealed unsafe class MouseHook : IDisposable
{
    const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x200, WM_QUIT = 0x12;
    const int TickMs = 25;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll")] static extern nint SetWindowsHookExW(int id, nint proc, nint mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, nint w, nint l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(string? name);

    // колбэк хука — статический [UnmanagedCallersOnly] (AOT), поэтому экземпляр один
    static MouseHook? s_instance;

    readonly Action<int, int> _onMove;
    readonly Action<int, int>? _onPrimaryDown;
    readonly System.Collections.Concurrent.ConcurrentQueue<long> _downs = new();
    readonly AutoResetEvent _signal = new(false);
    long _lastPt;   // упаковано (x, y)
    uint _hookThreadId;
    volatile bool _stop;
    volatile bool _fastTick;

    public MouseHook(Action<int, int> onMove, Action<int, int>? onPrimaryDown = null)
    {
        _onMove = onMove;
        _onPrimaryDown = onPrimaryDown;
        GetCursorPos(out var p);
        _lastPt = Pack(p.X, p.Y);
        s_instance = this;

        var ready = new ManualResetEventSlim();
        new Thread(() => HookThread(ready)) { IsBackground = true, Name = "mouse-hook" }.Start();
        ready.Wait();
        new Thread(Worker) { IsBackground = true, Name = "edge-worker" }.Start();
    }

    /// <summary>Пока true — рабочий поток просыпается каждые 25 мс даже без движения мыши (ожидание задержки скрытия / dwell).</summary>
    public bool FastTick { set => _fastTick = value; }

    static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

    void HookThread(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        nint hook = SetWindowsHookExW(WH_MOUSE_LL, (nint)(delegate* unmanaged<int, nint, nint, nint>)&HookProc, GetModuleHandleW(null), 0);
        ready.Set();
        while (GetMessageW(out _, 0, 0, 0) > 0) { }
        if (hook != 0) UnhookWindowsHookEx(hook);
    }

    [UnmanagedCallersOnly]
    static nint HookProc(int code, nint wParam, nint lParam)
    {
        var w = s_instance;
        if (code >= 0 && w != null)
        {
            var h = (MSLLHOOKSTRUCT*)lParam;
            if ((int)wParam == WM_MOUSEMOVE)
            {
                Interlocked.Exchange(ref w._lastPt, Pack(h->pt.X, h->pt.Y));
                w._signal.Set();
            }
            else if (w._onPrimaryDown != null && PrimaryButton.IsDownMessage((int)wParam))
            {
                w._downs.Enqueue(Pack(h->pt.X, h->pt.Y));
                w._signal.Set();
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    void Worker()
    {
        while (!_stop)
        {
            _signal.WaitOne(_fastTick ? TickMs : -1);
            if (_stop) break;
            try
            {
                while (_downs.TryDequeue(out long d)) _onPrimaryDown?.Invoke((int)(d >> 32), (int)(d & 0xFFFFFFFF));
                long p = Interlocked.Read(ref _lastPt);
                _onMove((int)(p >> 32), (int)(p & 0xFFFFFFFF));
            }
            catch { /* рабочий поток не должен падать из-за сбоя обработчика */ }
        }
    }

    public void Dispose()
    {
        _stop = true;
        PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
        _signal.Set();
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }
}
