using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace S10Spring;

public sealed partial class MainWindow : Window
{
    enum PanelState { Hidden, Peek, Open }

    const double CardW = 216, CardH = 469, EdgeGap = 8, PeekVis = 10;   // epx
    const double Reserve = 200, PadV = 40;                              // transparent room left of the card (fan, spring overshoot) / above+below
    const int GWL_EXSTYLE = -20;
    const nint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80;
    const uint SWP_NOSIZE = 1, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int SetWindowRgn(nint hwnd, nint rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int l, int t, int r, int b);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmEnableBlurBehindWindow(nint hwnd, ref DWM_BLURBEHIND bb);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct DWM_BLURBEHIND { public uint Flags; public int Enable; public nint Rgn; public int Transition; }

    readonly string[] _args = App.Args;
    readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "s10.log");
    readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();
    nint _hwnd;
    double _dpi = 1;
    bool _modeB;
    string _clickThrough = "ex";
    double _zeta = 0.8, _periodMs = 60, _bResponse = 0.35;
    Visual _panelVisual = null!;
    Compositor _comp = null!;
    PanelState _state = PanelState.Hidden;
    int _gen;

    // Mode B spring state (physical px, window left edge)
    double _bPos, _bVel, _bTarget, _bT0, _bX0, _bV0;
    readonly List<double> _frames = new();
    long _lastTick;
    bool _ticking;
    int _screenRight, _winTop, _winHeight, _winWidth;
    int _clicks, _centerY, _cardW, _cardH;
    [DllImport("gdi32.dll")] static extern nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    string Opt(string name, string def)
    {
        int i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : def;
    }
    bool Flag(string name) => Array.IndexOf(_args, name) >= 0;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Start()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _modeB = Opt("--mode", "A").Equals("B", StringComparison.OrdinalIgnoreCase);
        _clickThrough = Opt("--ct", Opt("--mode", "A") == "B" ? "ex" : "rgn");
        var sp = Opt("--spring", "0.8,60").Split(',');
        _zeta = double.Parse(sp[0], inv);
        _periodMs = double.Parse(sp[1], inv);
        _bResponse = double.Parse(Opt("--bresp", "0.35"), inv);

        _hwnd = WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        int noRound = Opt("--mode", "A") == "B" && !Flag("--bregion") ? 2 : 1, noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(_hwnd, 33, ref noRound, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE: B = ROUND (smooth system 8 px), A = DONOTROUND
        DwmSetWindowAttribute(_hwnd, 34, ref noBorder, 4);

        if (Flag("--solid")) Fill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 0, 255));
        else if (Opt("--fill", "dark") == "none") Fill.Background = null;
        switch (Opt("--backdrop", "none"))
        {
            case "win-acrylic":
                SystemBackdrop = new DesktopAcrylicBackdrop();
                Fill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x20, 0x20, 0x20));
                break;
            case "transparent":
                TransparentBackdrop.Log = Log; SystemBackdrop = new TransparentBackdrop();
                break;
            case "win-mica":
                SystemBackdrop = new MicaBackdrop();
                Fill.Background = null;
                break;
            case "element":
                TransparentBackdrop.Log = Log; SystemBackdrop = new TransparentBackdrop();
                var be = new Microsoft.UI.Xaml.Controls.SystemBackdropElement { SystemBackdrop = new DesktopAcrylicBackdrop() };
                be.CornerRadius = new CornerRadius(8); Fill.Children.Insert(0, be);
                Fill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x20, 0x20, 0x20));
                break;
        }
        if (Flag("--popup"))
        {
            SetWindowLongPtr(_hwnd, -16, unchecked((nint)0x90000000));   // WS_POPUP | WS_VISIBLE
            SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | 0x20 | 2);   // FRAMECHANGED | NOMOVE
        }
        if (Flag("--bb"))
        {
            var bb = new DWM_BLURBEHIND { Flags = 3, Enable = 1, Rgn = CreateRectRgn(0, 0, -1, -1) };
            Log("blurbehind hr=" + DwmEnableBlurBehindWindow(_hwnd, ref bb));
        }

        _dpi = GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.Primary.OuterBounds;
        _screenRight = area.X + area.Width;
        _centerY = area.Y + area.Height / 2;
        _cardW = (int)Math.Round((CardW + EdgeGap) * _dpi);     // card + gap to the edge, physical px
        _cardH = (int)Math.Round(CardH * _dpi);
        if (_modeB) { _winWidth = (int)Math.Round(CardW * _dpi); _winHeight = _cardH; Panel.Margin = new Thickness(0); Panel.HorizontalAlignment = HorizontalAlignment.Stretch; }
        else { _winWidth = (int)Math.Round((CardW + EdgeGap + Reserve) * _dpi); _winHeight = (int)Math.Round((CardH + 2 * PadV) * _dpi); }
        _winTop = _centerY - _winHeight / 2;

        // own 1-physical-pixel border, thickness in epx so it stays crisp at 125/150 %
        Panel.BorderThickness = new Thickness(1.0 / _dpi);
        bool dark = Root.ActualTheme != ElementTheme.Light;
        Panel.BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x70, 0x8C, 0x8C, 0x8C) : Windows.UI.Color.FromArgb(0x50, 0x40, 0x40, 0x40));

        // all strokes are 1 physical pixel; dash lengths are in stroke-thickness units, i.e. physical px (4 on / 3 off)
        QsTile.BorderThickness = new Thickness(1.0 / _dpi);
        ShelfDash.StrokeThickness = 1.0 / _dpi;
        ShelfDash.Margin = new Thickness(0.5 / _dpi);   // stroke is centred on the geometry: inset half a physical pixel so none is clipped
        ShelfDash.Stroke = Panel.BorderBrush;
        QsTile.BorderBrush = Panel.BorderBrush;
        _panelVisual = ElementCompositionPreview.GetElementVisual(Panel);
        _comp = _panelVisual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(Panel, true);
        Info.Text = $"{(_modeB ? "B window move" : "A composition")}\nz={_zeta} T={_periodMs}ms dpi={_dpi:F2}";

        if (_modeB) { AppWindow.MoveAndResize(new(_screenRight, _winTop, _winWidth, _winHeight)); if (Flag("--bregion")) SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, _winWidth + 1, _winHeight + 1, (int)(16 * _dpi), (int)(16 * _dpi)), false); }
        else AppWindow.MoveAndResize(new(_screenRight - _winWidth, _winTop, _winWidth, _winHeight));
        _bPos = _screenRight; _bTarget = _screenRight;
        SetPanelOffset(_modeB ? 0 : CardW + EdgeGap);
        HideTrim();
        ApplyClickThrough(true);

        foreach (var name in new[] { "open", "peek", "close", "exit" })
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "S10_" + name);
            var cmd = name;
            new Thread(() => { while (true) { ev.WaitOne(); _dq.TryEnqueue(() => Command(cmd)); } }) { IsBackground = true }.Start();
        }
        if (Flag("--cursor")) StartCursorDriver();
        _dq.TryEnqueue(async () => { await Task.Delay(500); { AppWindow.Show(false); StripFrame(); } await Task.Delay(500); LogRects("shown"); HideTrim(); });
        Log("ready " + Info.Text.Replace("\n", " "));
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref POINT p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int a, out RECT r, int size);
    // The borderless presenter still has a 3 px WS_DLGFRAME non-client border (painted light under acrylic, and it
    // insets the XAML content). WM_NCCALCSIZE -> 0 makes the whole window rect the client area.
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(nint h, nint proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint h, uint msg, nint w, nint l);

    [UnmanagedCallersOnly]
    static nint NcProc(nint h, uint msg, nint w, nint l, nuint id, nuint data)
    {
        if (msg == 0x83 && w != 0) return 0;
        return DefSubclassProc(h, msg, w, l);
    }

    unsafe void StripFrame()
    {
        if (Flag("--keepframe") || _stripped) return;
        _stripped = true;
        SetWindowSubclass(_hwnd, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&NcProc, 1, 0);
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | 0x20 | 2);   // FRAMECHANGED | NOMOVE
    }
    bool _stripped;

    void LogRects(string tag)
    {
        GetWindowRect(_hwnd, out var w); GetClientRect(_hwnd, out var c);
        var p = new POINT(); ClientToScreen(_hwnd, ref p);
        DwmGetWindowAttribute(_hwnd, 9, out var e, 16);
        Log($"{tag} window=({w.L},{w.T},{w.R},{w.B}) client@({p.X},{p.Y}) size {c.R}x{c.B} dwmFrame=({e.L},{e.T},{e.R},{e.B}) style=0x{GetWindowLongPtr(_hwnd, -16):X}");
    }

    void OnClick(object sender, RoutedEventArgs e)
    {
        Clicks.Text = $"clicks: {++_clicks}";
        Log("button clicked");
    }

    void Command(string cmd)
    {
        switch (cmd)
        {
            case "open": SetState(PanelState.Open); break;
            case "peek": SetState(PanelState.Peek); break;
            case "close": SetState(PanelState.Hidden); break;
            case "exit": Environment.Exit(0); break;
        }
    }

    // Demo-only cursor driver; the real one is the WH_MOUSE_LL hook (S8). Zone: card height +- 1 cm.
    long _leftPanelAt;
    void StartCursorDriver()
    {
        var timer = _dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            GetCursorPos(out var p);
            double fromRight = _screenRight - p.X;
            double cm = 96 * _dpi / 2.54;
            bool inBand = Math.Abs(p.Y - _centerY) <= _cardH / 2.0 + cm;
            bool overCard = fromRight <= _cardW && Math.Abs(p.Y - _centerY) <= _cardH / 2.0;
            if (inBand && fromRight <= 1) { _leftPanelAt = 0; if (_state != PanelState.Open) SetState(PanelState.Open); }
            else if (_state == PanelState.Open)
            {
                if (!overCard)
                {
                    if (_leftPanelAt == 0) _leftPanelAt = Stopwatch.GetTimestamp();
                    else if (Stopwatch.GetElapsedTime(_leftPanelAt).TotalMilliseconds > 300) { _leftPanelAt = 0; SetState(PanelState.Hidden); }
                }
                else _leftPanelAt = 0;
            }
            else if (inBand && fromRight <= cm) { if (_state != PanelState.Peek) SetState(PanelState.Peek); }
            else if (_state == PanelState.Peek) SetState(PanelState.Hidden);
        };
        timer.Start();
    }

    double VisibleDip(PanelState s) => s switch { PanelState.Open => CardW + EdgeGap, PanelState.Peek => PeekVis, _ => 0 };

    void SetState(PanelState s)
    {
        if (s == _state) return;
        var prev = _state;
        _state = s;
        int gen = ++_gen;
        Log($"state {prev} -> {s}");
        double vis = VisibleDip(s);

        if (prev == PanelState.Hidden && vis > 0)
        {
            if (!_modeB) SetPanelOffset(CardW + EdgeGap);
            ApplyClickThrough(s != PanelState.Open);
            { AppWindow.Show(false); StripFrame(); }
        }
        ApplyClickThrough(s != PanelState.Open);

        if (_modeB) StartB(_screenRight - vis * _dpi);
        else { ApplyRegion(false); StartA(CardW + EdgeGap - vis, gen); }
    }

    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(nint h);
    void HideTrim()
    {
        AppWindow.Hide();
        if (Flag("--trim")) EmptyWorkingSet(Process.GetCurrentProcess().Handle);
    }

    void SetPanelOffset(double x) => _panelVisual.Properties.InsertVector3("Translation", new Vector3((float)x, 0, 0));

    void StartA(double finalX, int gen)
    {
        var spring = _comp.CreateSpringVector3Animation();
        spring.Target = "Translation";
        spring.FinalValue = new Vector3((float)finalX, 0, 0);
        spring.DampingRatio = (float)_zeta;
        spring.Period = TimeSpan.FromMilliseconds(_periodMs);
        var batch = _comp.CreateScopedBatch(CompositionBatchTypes.Animation);
        _panelVisual.StartAnimation("Translation", spring);
        batch.End();
        long t0 = Stopwatch.GetTimestamp();
        batch.Completed += (_, _) => _dq.TryEnqueue(() =>
        {
            if (gen != _gen) return;
            Log($"A settled after {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms (state {_state})");
            if (_state == PanelState.Hidden) HideTrim(); else ApplyRegion(true);
        });
        StartFrameProbe();
    }

    // Mode B: analytic damped spring on the window's left edge; retargeting keeps position and velocity.
    void StartB(double target)
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        StepB(now);
        _bTarget = target; _bT0 = now; _bX0 = _bPos - target; _bV0 = _bVel;
        _lastTick = Stopwatch.GetTimestamp();
        _frames.Clear();
        if (!_ticking) { _ticking = true; CompositionTarget.Rendering += OnTick; }
    }

    void StepB(double now)
    {
        double t = Math.Max(now - _bT0, 0);
        double w = 2 * Math.PI / _bResponse, z = Math.Min(_zeta, 0.999);
        double wd = w * Math.Sqrt(1 - z * z);
        double e = Math.Exp(-z * w * t);
        double c1 = _bX0, c2 = (_bV0 + z * w * _bX0) / wd;
        double cos = Math.Cos(wd * t), sin = Math.Sin(wd * t);
        _bPos = _bTarget + e * (c1 * cos + c2 * sin);
        _bVel = e * ((-z * w) * (c1 * cos + c2 * sin) + wd * (-c1 * sin + c2 * cos));
    }

    void OnTick(object? s, object e)
    {
        long ts = Stopwatch.GetTimestamp();
        _frames.Add(Stopwatch.GetElapsedTime(_lastTick, ts).TotalMilliseconds);
        _lastTick = ts;
        StepB(ts / (double)Stopwatch.Frequency);
        SetWindowPos(_hwnd, 0, (int)Math.Round(_bPos), _winTop, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (Math.Abs(_bPos - _bTarget) < 0.5 && Math.Abs(_bVel) < 5)
        {
            CompositionTarget.Rendering -= OnTick; _ticking = false;
            SetWindowPos(_hwnd, 0, (int)Math.Round(_bTarget), _winTop, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            LogFrames("B");
            if (_state == PanelState.Hidden) HideTrim();
        }
    }

    // UI-thread frame cadence during mode A animations (the animation itself runs on the compositor thread).
    bool _probing; long _probeStart;
    void StartFrameProbe()
    {
        _frames.Clear(); _lastTick = Stopwatch.GetTimestamp(); _probeStart = _lastTick;
        if (_probing) return;
        _probing = true;
        CompositionTarget.Rendering += OnProbe;
    }

    void OnProbe(object? s, object e)
    {
        long ts = Stopwatch.GetTimestamp();
        _frames.Add(Stopwatch.GetElapsedTime(_lastTick, ts).TotalMilliseconds);
        _lastTick = ts;
        if (Stopwatch.GetElapsedTime(_probeStart, ts).TotalMilliseconds > 900)
        {
            CompositionTarget.Rendering -= OnProbe; _probing = false;
            LogFrames("A-UI");
        }
    }

    void LogFrames(string tag)
    {
        if (_frames.Count < 3) return;
        var f = _frames.Skip(1).ToList();
        double mean = f.Average(), max = f.Max();
        var sorted = f.OrderBy(x => x).ToList();
        double p99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
        Log($"{tag} frames={f.Count} mean={mean:F2}ms p99={p99:F2}ms max={max:F2}ms late(>1.5x mean)={f.Count(x => x > mean * 1.5)}");
    }

    void ApplyClickThrough(bool through)
    {
        if (_clickThrough == "ex")
        {
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            ex |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            if (through) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, ex);
            SetLayeredWindowAttributes(_hwnd, 0, 255, 2);
        }
        else if (_clickThrough == "tr")
        {
            // WS_EX_TRANSPARENT without WS_EX_LAYERED (layered breaks per-pixel alpha of the composition surface)
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
            if (through) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, ex);
        }
        else if (_clickThrough == "key")
        {
            // black (the unpainted window background) becomes transparent AND hit-test transparent
            var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
            SetLayeredWindowAttributes(_hwnd, 0x000000, 0, 1);
        }
        else if (_clickThrough == "rgn" && !_modeB) ApplyRegion(false);
    }

    // Mode A: while the spring runs the region is the whole window (content must not be clipped); when settled it is
    // only the visible part of the card (Open) or the peeking strip (Peek) - the rest of the window is click-through.
    void ApplyRegion(bool settled)
    {
        if (_clickThrough != "rgn" || _modeB) return;
        if (!settled || _state == PanelState.Hidden) { SetWindowRgn(_hwnd, 0, true); return; }
        int top = (_winHeight - _cardH) / 2;
        int gap = (int)Math.Round(EdgeGap * _dpi), r = (int)(16 * _dpi);
        nint rgn = _state == PanelState.Open
            ? CreateRectRgn(_winWidth - _cardW, top, _winWidth - gap, top + _cardH)
            : CreateRectRgn(_winWidth - (int)(PeekVis * _dpi), top, _winWidth, top + _cardH);
        SetWindowRgn(_hwnd, rgn, true);
    }

    void Log(string s) => File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n");
}
