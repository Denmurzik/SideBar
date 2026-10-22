using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace S7Panel;

public sealed partial class MainWindow : Window
{
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(nint hProcess);
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(nint h, nint min, nint max);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private readonly string _log = Path.Combine(AppContext.BaseDirectory, "s7.log");
    private readonly Stopwatch _showWatch = new();
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();
    private bool _awaitFrame;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    public void Start()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var area = DisplayArea.Primary.WorkArea;
        var dpi = (double)GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(340 * dpi);
        AppWindow.MoveAndResize(new(area.X + area.Width - w, area.Y, w, area.Height));

        CompositionTarget.Rendering += OnRendering;

        var proto = ProtoCheck();
        Info.Text = proto;
        Log("start; " + proto);
        AppWindow.Show(false);

        StartCommandListener();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);

    private static string ProtoCheck()
    {
        var p = new Proto.Ping { Name = "hello", Id = 42 };
        p.Chunks.Add(Google.Protobuf.ByteString.CopyFrom(new byte[] { 1, 2, 3 }));
        p.Meta["k"] = "v";
        var bytes = Google.Protobuf.MessageExtensions.ToByteArray(p);
        var back = Proto.Ping.Parser.ParseFrom(bytes);
        return $"protobuf ok={back.Name == "hello" && back.Id == 42 && back.Chunks.Count == 1 && back.Meta["k"] == "v"} bytes={bytes.Length}";
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_awaitFrame) return;
        _awaitFrame = false;
        Log($"first-frame {_showWatch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private void StartCommandListener()
    {
        // Commands arrive through named events so the measuring script can drive the window.
        foreach (var name in new[] { "show", "hide", "exit" })
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "S7Panel_" + name);
            var cmd = name;
            var t = new Thread(() =>
            {
                while (true)
                {
                    ev.WaitOne();
                    _dq.TryEnqueue(() => Execute(cmd));
                }
            }) { IsBackground = true };
            t.Start();
        }
    }

    private void Execute(string cmd)
    {
        switch (cmd)
        {
            case "show":
                _showWatch.Restart();
                _awaitFrame = true;
                AppWindow.Show(true);
                break;
            case "hide":
                AppWindow.Hide();
                var h = Process.GetCurrentProcess().Handle;
                EmptyWorkingSet(h);
                SetProcessWorkingSetSize(h, -1, -1);
                Log("hidden+trimmed");
                break;
            case "exit":
                Environment.Exit(0);
                break;
        }
    }

    private void Log(string s) => File.AppendAllText(_log, $"{DateTime.Now:HH:mm:ss.fff} {s}\n");
}
