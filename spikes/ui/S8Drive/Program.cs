using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Automated S8 driver: starts S8Edge, then uses SendInput to run real OLE drag-and-drop scenarios between its own
// WinForms forms (source / accept / reject) and the S8Edge panel, and checks the S8Edge log.
// Usage: S8Drive.exe <path to S8Edge.exe>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        return new Driver(args[0]).Run();
    }
}

sealed class Driver
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    readonly string _exe, _log, _reportPath;
    readonly Rectangle _scr = Screen.PrimaryScreen!.Bounds, _wa = Screen.PrimaryScreen!.WorkingArea;
    readonly List<string> _report = new();
    Form _src = null!, _accept = null!, _reject = null!;
    volatile DataObject? _pending;
    string _lastEffect = "";
    readonly List<string> _accepted = new();
    int _fail;
    string _lastAllowed = "";

    public Driver(string exe)
    {
        _exe = exe;
        _log = Path.Combine(Path.GetDirectoryName(exe)!, "s8.log");
        _reportPath = Path.Combine(Path.GetDirectoryName(exe)!, "s8-drive-report.txt");
    }

    public int Run()
    {
        File.Delete(_log);
        var app = Process.Start(_exe)!;
        for (int i = 0; i < 100 && !(File.Exists(_log) && Read().Contains("ready; edge watcher running")); i++) Thread.Sleep(100);
        Rep($"S8Edge started, log: {Read().Replace("\n", " | ")}");
        SHQueryUserNotificationState(out int qs);
        Rep($"SHQueryUserNotificationState now = {qs} (5 = QUNS_ACCEPTS_NOTIFICATIONS, normal desktop)");

        _src = MakeForm("SOURCE", 150, 250, Color.SteelBlue);
        _accept = MakeForm("ACCEPT (Copy)", 150, 500, Color.SeaGreen);
        _reject = MakeForm("REJECT (None)", 150, 750, Color.IndianRed);
        _src.MouseDown += (_, e) =>
        {
            var d = _pending; _pending = null;
            if (d == null) return;
            var eff = _src.DoDragDrop(d, DragDropEffects.Copy | DragDropEffects.Move);
            _lastEffect = eff.ToString();
        };
        _accept.AllowDrop = _reject.AllowDrop = true;
        _accept.DragEnter += (_, e) => { _lastAllowed = e.AllowedEffect.ToString(); e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
        _accept.DragOver += (_, e) => e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        _accept.DragDrop += (_, e) => _accepted.AddRange((string[])e.Data!.GetData(DataFormats.FileDrop)!);
        _reject.DragEnter += (_, e) => e.Effect = DragDropEffects.None;
        _reject.DragOver += (_, e) => e.Effect = DragDropEffects.None;

        _src.Shown += (_, _) => Task.Run(() =>
        {
            try
            {
                Thread.Sleep(800);
                Scenarios();
            }
            catch (Exception ex) { Rep("DRIVER EXCEPTION: " + ex); _fail++; }
            finally { _src.BeginInvoke(() => Application.Exit()); }
        });
        _accept.Show(); _reject.Show();
        Application.Run(_src);

        Rep("---- full S8Edge log ----");
        _report.AddRange(Read().Split('\n'));
        File.WriteAllLines(_reportPath, _report);
        foreach (var l in _report) Console.WriteLine(l);
        try { app.Kill(); } catch { }
        return _fail;
    }

    Form MakeForm(string title, int x, int y, Color c)
    {
        var f = new Form { Text = title, StartPosition = FormStartPosition.Manual, Location = new Point(x, y), Size = new Size(300, 150), TopMost = true, BackColor = c, FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = false };
        f.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Enabled = false });
        return f;
    }

    // ---- scenarios ----
    void Scenarios()
    {
        int W = _scr.Right, H = _scr.Top + _scr.Height / 2;   // card is centred vertically on the primary monitor
        var tmp = Path.Combine(Path.GetTempPath(), "S8Src");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "a.txt"), "aaa"); File.WriteAllText(Path.Combine(tmp, "b.txt"), "bbbbbb");

        // T1: edge state machine without drag
        int m = Mark();
        MoveTo(W - 300, H, 200); Thread.Sleep(300);
        MoveTo(W - 20, H, 300); Thread.Sleep(400);
        MoveTo(W - 1, H, 150); Thread.Sleep(600);
        MoveTo(W - 700, H, 200); Thread.Sleep(900);
        var t1 = Since(m);
        Check("T1 edge: Hidden->Peek->Open->Hidden", InOrder(t1, "EDGE -> Peek", "EDGE -> Open", "EDGE -> Hidden"), t1);

        // T1b: touching the edge OUTSIDE the card band (card +- 1 cm) must do nothing
        m = Mark();
        MoveTo(W - 300, H - 234 - 90, 200); Thread.Sleep(200);
        MoveTo(W - 1, H - 234 - 90, 300); Thread.Sleep(500);
        MoveTo(W - 300, H, 300); Thread.Sleep(900);
        var t1b = Since(m);
        Check("T1b edge outside the card's Y band: no Peek/Open", !t1b.Any(l => l.Contains("EDGE -> Peek") || l.Contains("EDGE -> Open")), t1b);

        // T2: OLE drag of real files from our source form to the edge, drop onto the panel
        m = Mark();
        var files = new DataObject();
        files.SetData(DataFormats.FileDrop, new[] { Path.Combine(tmp, "a.txt"), Path.Combine(tmp, "b.txt") });
        DragToPanel(files, W, H, out var effect);
        var t2 = Since(m);
        Check("T2 hook sees moves during OLE drag + panel opens with LMB held",
              t2.Any(l => l.Contains("EDGE -> Peek") && l.Contains("lbutton=True")) && t2.Any(l => l.Contains("EDGE -> Open") && l.Contains("lbutton=True")), t2);
        Check("T2 drop of CF_HDROP files accepted by WinUI (StorageItems)", t2.Any(l => l.Contains("shelf += a.txt")) && t2.Any(l => l.Contains("shelf += b.txt")), t2);
        Rep($"  source DoDragDrop returned: {effect}");

        // T3: virtual file (CF_FILEDESCRIPTORW + CF_FILECONTENTS), as from a browser / messenger
        m = Mark();
        var content = Encoding.UTF8.GetBytes("virtual file content 0123456789");
        DragToPanel(MakeVirtual("virt.txt", content), W, H, out effect);
        var t3 = Since(m);
        Check("T3 virtual file read via native IDataObject (proxy)", t3.Any(l => l.Contains("read=" + content.Length)) && t3.Any(l => l.Contains("shelf += virt.txt")), t3);
        Rep($"  source DoDragDrop returned: {effect}");

        // T4: drag ALL out of the shelf to the accepting form, shelf must be emptied on success
        m = Mark();
        _accepted.Clear();
        DragOut(W, H, _accept, out effect);
        var t4 = Since(m);
        Check("T4a drag-out to accepting target: DropResult=Copy, files delivered", t4.Any(l => l.Contains("completed, DropResult=Copy")) && _accepted.Count >= 3, t4);
        Rep($"  accepted files: {string.Join(", ", _accepted.Select(Path.GetFileName))}");

        // T5: refill, drag out to the rejecting form, shelf must keep its items
        m = Mark();
        DragToPanel(files, W, H, out _);
        Check("T5 refill: files dropped on shelf again", Since(m).Any(l => l.Contains("shelf += a.txt")), Since(m));
        m = Mark();
        DragOut(W, H, _reject, out effect);
        var t5 = Since(m);
        Check("T5 drag-out to rejecting target: DropResult=None and shelf kept (count=2)", t5.Any(l => l.Contains("completed, DropResult=None")) && t5.Any(l => l.Contains("shelf count=2")), t5);
        MoveTo(W - 300, H, 300); Thread.Sleep(200);
        MoveTo(W - 1, H, 200); Thread.Sleep(900);   // the panel went Hidden after the drag: reopen it
        m = Mark();
        _accepted.Clear();
        DragOut(W, H, _accept, out effect);
        var t6 = Since(m);
        Check("T5b items still on shelf after rejected drop (second drag-out delivers them)", _accepted.Count == 2 && t6.Any(l => l.Contains("starting, 2 items")), t6);
        // T7: drag-out must be COPY only, a [reference] original (outside temp) must stay in place
        var refFile = Path.Combine(AppContext.BaseDirectory, "ref-original.txt");
        File.WriteAllText(refFile, "original must survive");
        var refData = new DataObject();
        refData.SetData(DataFormats.FileDrop, new[] { refFile });
        DragToPanel(refData, W, H, out effect);
        m = Mark();
        _lastAllowed = "";
        _accepted.Clear();
        DragOut(W, H, _accept, out effect);
        var t8 = Since(m);
        Check("T7 drag-out is Copy-only (AllowedEffect), DropResult=Copy, [reference] original still exists",
              _lastAllowed == "Copy" && t8.Any(l => l.Contains("completed, DropResult=Copy")) && File.Exists(refFile), t8);
        Rep($"  target saw AllowedEffect={_lastAllowed}");

        // T6: Chrome-like source - CF_HDROP (temp file) AND virtual file at once: one item, materialised into Shelf
        m = Mark();
        var tempFile = Path.Combine(tmp, "chrome-temp.jpg");
        File.WriteAllBytes(tempFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        var both = MakeVirtual("Untitled.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5 });
        both.SetData(DataFormats.FileDrop, new[] { tempFile });
        DragToPanel(both, W, H, out effect);
        var t7 = Since(m);
        Check("T6 hdrop+virtual at once: exactly one item, taken from the virtual file, materialised in SideBar\\Shelf",
              t7.Count(l => l.Contains("shelf += ")) == 1 && t7.Any(l => l.Contains("shelf += Untitled.jpg") && l.Contains("where=Shelf") && l.Contains("[virtual]")), t7);
        MoveTo(W - 700, H, 200); Thread.Sleep(700);
    }

    void DragToPanel(DataObject data, int W, int H, out string effect)
    {
        _lastEffect = "";
        _pending = data;
        MoveTo(_src.Left + 150, _src.Top + 75, 250); Thread.Sleep(200);
        Mouse(0x0002);                                   // LMB down -> DoDragDrop starts in MouseDown
        Thread.Sleep(250);
        MoveTo(_src.Left + 170, _src.Top + 90, 100);
        MoveTo(W - 300, H, 400);
        MoveTo(W - 30, H, 300); Thread.Sleep(300);   // peek zone
        int m = Mark();
        MoveTo(W - 1, H, 200); Thread.Sleep(1000);   // touch the edge -> open
        if (!Since(m).Any(l => l.Contains("EDGE -> Open")) && !LastEdgeIsOpen())
        {
            keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0);   // Esc: cancel instead of dropping on whatever is below
            Mouse(0x0004); Thread.Sleep(500); effect = "CANCELLED (panel did not open)"; return;
        }
        MoveTo(W - 116, H - 114, 300); Thread.Sleep(500);   // over DropZone
        Mouse(0x0004);                                   // LMB up -> drop
        Thread.Sleep(1800);
        effect = _lastEffect;
    }

    void DragOut(int W, int H, Form target, out string effect)
    {
        _lastEffect = "";
        MoveTo(W - 116, H + 18, 300); Thread.Sleep(500);  // "Drag ALL" bar (top of the stack tile)
        Mouse(0x0002); Thread.Sleep(200);
        MoveTo(W - 100, H + 28, 100); MoveTo(W - 140, H + 33, 100); Thread.Sleep(300);
        MoveTo(target.Left + 150, target.Top + 75, 700); Thread.Sleep(500);
        Mouse(0x0004); Thread.Sleep(1500);
        effect = _lastEffect;
    }

    static DataObject MakeVirtual(string name, byte[] content)
    {
        var fgd = new byte[4 + 592];
        BitConverter.GetBytes(1).CopyTo(fgd, 0);
        BitConverter.GetBytes(0x40).CopyTo(fgd, 4);                                  // FD_FILESIZE
        BitConverter.GetBytes((uint)content.Length).CopyTo(fgd, 4 + 68);            // nFileSizeLow
        Encoding.Unicode.GetBytes(name).CopyTo(fgd, 4 + 72);                         // cFileName
        var d = new DataObject();
        d.SetData("FileGroupDescriptorW", new MemoryStream(fgd));
        d.SetData("FileContents", new MemoryStream(content));
        return d;
    }

    // ---- input ----
    void Mouse(uint flags)
    {
        var i = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    void MoveTo(int x, int y, int ms)
    {
        GetCursorPos(out var p);
        int steps = Math.Max(1, ms / 8);
        for (int s = 1; s <= steps; s++)
        {
            int cx = p.X + (x - p.X) * s / steps, cy = p.Y + (y - p.Y) * s / steps;
            var i = new INPUT
            {
                type = 0,
                mi = new MOUSEINPUT { dx = (int)((long)cx * 65535 / (_scr.Width - 1)), dy = (int)((long)cy * 65535 / (_scr.Height - 1)), dwFlags = 0x8001 }
            };
            SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(8);
        }
    }

    // ---- log helpers ----
    string Read() { try { using var fs = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var sr = new StreamReader(fs); return sr.ReadToEnd(); } catch { return ""; } }
    bool LastEdgeIsOpen() => Read().Split((char)10).Where(l => l.Contains("EDGE ->")).LastOrDefault()?.Contains("-> Open") == true;
    int Mark() => Read().Split('\n').Length;
    List<string> Since(int mark) => Read().Split('\n').Skip(mark - 1).Where(l => l.Length > 0).ToList();
    static bool InOrder(List<string> lines, params string[] keys)
    {
        int k = 0;
        foreach (var l in lines) if (k < keys.Length && l.Contains(keys[k])) k++;
        return k == keys.Length;
    }
    void Check(string name, bool ok, List<string> lines)
    {
        Rep((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _fail++;
        foreach (var l in lines.Where(l => !l.Contains("hook moves"))) Rep("    " + l);
    }
    void Rep(string s) { lock (_report) _report.Add(s); }
}
