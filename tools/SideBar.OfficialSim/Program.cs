using System.Runtime.InteropServices;

namespace SideBar.OfficialSim;

// Режим берётся из SIDEBAR_SIM_MODE:
//   normal  — маркер через ~300 мс; WM_QUIT завершает процесс
//   hung    — как normal, но WM_QUIT игнорируется (выход по WM_CLOSE окну или по таймеру жизни)
//   user    — после маркера пишет "Received WM_COPYDATA" (пользователь «открыл» приложение)
//   rotate  — до маркера начинает новый nearby_share_<ts>.log и пишет маркер уже в него
//   silent  — маркер не пишет
//   show    — ДО маркера на ~600 мс показывает окно (SW_SHOWNOACTIVATE, вне экрана) и снова прячет
// Каталог данных — SIDEBAR_SHARING_DIR (обязателен: настоящие данные Google не трогаем). Жизнь ограничена SIDEBAR_SIM_LIFETIME_SEC (по умолчанию 90).
static unsafe class Program
{
    const string WindowClass = "Google.Windows.NearbyShare";
    const uint WM_CLOSE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", ExactSpelling = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint CreateWindowExW(uint ex, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll", ExactSpelling = true)] static extern int GetMessageW(out MSG m, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", ExactSpelling = true)] static extern int TranslateMessage(ref MSG m);
    [DllImport("user32.dll", ExactSpelling = true)] static extern nint DispatchMessageW(ref MSG m);
    [DllImport("user32.dll", ExactSpelling = true)] static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);

    [DllImport("user32.dll", ExactSpelling = true)] static extern int ShowWindow(nint hwnd, int cmd);

    static nint _hwnd;
    static string _mode = "normal";
    static string _logDir = "";
    static FileStream? _log;
    static readonly object Gate = new();

    static int Main()
    {
        _mode = Environment.GetEnvironmentVariable("SIDEBAR_SIM_MODE") ?? "normal";
        var sharing = Environment.GetEnvironmentVariable("SIDEBAR_SHARING_DIR");
        if (string.IsNullOrEmpty(sharing)) return 2;
        _logDir = Path.Combine(sharing, "Logs");
        Directory.CreateDirectory(_logDir);

        StartLog();
        Write("main.cc:414] We are the only instance, command line: ");

        fixed (char* cls = WindowClass)
        fixed (char* title = "NearbyShareSim")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                lpszClassName = cls,
            };
            if (RegisterClassExW(ref wc) == 0) return 3;
            // Окно верхнего уровня (WS_EX_TOOLWINDOW: без кнопки на панели задач даже в режиме show), но без ShowWindow: невидимо, как у настоящего приложения при SW_HIDE.
            if ((_hwnd = CreateWindowExW(0x80, cls, title, 0x00CF0000, -32000, -32000, 100, 100, 0, 0, 0, 0)) == 0) return 4;
        }

        var lifetime = int.TryParse(Environment.GetEnvironmentVariable("SIDEBAR_SIM_LIFETIME_SEC"), out var s) ? s : 90;
        new Thread(() => Script(lifetime)) { IsBackground = true }.Start();

        while (true)
        {
            var r = GetMessageW(out var msg, 0, 0, 0);
            if (r == -1) break;
            if (r == 0)
            {
                if (_mode == "hung") continue;   // WM_QUIT проглочен
                break;
            }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Write("[[ Exiting Nearby Share for Windows ]]");
        return 0;
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint m, nint w, nint l)
    {
        if (m == WM_CLOSE) Environment.Exit(0);
        return DefWindowProcW(h, m, w, l);
    }

    static void Script(int lifetimeSec)
    {
        if (_mode == "show")
        {
            ShowWindow(_hwnd, 4);   // IsWindowVisible == true, но окно за пределами экрана
            Thread.Sleep(600);
            ShowWindow(_hwnd, 0);
        }
        Thread.Sleep(300);
        if (_mode == "rotate") StartLog();
        if (_mode != "silent") Write("pub.cc:1] operator(): PublishDevice succeeded. contact_removed: false");
        if (_mode == "user")
        {
            Thread.Sleep(100);
            Write("main.cc:1] Received WM_COPYDATA");
        }
        Thread.Sleep(lifetimeSec * 1000);
        Environment.Exit(0);
    }

    // Лог держим открытым с FileShare.ReadWrite|Delete, как настоящее приложение.
    static void StartLog()
    {
        lock (Gate)
        {
            _log?.Dispose();
            var name = $"nearby_share_{DateTime.UtcNow:yyyy-MM-dd-HHmmss-fff}.log";
            _log = new FileStream(Path.Combine(_logDir, name), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    static void Write(string line)
    {
        lock (Gate)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"I{DateTime.UtcNow:MMdd HH:mm:ss.ffffff} {Environment.ProcessId} {line}\n");
            _log!.Write(bytes);
            _log.Flush();
        }
    }
}
