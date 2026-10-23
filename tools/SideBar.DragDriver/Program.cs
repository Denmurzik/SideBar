using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

// Автотест полки (M2): запускает опубликованный SideBar.exe с изолированным SIDEBAR_DATA_DIR, через SendInput гонит
// настоящие OLE-drag'и из собственных WinForms-окон на карточку и проверяет shelf.json и файлы в Shelf\.
// Строки PASS/FAIL — в консоль и в %TEMP%\SideBarDrive\report.txt. Код выхода = число FAIL.
// Usage: SideBar.DragDriver.exe [путь к SideBar.exe]   (по умолчанию artifacts\app\SideBar.exe рядом с SideBar.slnx)
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        return new Driver(args.Length > 0 ? args[0] : FindApp()).Run();
    }

    static string FindApp()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SideBar.slnx"))) return Path.Combine(d.FullName, "artifacts", "app", "SideBar.exe");
        return Path.GetFullPath(Path.Combine("artifacts", "app", "SideBar.exe"));
    }
}

sealed record ShelfEntry(Guid Id, string Name, string Path, int Kind);

sealed class Driver
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);

    const int Reference = 0, Owned = 1;

    readonly string _exe;
    readonly string _work = Path.Combine(Path.GetTempPath(), "SideBarDrive");
    readonly string _dataDir, _shelfJson, _shelfRoot, _reportPath, _appLog;
        readonly string _srcDir = Path.Combine(AppContext.BaseDirectory, "drive-src");
    readonly Rectangle _scr = Screen.PrimaryScreen!.Bounds;
    readonly List<string> _report = [];
    Form _src = null!, _other = null!;
    volatile object? _pending;
    string _lastLog = "";
    string _lastEffect = "";
    int _fail;
    Process? _app;
    double _scale = 1;

    public Driver(string exe)
    {
        _exe = Path.GetFullPath(exe);
        _dataDir = Path.Combine(_work, "data");
        _shelfJson = Path.Combine(_dataDir, "shelf.json");
        _shelfRoot = Path.Combine(_dataDir, "Shelf");
        _reportPath = Path.Combine(_work, "report.txt");
        _appLog = Path.Combine(_dataDir, "log", "sidebar.log");   // лог изолирован SIDEBAR_DATA_DIR: настоящий %TEMP%\SideBar\sidebar.log не трогаем
    }

    public int Run()
    {
        GetCursorPos(out var cursor);
        try
        {
            if (!File.Exists(_exe)) { Rep($"FAIL SideBar.exe not found: {_exe}"); _fail++; return Finish(); }
            if (Process.GetProcessesByName("SideBar").Length > 0)
            {
                Rep("FAIL SideBar.exe is already running (not started by the driver); close it first (single-instance mutex)");
                _fail++;
                return Finish();
            }
            Directory.CreateDirectory(_work);
            if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true);
            Directory.CreateDirectory(_srcDir);
            if (!StartApp()) { _fail++; return Finish(); }

            _src = MakeForm("SOURCE", 150, 250, Color.SteelBlue);
            _other = MakeForm("OTHER", 150, 500, Color.SeaGreen);
            _src.MouseDown += (_, _) =>
            {
                var d = _pending; _pending = null;
                if (d == null) return;
                _lastEffect = _src.DoDragDrop(d, DragDropEffects.Copy | DragDropEffects.Move).ToString();
            };
            _src.Shown += (_, _) => Task.Run(() =>
            {
                try { Thread.Sleep(800); _scale = _src.DeviceDpi / 96.0; Scenarios(); }
                catch (Exception ex) { Rep("FAIL driver exception: " + ex); _fail++; }
                finally { _src.BeginInvoke(() => Application.Exit()); }
            });
            _other.Show();
            Application.Run(_src);
        }
        finally
        {
            StopApp();
            SetCursorPos(cursor.X, cursor.Y);
            try { Directory.Delete(_srcDir, true); } catch { }
        }
        return Finish();
    }

    int Finish()
    {
        Rep(_fail == 0 ? "ALL PASS" : $"{_fail} FAIL");
        try { Directory.CreateDirectory(_work); File.WriteAllLines(_reportPath, _report); } catch { }
        foreach (var l in _report) Console.WriteLine(l);
        return _fail;
    }

    Form MakeForm(string title, int x, int y, Color c)
    {
        var f = new Form { Text = title, StartPosition = FormStartPosition.Manual, Location = new Point(x, y), Size = new Size(300, 150), TopMost = true, BackColor = c, FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = false };
        f.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Enabled = false });
        return f;
    }

    // ---- приложение ----
    bool StartApp()
    {
        try { File.Delete(_appLog); } catch { }
        var psi = new ProcessStartInfo(_exe) { UseShellExecute = false };
        psi.Environment["SIDEBAR_DATA_DIR"] = _dataDir;
        _app = Process.Start(psi)!;
        for (int i = 0; i < 150 && !AppLog().Contains(" ready"); i++)
        {
            if (_app.HasExited) break;
            Thread.Sleep(100);
        }
        bool ok = !_app.HasExited && AppLog().Contains(" ready");
        Rep((ok ? "PASS " : "FAIL ") + "app started and drop target registered");
        if (!ok) Rep("    " + AppLog().Replace("\n", " | "));
        return ok;
    }

    void StopApp()
    {
        try { if (_app is { HasExited: false }) { _app.Kill(true); _app.WaitForExit(5000); } } catch { }
    }

    // ---- сценарии ----
    void Scenarios()
    {
        int W = _scr.Right, H = _scr.Top + _scr.Height / 2;
        var tmpDir = Path.GetTempPath();
        string F(string name, string text) { var p = Path.Combine(_srcDir, name); File.WriteAllText(p, text); return p; }
        var a = F("a.txt", "aaa"); var b = F("b.txt", "bbbbbb"); var c = F("c.txt", "cccccccc");
        var refFile = F("ref-original.txt", "original must survive");
        if (Path.GetFullPath(_srcDir).StartsWith(Path.GetFullPath(tmpDir), StringComparison.OrdinalIgnoreCase))
        { Rep("FAIL source files must be outside %TEMP% (they would count as temporary)"); _fail++; return; }

        // T2: несколько обычных файлов одним drop'ом -> ссылки
        var files = new DataObject();
        files.SetData(DataFormats.FileDrop, new[] { a, b, c });
        DragToPanel(files, W, H);
        var s = WaitShelf(x => x.Count == 3);
        Check("T2 three files in one drop -> three [reference] items, originals untouched",
            s.Count == 3 && s.All(x => x.Kind == Reference) && Same(s.Select(x => x.Path), a, b, c) && File.Exists(a) && File.Exists(b) && File.Exists(c), Dump(s));

        // T2b: тот же файл ещё раз -> уникальное отображаемое имя
        var again = new DataObject();
        again.SetData(DataFormats.FileDrop, new[] { a });
        DragToPanel(again, W, H);
        s = WaitShelf(x => x.Count == 4);
        Check("T2b duplicate a.txt -> second [reference] item named 'a (2).txt'",
            s.Count == 4 && s[3].Name == "a (2).txt" && s[3].Kind == Reference && s[3].Path.Equals(a, StringComparison.OrdinalIgnoreCase) && File.Exists(a), Dump(s));

        // T3: виртуальный файл > 1 МиБ (потоковая запись кусками) -> Owned в Shelf
        var big = new byte[(1 << 20) * 5 / 2 + 123];
        new Random(1).NextBytes(big);
        DragToPanel(MakeVirtual("virt.bin", big), W, H);
        s = WaitShelf(x => x.Count == 5);
        var v = s.Count == 5 ? s[4] : null;
        Check("T3 virtual file -> [owned] item inside Shelf\\<guid>\\virt.bin, same bytes, no .part left",
            v != null && v.Kind == Owned && v.Name == "virt.bin" && InShelf(v.Path) && File.Exists(v.Path) && SameBytes(v.Path, big) && NoPartFiles(), Dump(s));

        // T6: как Chrome — CF_HDROP на temp-файл + виртуальный файл -> ровно один Owned, живёт после удаления temp
        var tempJpg = Path.Combine(tmpDir, "drive-chrome-temp.jpg");
        File.WriteAllBytes(tempJpg, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        var jpgBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var both = MakeVirtual("Untitled.jpg", jpgBytes);
        both.SetData(DataFormats.FileDrop, new[] { tempJpg });
        DragToPanel(both, W, H);
        s = WaitShelf(x => x.Count == 6);
        var t6 = s.Count == 6 ? s[5] : null;
        File.Delete(tempJpg);
        Thread.Sleep(300);
        Check("T6 hdrop(temp)+virtual -> exactly one [owned] item 'Изображение ....jpg' in Shelf, survives deleting the temp file",
            t6 != null && t6.Kind == Owned && t6.Name.StartsWith("Изображение ") && t6.Name.EndsWith(".jpg") && InShelf(t6.Path)
            && File.Exists(t6.Path) && SameBytes(t6.Path, jpgBytes) && s.Count == 6, Dump(s));

        // T8: как Проводник — CF_HDROP (не temp) + FileGroupDescriptorW -> один элемент-ссылка, Shelf не пополняется
        int dirsBefore = ShelfDirs();
        var explorerLike = MakeVirtual("explorer-virtual.txt", [1, 2, 3]);
        explorerLike.SetData(DataFormats.FileDrop, new[] { refFile });
        DragToPanel(explorerLike, W, H);
        s = WaitShelf(x => x.Count == 7);
        Check("T8 hdrop(non-temp)+virtual -> one [reference] item, nothing copied into Shelf",
            s.Count == 7 && s[6].Kind == Reference && s[6].Path.Equals(refFile, StringComparison.OrdinalIgnoreCase) && ShelfDirs() == dirsBefore, Dump(s));

        // T9: только временный CF_HDROP (без виртуального файла) -> копия в Shelf, синхронно внутри Drop
        var tempBin = Path.Combine(tmpDir, "drive-temp-only.bin");
        File.WriteAllBytes(tempBin, [9, 8, 7, 6, 5]);
        var tempOnly = new DataObject();
        tempOnly.SetData(DataFormats.FileDrop, new[] { tempBin });
        DragToPanel(tempOnly, W, H);
        s = WaitShelf(x => x.Count == 8);
        File.Delete(tempBin);
        var t9 = s.Count == 8 ? s[7] : null;
        Check("T9 temp-only hdrop -> [owned] copy in Shelf, original temp file may vanish",
            t9 != null && t9.Kind == Owned && t9.Name == "drive-temp-only.bin" && InShelf(t9.Path) && SameBytes(t9.Path, [9, 8, 7, 6, 5]), Dump(s));

        // T10: неподдерживаемое содержимое (только текст) -> DROPEFFECT_NONE, стопка не меняется
        var text = new DataObject();
        text.SetData(DataFormats.UnicodeText, "plain text");
        bool opened = DragToPanel(text, W, H);
        Thread.Sleep(500);
        s = ReadShelf();
        Check("T10 text-only drag -> rejected (effect None), shelf unchanged", opened && _lastEffect == "None" && s.Count == 8, $"effect={_lastEffect}; " + Dump(s));

        // T3b: FileContents как TYMED_ISTREAM (ветка потокового чтения кусками), 3.5 МиБ
        var streamBytes = new byte[(1 << 20) * 7 / 2 + 77];
        new Random(2).NextBytes(streamBytes);
        DragToPanel(new IStreamSource([("stream.bin", streamBytes, streamBytes.Length)]), W, H);
        s = WaitShelf(x => x.Count == 9);
        var st = s.Count == 9 ? s[8] : null;
        Check("T3b FileContents via TYMED_ISTREAM -> [owned] item, same SHA-256, app saw tymed 4",
            st != null && st.Kind == Owned && st.Name == "stream.bin" && InShelf(st.Path) && SameBytes(st.Path, streamBytes) && NoPartFiles()
            && _lastLog.Contains("FileContents tymed 4"), Dump(s) + "\n" + _lastLog);

        // T3c: один из файлов объявлен 3 ГиБ -> отклоняется весь drop, ничего не записано
        int dirs = ShelfDirs();
        bool opened2 = DragToPanel(new IStreamSource([("ok.bin", [1, 2, 3], 3), ("huge.bin", [4, 5, 6], 3L << 30)]), W, H);
        Thread.Sleep(500);
        s = ReadShelf();
        Check("T3c multi-file drop with one declared size over 2 GiB -> whole drop rejected, nothing written",
            opened2 && _lastEffect == "None" && s.Count == 9 && ShelfDirs() == dirs && NoPartFiles(), $"effect={_lastEffect}; " + Dump(s));

        // T8b: каталог -> элемент-ссылка
        var folder = Path.Combine(_srcDir, "folder1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");
        var dirDrop = new DataObject();
        dirDrop.SetData(DataFormats.FileDrop, new[] { folder });
        DragToPanel(dirDrop, W, H);
        s = WaitShelf(x => x.Count == 10);
        Check("T8b folder drop -> [reference] item to the directory, contents untouched",
            s.Count == 10 && s[9].Kind == Reference && s[9].Path.TrimEnd('\\').Equals(folder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder), Dump(s));

        // T11: перезапуск — стопка восстановлена из shelf.json, пропавшая ссылка выпала, осиротевший <guid> удалён
        var before = ReadShelf();
        MoveTo(W - 700, H, 200); Thread.Sleep(700);
        StopApp();
        var orphan = Path.Combine(_shelfRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "stale.part"), "x");
        var foreign = Path.Combine(_shelfRoot, "not-a-guid");
        Directory.CreateDirectory(foreign);
        File.Delete(c);
        bool restarted = StartApp();
        var after = ReadShelf();
        var expected = before.Where(x => !x.Path.Equals(c, StringComparison.OrdinalIgnoreCase)).ToList();
        Check("T11 restart: shelf restored from shelf.json (same ids/order), vanished reference pruned, owned files intact",
            restarted && after.Select(x => x.Id).SequenceEqual(expected.Select(x => x.Id)) && after.Count == 9
            && after.Where(x => x.Kind == Owned).All(x => File.Exists(x.Path)), $"before={before.Count} after={after.Count}\n" + Dump(after));
        Check("T11b restart: orphan Shelf\\<guid> removed, non-guid folder kept",
            !Directory.Exists(orphan) && Directory.Exists(foreign), $"orphanExists={Directory.Exists(orphan)} foreignExists={Directory.Exists(foreign)}");
        MoveTo(W - 700, H, 200); Thread.Sleep(500);

        // T12: битый shelf.json -> Shelf\<guid> не чистятся, файл уходит в shelf.bad.json, Owned-элементы восстановлены из папок
        var ownedBefore = ReadShelf().Where(x => x.Kind == Owned).Select(x => x.Path).ToList();
        int dirsBeforeBad = ShelfDirs();
        StopApp();
        File.WriteAllText(_shelfJson, "{ not json [[[");
        bool restarted2 = StartApp();
        var bad = Path.Combine(_dataDir, "shelf.bad.json");
        var rec = ReadShelf();
        bool recovered = RecoveredOk(rec, ownedBefore);
        Check("T12 corrupt shelf.json -> bad file kept as shelf.bad.json, Shelf folders intact, owned items recovered (Id = folder guid)",
            restarted2 && ownedBefore.Count > 0 && ShelfDirs() == dirsBeforeBad && File.Exists(bad) && File.ReadAllText(bad).Contains("not json") && recovered,
            $"owned={ownedBefore.Count} dirs={ShelfDirs()}/{dirsBeforeBad} bad={File.Exists(bad)}" + "\n" + Dump(rec));
        StopApp();
        bool restarted3 = StartApp();
        var rec2 = ReadShelf();
        Check("T12b second restart keeps the recovered items (same Ids)",
            restarted3 && RecoveredOk(rec2, ownedBefore) && rec2.Select(x => x.Id).SequenceEqual(rec.Select(x => x.Id)) && ShelfDirs() == dirsBeforeBad, Dump(rec2));
    }

    bool RecoveredOk(List<ShelfEntry> items, List<string> ownedPaths) =>
        items.Count == ownedPaths.Count && items.All(x => x.Kind == Owned && File.Exists(x.Path)
            && Guid.TryParse(Path.GetFileName(Path.GetDirectoryName(x.Path)), out var g) && g == x.Id)
        && Same(items.Select(x => x.Path), [.. ownedPaths]);

    // ---- проверки состояния ----
    List<ShelfEntry> ReadShelf()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(_shelfJson)) return [];
                using var doc = JsonDocument.Parse(File.ReadAllText(_shelfJson));
                return doc.RootElement.EnumerateArray().Select(e => new ShelfEntry(
                    e.GetProperty("Id").GetGuid(), e.GetProperty("DisplayName").GetString()!,
                    e.GetProperty("Path").GetString()!, e.GetProperty("Kind").GetInt32())).ToList();
            }
            catch (Exception e) when (e is IOException or JsonException) { Thread.Sleep(100); }
        }
        return [];
    }

    List<ShelfEntry> WaitShelf(Func<List<ShelfEntry>, bool> cond, int ms = 4000)
    {
        var s = ReadShelf();
        for (int t = 0; t < ms && !cond(s); t += 100) { Thread.Sleep(100); s = ReadShelf(); }
        return s;
    }

    bool InShelf(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(_shelfRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    int ShelfDirs() => Directory.Exists(_shelfRoot) ? Directory.GetDirectories(_shelfRoot).Length : 0;
    bool NoPartFiles() => !Directory.Exists(_shelfRoot) || !Directory.EnumerateFiles(_shelfRoot, "*.part", SearchOption.AllDirectories).Any();
    static bool Same(IEnumerable<string> paths, params string[] expected) =>
        paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).SequenceEqual(expected.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    static bool SameBytes(string path, byte[] expected) =>
        File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(expected));
    static string Dump(List<ShelfEntry> s) => string.Join("\n", s.Select(x => $"  [{(x.Kind == Owned ? "owned" : "reference")}] {x.Name} <- {x.Path}"));

    // ---- drag ----
    /// <summary>false (и FAIL в отчёте), если карточка не открылась и drag отменён.</summary>
    bool DragToPanel(object data, int W, int H)
    {
        _lastEffect = "";
        _pending = data;
        MoveTo(_src.Left + 150, _src.Top + 75, 250); Thread.Sleep(200);
        Mouse(0x0002);                                   // ЛКМ вниз -> DoDragDrop стартует в MouseDown
        Thread.Sleep(250);
        MoveTo(_src.Left + 170, _src.Top + 90, 100);
        MoveTo(W - 300, H, 400);
        MoveTo(W - 30, H, 300); Thread.Sleep(300);       // peek-зона
        MoveTo(W - 1, H, 200);                           // касание края -> Open
        for (int i = 0; i < 15 && !AppLog().Contains("edge Open"); i++) Thread.Sleep(100);
        if (!AppLog().Contains("edge Open"))
        {
            keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0);   // Esc: отмена вместо drop'а на то, что под курсором
            Mouse(0x0004); Thread.Sleep(500);
            Rep("FAIL card did not open; drag cancelled");
            _fail++;
            return false;
        }
        Thread.Sleep(700);                               // пружина успокаивается
        MoveTo(W - (int)((8 + 108) * _scale), H, 300); Thread.Sleep(400);   // центр карточки
        Mouse(0x0004);                                   // ЛКМ вверх -> drop
        Thread.Sleep(1200);
        MoveTo(W - 700, H, 200); Thread.Sleep(500);      // уходим: карточка закроется, следующий drag откроет её заново
        _lastLog = AppLog();
        try { File.Delete(_appLog); } catch { }          // чтобы "edge Open" в следующий раз был свежим
        return true;
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

    // ---- ввод ----
    static void Mouse(uint flags)
    {
        var i = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, [i], Marshal.SizeOf<INPUT>());
    }

    void MoveTo(int x, int y, int ms)
    {
        GetCursorPos(out var p);
        int steps = Math.Max(1, ms / 8);
        for (int st = 1; st <= steps; st++)
        {
            int cx = p.X + (x - p.X) * st / steps, cy = p.Y + (y - p.Y) * st / steps;
            var i = new INPUT
            {
                type = 0,
                mi = new MOUSEINPUT { dx = (int)((long)cx * 65535 / (_scr.Width - 1)), dy = (int)((long)cy * 65535 / (_scr.Height - 1)), dwFlags = 0x8001 }
            };
            SendInput(1, [i], Marshal.SizeOf<INPUT>());
            Thread.Sleep(8);
        }
    }

    // ---- отчёт ----
    string AppLog()
    {
        try
        {
            using var fs = new FileStream(_appLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    void Check(string name, bool ok, string detail)
    {
        Rep((ok ? "PASS " : "FAIL ") + name);
        if (!ok)
        {
            _fail++;
            foreach (var l in detail.Split('\n')) Rep("    " + l);
            Rep("    app log: " + AppLog().Replace("\n", " | "));
        }
    }

    void Rep(string s) { lock (_report) _report.Add(s); }
}

/// <summary>
/// IDataObject, отдающий FileContents как TYMED_ISTREAM (так делают браузеры и мессенджеры; WinForms DataObject отдаёт HGLOBAL).
/// Размер в дескрипторе задаётся отдельно, чтобы проверять отказ по заявленному размеру.
/// </summary>
sealed class IStreamSource : System.Runtime.InteropServices.ComTypes.IDataObject
{
    [DllImport("shlwapi.dll")] static extern nint SHCreateMemStream(byte[] init, uint cb);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint h);

    const int TYMED_HGLOBAL = 1, TYMED_ISTREAM = 4;
    const int DV_E_FORMATETC = unchecked((int)0x80040064);

    readonly (string Name, byte[] Data, long Declared)[] _files;
    readonly short _cfDescriptor = (short)RegisterClipboardFormatW("FileGroupDescriptorW");
    readonly short _cfContents = (short)RegisterClipboardFormatW("FileContents");

    public IStreamSource((string, byte[], long)[] files) => _files = files;

    public int QueryGetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC f)
    {
        if (f.cfFormat == _cfDescriptor && ((int)f.tymed & TYMED_HGLOBAL) != 0) return 0;
        if (f.cfFormat == _cfContents && ((int)f.tymed & TYMED_ISTREAM) != 0 && f.lindex >= 0 && f.lindex < _files.Length) return 0;
        return DV_E_FORMATETC;
    }

    public void GetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC f, out System.Runtime.InteropServices.ComTypes.STGMEDIUM m)
    {
        m = default;
        if (f.cfFormat == _cfDescriptor && ((int)f.tymed & TYMED_HGLOBAL) != 0)
        {
            var fgd = new byte[4 + 592 * _files.Length];
            BitConverter.GetBytes(_files.Length).CopyTo(fgd, 0);
            for (int i = 0; i < _files.Length; i++)
            {
                int o = 4 + i * 592;
                BitConverter.GetBytes(0x40).CopyTo(fgd, o);                                         // FD_FILESIZE
                BitConverter.GetBytes((uint)(_files[i].Declared >> 32)).CopyTo(fgd, o + 64);
                BitConverter.GetBytes((uint)_files[i].Declared).CopyTo(fgd, o + 68);
                Encoding.Unicode.GetBytes(_files[i].Name).CopyTo(fgd, o + 72);
            }
            nint h = GlobalAlloc(0x2, (nuint)fgd.Length);   // GMEM_MOVEABLE
            Marshal.Copy(fgd, 0, GlobalLock(h), fgd.Length);
            GlobalUnlock(h);
            m.tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL;
            m.unionmember = h;
            return;
        }
        if (f.cfFormat == _cfContents && ((int)f.tymed & TYMED_ISTREAM) != 0 && f.lindex >= 0 && f.lindex < _files.Length)
        {
            var data = _files[f.lindex].Data;
            m.tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM;
            m.unionmember = SHCreateMemStream(data, (uint)data.Length);
            return;
        }
        Marshal.ThrowExceptionForHR(DV_E_FORMATETC);
    }

    public void GetDataHere(ref System.Runtime.InteropServices.ComTypes.FORMATETC f, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM m) => throw new NotImplementedException();
    public int GetCanonicalFormatEtc(ref System.Runtime.InteropServices.ComTypes.FORMATETC a, out System.Runtime.InteropServices.ComTypes.FORMATETC b) { b = a; b.ptd = 0; return 0x40130; }
    public void SetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC f, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM m, bool release) => Marshal.ThrowExceptionForHR(DV_E_FORMATETC);
    public System.Runtime.InteropServices.ComTypes.IEnumFORMATETC EnumFormatEtc(System.Runtime.InteropServices.ComTypes.DATADIR d) => throw new NotImplementedException();
    public int DAdvise(ref System.Runtime.InteropServices.ComTypes.FORMATETC f, System.Runtime.InteropServices.ComTypes.ADVF a, System.Runtime.InteropServices.ComTypes.IAdviseSink s, out int c) { c = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int c) => throw new NotImplementedException();
    public int EnumDAdvise(out System.Runtime.InteropServices.ComTypes.IEnumSTATDATA e) { e = null!; return unchecked((int)0x80040003); }
}
