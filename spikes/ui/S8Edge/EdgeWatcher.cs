using System.Diagnostics;
using System.Runtime.InteropServices;

namespace S8Edge;

public enum EdgeState { Hidden, Peek, Open }

// WH_MOUSE_LL on its own thread with its own message loop. The hook callback only stores the point and signals;
// the state machine runs on a worker thread, so the hook never blocks on UI or I/O.
public sealed unsafe class EdgeWatcher
{
    const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x200, WM_QUIT = 0x12;
    const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookExW(int id, nint proc, nint mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, nint w, nint l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint mon, int type, out uint x, out uint y);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    // single static instance: the hook procedure is a static [UnmanagedCallersOnly] function (AOT friendly)
    static EdgeWatcher? s_instance;
    long _lastPt;                 // packed (x, y)
    long _moves, _movesDuringButton;
    readonly AutoResetEvent _signal = new(false);
    uint _hookThreadId;
    volatile bool _stop;

    public int MonitorRight { get; private set; }
    public int MonitorTop { get; private set; }
    public int MonitorBottom { get; private set; }
    public double Dpi { get; private set; }
    public int PeekPx => (int)Math.Round(Dpi / 2.54);    // 1 cm
    public int PanelPx { get; set; } = 340;
    public int CardTop { get; set; }
    public int CardBottom { get; set; }
    public int YMarginPx => PeekPx;                       // 1 cm above/below the card
    public int HideDelayMs { get; set; } = 300;

    public event Action<EdgeState, string>? StateChanged;
    public event Action<string>? Log;
    public EdgeState State { get; private set; } = EdgeState.Hidden;
    public long Moves => Interlocked.Read(ref _moves);
    public long MovesDuringButton => Interlocked.Read(ref _movesDuringButton);

    public void Start()
    {
        s_instance = this;
        var mon = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        GetMonitorInfoW(mon, ref mi);
        MonitorRight = mi.rcMonitor.Right; MonitorTop = mi.rcMonitor.Top; MonitorBottom = mi.rcMonitor.Bottom;
        GetDpiForMonitor(mon, 0, out var dx, out _);
        Dpi = dx;
        Log?.Invoke($"primary monitor right={MonitorRight} top={MonitorTop} bottom={MonitorBottom} dpi={Dpi} peekPx={PeekPx}");

        var ready = new ManualResetEventSlim();
        new Thread(() => HookThread(ready)) { IsBackground = true, Name = "mouse-hook" }.Start();
        ready.Wait();
        new Thread(Worker) { IsBackground = true, Name = "edge-worker" }.Start();
    }

    public void Stop()
    {
        _stop = true;
        PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
        _signal.Set();
    }

    void HookThread(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        nint hook = SetWindowsHookExW(WH_MOUSE_LL, (nint)(delegate* unmanaged<int, nint, nint, nint>)&HookProc, GetModuleHandleW(null), 0);
        Log?.Invoke($"hook installed={(hook != 0)} thread={_hookThreadId}");
        ready.Set();
        while (GetMessageW(out _, 0, 0, 0) > 0) { }
        UnhookWindowsHookEx(hook);
    }

    [UnmanagedCallersOnly]
    static nint HookProc(int code, nint wParam, nint lParam)
    {
        var w = s_instance;
        if (code >= 0 && w != null && (int)wParam == WM_MOUSEMOVE)
        {
            var h = (MSLLHOOKSTRUCT*)lParam;
            Interlocked.Exchange(ref w._lastPt, ((long)h->pt.X << 32) | (uint)h->pt.Y);
            Interlocked.Increment(ref w._moves);
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0) Interlocked.Increment(ref w._movesDuringButton);
            w._signal.Set();
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    // --- state machine ---
    long _leftPanelAt;

    void Worker()
    {
        while (!_stop)
        {
            int timeout = State == EdgeState.Open && _leftPanelAt != 0 ? 25 : -1;
            _signal.WaitOne(timeout);
            if (_stop) break;
            long p = Interlocked.Read(ref _lastPt);
            int x = (int)(p >> 32), y = (int)(p & 0xFFFFFFFF);
            Step(x, y);
        }
    }

    void Step(int x, int y)
    {
        bool onMonitorY = y >= CardTop - YMarginPx && y <= CardBottom + YMarginPx;   // activation band: card +- 1 cm
        bool onCardY = y >= CardTop && y <= CardBottom;
        bool onMonitorX = x < MonitorRight;
        int fromRight = MonitorRight - x;                       // 1 = touching the edge
        bool touching = onMonitorY && onMonitorX && fromRight <= 1;
        bool inPeek = onMonitorY && onMonitorX && fromRight <= PeekPx;
        bool overPanel = onCardY && onMonitorX && fromRight <= PanelPx;

        switch (State)
        {
            case EdgeState.Hidden:
                if (touching) Go(EdgeState.Open, x, y);
                else if (inPeek) Go(EdgeState.Peek, x, y);
                break;
            case EdgeState.Peek:
                if (touching) Go(EdgeState.Open, x, y);
                else if (!inPeek) Go(EdgeState.Hidden, x, y);
                break;
            case EdgeState.Open:
                if (overPanel) _leftPanelAt = 0;
                else if (_leftPanelAt == 0) _leftPanelAt = Stopwatch.GetTimestamp();
                else if (Stopwatch.GetElapsedTime(_leftPanelAt).TotalMilliseconds >= HideDelayMs) { _leftPanelAt = 0; Go(EdgeState.Hidden, x, y); }
                break;
        }
    }

    void Go(EdgeState next, int x, int y)
    {
        bool buttonHeld = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        string why = $"x={x} y={y} lbutton={buttonHeld}";
        if (State == EdgeState.Hidden && next != EdgeState.Hidden && IsFullscreenBusy(out var qs) && !buttonHeld)
        {
            if (_suppressedLog++ % 100 == 0) Log?.Invoke($"suppressed {next}: fullscreen/busy ({qs}) {why}");
            return;
        }
        State = next;
        _leftPanelAt = 0;
        StateChanged?.Invoke(next, why);
    }
    int _suppressedLog;

    // QUNS_BUSY=2, QUNS_RUNNING_D3D_FULL_SCREEN=3, QUNS_PRESENTATION_MODE=4
    public static bool IsFullscreenBusy(out int state)
    {
        state = 0;
        return SHQueryUserNotificationState(out state) == 0 && state is 2 or 3 or 4;
    }
}
