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
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);

    const int Reference = 0, Owned = 1;

    readonly string _exe;
    readonly string _work = Path.Combine(Path.GetTempPath(), "SideBarDrive");
    readonly string _dataDir, _shelfJson, _shelfRoot, _reportPath, _appLog;
        readonly string _srcDir = Path.Combine(AppContext.BaseDirectory, "drive-src");
    readonly Rectangle _scr = Screen.PrimaryScreen!.Bounds;
    readonly List<string> _report = [];
    Form _src = null!, _other = null!;
    volatile bool _otherRejects;
    volatile string[] _received = [];
    volatile string _otherEffect = "";
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
            _other.AllowDrop = true;
            _other.DragEnter += (_, e) => e.Effect = _otherRejects ? DragDropEffects.None : (e.AllowedEffect & DragDropEffects.Copy);
            _other.DragOver += (_, e) => e.Effect = _otherRejects ? DragDropEffects.None : (e.AllowedEffect & DragDropEffects.Copy);
            _other.DragDrop += (_, e) =>
            {
                _otherEffect = e.Effect.ToString();
                _received = e.Data?.GetData(DataFormats.FileDrop) as string[] ?? [];
            };
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
    /// <param name="defaultDeleteDelay">true — без SIDEBAR_OWNED_DELETE_DELAY_SEC (боевая задержка 10 мин); иначе короткая, чтобы D3c не ждал 10 минут.</param>
    bool StartApp(bool defaultDeleteDelay = false, int deleteDelaySec = 30)
    {
        try { File.Delete(_appLog); } catch { }
        var psi = new ProcessStartInfo(_exe) { UseShellExecute = false };
        psi.Environment["SIDEBAR_DATA_DIR"] = _dataDir;
        psi.Environment["SIDEBAR_NEARBY_ONLY_NAME"] = "SidebarE2E";   // Debug: в плитке только устройство e2e, настоящий телефон не виден
        psi.Environment["SIDEBAR_NEARBY_INCLUDE_LOCAL"] = "1";   // только Debug-сборка: loopback e2e видит сервисы этой машины; Release игнорирует
        if (defaultDeleteDelay) psi.Environment.Remove("SIDEBAR_OWNED_DELETE_DELAY_SEC");
        else psi.Environment["SIDEBAR_OWNED_DELETE_DELAY_SEC"] = deleteDelaySec.ToString();
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

        if (Environment.GetEnvironmentVariable("SIDEBAR_DRIVER_CLEAR_ONLY") == "1") { ClearScenarios(W, H, a, b, c); return; }   // быстрый прогон: только корзина полки (C1..C3)
        if (Environment.GetEnvironmentVariable("SIDEBAR_DRIVER_SHARE_ONLY") == "1" || Environment.GetEnvironmentVariable("SIDEBAR_DRIVER_FAN_ONLY") == "1")   // быстрый прогон: только вынос стопки и отправка с плитки
        {
            var quickFolder = Path.Combine(_srcDir, "folder1");
            Directory.CreateDirectory(quickFolder);
            File.WriteAllText(Path.Combine(quickFolder, "inside.txt"), "x");
            DragOutScenarios(W, H, a, refFile, quickFolder);
            return;
        }

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

        // T12: битый shelf.json -> Shelf\<guid> не чистятся, файл уходит в shelf.bad-<время>.json, Owned-элементы восстановлены из папок
        var ownedBefore = ReadShelf().Where(x => x.Kind == Owned).Select(x => x.Path).ToList();
        int dirsBeforeBad = ShelfDirs();
        StopApp();
        File.WriteAllText(_shelfJson, "{ not json [[[");
        bool restarted2 = StartApp();
        var bad = Directory.GetFiles(_dataDir, "shelf.bad-*.json").FirstOrDefault() ?? "";
        var rec = ReadShelf();
        bool recovered = RecoveredOk(rec, ownedBefore);
        Check("T12 corrupt shelf.json -> bad file kept as shelf.bad-<timestamp>.json, Shelf folders intact, owned items recovered (Id = folder guid)",
            restarted2 && ownedBefore.Count > 0 && ShelfDirs() == dirsBeforeBad && bad != "" && File.Exists(bad) && File.ReadAllText(bad).Contains("not json") && recovered,
            $"owned={ownedBefore.Count} dirs={ShelfDirs()}/{dirsBeforeBad} bad={bad != ""}" + "\n" + Dump(rec));
        StopApp();
        bool restarted3 = StartApp();
        var rec2 = ReadShelf();
        Check("T12b second restart keeps the recovered items (same Ids)",
            restarted3 && RecoveredOk(rec2, ownedBefore) && rec2.Select(x => x.Id).SequenceEqual(rec.Select(x => x.Id)) && ShelfDirs() == dirsBeforeBad, Dump(rec2));
        DragOutScenarios(W, H, a, refFile, folder);
        ShareScenarios(W, H, a);
    }

    // ---- отправка из плитки Quick Share (M3 Task 5) ----
    bool WaitLog(string needle, int ms)
    {
        for (int t = 0; t < ms && !AppLog().Contains(needle); t += 100) Thread.Sleep(100);
        return AppLog().Contains(needle);
    }

    void ShareScenarios(int W, int H, string a)
    {
        var before = ReadShelf();
        var files = new DataObject();
        files.SetData(DataFormats.FileDrop, new[] { a });
        bool dropped = DragToPanel(files, W, H, QsTile(W, H));
        var log = _lastLog;
        Rep("INFO Q1 log: " + log.Replace("\n", " | "));
        Check("Q1 foreign file drop on the Quick Share tile -> tile Searching, send starts, shelf untouched",
            dropped && log.Contains("share tile Searching") && log.Contains("share: searching") && log.Contains("share: 1 file(s)") && ReadShelf().Select(x => x.Id).SequenceEqual(before.Select(x => x.Id)), log);
        MoveTo(_other.Left + 150, _other.Top + 75, 400); Thread.Sleep(2500);   // карточка держится открытой: поиск идёт
        Check("Q1a card stays open while searching (no 'edge Hidden')", !AppLog().Contains("edge Hidden"), "");
        Click();
        Check("Q1b click outside the card -> tile Idle", WaitLog("share tile Idle", 2500), "");

        var text = new DataObject();
        text.SetData(DataFormats.UnicodeText, "just text");
        bool textDropped = DragToPanel(text, W, H, QsTile(W, H));
        Check("Q2 text drop on the Quick Share tile -> rejected, no search", textDropped && !_lastLog.Contains("share tile Searching") && _lastEffect == "None", _lastLog + " effect=" + _lastEffect);

        var cli = Environment.GetEnvironmentVariable("SIDEBAR_E2E_CLI");
        if (string.IsNullOrEmpty(cli)) { Rep("SKIP Q3 loopback e2e (set SIDEBAR_E2E_CLI to SideBar.Nearby.Cli.exe and run a Debug app)"); return; }
        if (!_exe.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { Rep("SKIP Q3 loopback e2e needs a Debug app (name filter)"); return; }
        LoopbackE2E(W, H, cli);
    }

    void LoopbackE2E(int W, int H, string cliExe)
    {
        var outDir = Path.Combine(_work, "recv");
        try { Directory.Delete(outDir, true); } catch { }
        Directory.CreateDirectory(outDir);
        var payload = new byte[3 << 20];
        new Random(42).NextBytes(payload);
        var src = Path.Combine(_srcDir, "e2e.bin");
        File.WriteAllBytes(src, payload);

        var psi = new ProcessStartInfo(cliExe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("receive"); psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--yes"); psi.ArgumentList.Add("--name"); psi.ArgumentList.Add("SidebarE2E"); psi.ArgumentList.Add("--reannounce"); psi.ArgumentList.Add("0");   // повторное объявление даёт Lost/Found и мигание плитки
        var output = new System.Text.StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        try
        {
            Thread.Sleep(3000);
            var files = new DataObject();
            files.SetData(DataFormats.FileDrop, new[] { src });
            bool dropped = DragToPanel(files, W, H, QsTile(W, H));
            bool found = dropped && (_lastLog.Contains("share tile Choosing") || WaitLog("share tile Choosing", 30000));   // DragToPanel стирает лог после drop: устройство могло найтись раньше
            Check("Q3 loopback: Cli receiver appears in the tile as a device (tile Choosing)", found, output.ToString());
            if (!found) return;
            Thread.Sleep(500);
            var slot0 = new Point(QsTile(W, H).X - (int)(48 * _scale), QsTile(W, H).Y - (int)(52 * _scale));
            MoveTo(slot0.X, slot0.Y, 400); Thread.Sleep(300);
            Shot("6-choosing", W, H);
            // Имена устройств в лог не пишутся, поэтому опора - фильтр SIDEBAR_NEARBY_ONLY_NAME, который есть только в Debug-сборке:
            // с Release-приложением в плитке мог бы оказаться настоящий телефон, и клик по первой иконке ушёл бы на него.
            if (!_exe.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Rep("SKIP Q3b not a Debug app: the name filter is unavailable, not clicking (a real phone could be in the tile)"); return; }
            Click();
            bool sending = WaitLog("share tile Sending", 3000);
            bool done = sending && WaitLog("share tile Done", 40000);
            Thread.Sleep(2000);
            var got = Path.Combine(outDir, "e2e.bin");
            bool same = File.Exists(got) && File.ReadAllBytes(got).AsSpan().SequenceEqual(payload);
            Check("Q3b click on the device -> Sending -> Done, file received byte for byte", sending && done && same, output.ToString());
            Thread.Sleep(2000);
            Check("Q3c tile returns to Idle 1.5 s after Done", AppLog().Contains("share tile Idle"), "");
        }
        finally { try { proc.Kill(true); } catch { } }
    }

    // ---- вынос стопки (Task 4) ----
    Rectangle CardRect(int W, int H) => new(W - (int)(224 * _scale), H - (int)(234.5 * _scale), (int)(216 * _scale), (int)(469 * _scale));
    Point ShelfTile(int W, int H) => new(W - (int)(116 * _scale), H + (int)(114 * _scale));
    Point QsTile(int W, int H) => new(W - (int)(116 * _scale), H - (int)(114 * _scale));

    void Shot(string name, int W, int H, bool aroundCursor = false)
    {
        try
        {
            var dir = Path.Combine(_work, "shots");
            Directory.CreateDirectory(dir);
            var r = CardRect(W, H);
            if (aroundCursor)
            {
                GetCursorPos(out var c);
                r = new Rectangle(c.X - 200, c.Y - 200, 400, 400);
            }
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e) { Rep("    (shot " + name + " failed: " + e.Message + ")"); }
    }

    /// <summary>Открывает карточку касанием края без зажатой кнопки и ждёт, пока пружина успокоится.</summary>
    bool OpenCard(int W, int H)
    {
        try { File.Delete(_appLog); } catch { }
        MoveTo(W - 700, H, 200); Thread.Sleep(500);
        MoveTo(W - 30, H, 300); Thread.Sleep(300);
        MoveTo(W - 1, H, 200);
        for (int i = 0; i < 20 && !AppLog().Contains("edge Open"); i++) Thread.Sleep(100);
        Thread.Sleep(800);
        return AppLog().Contains("edge Open");
    }

    /// <summary>Берёт стопку с плитки полки и несёт в точку <paramref name="to"/>; shotName - скриншот посреди drag'а (карточка и окрестность курсора).</summary>
    bool DragStackTo(Point to, int W, int H, string? shotName = null)
    {
        _otherEffect = ""; _received = [];
        if (!OpenCard(W, H)) { Rep("FAIL card did not open for drag-out"); _fail++; return false; }
        var tile = ShelfTile(W, H);
        MoveTo(tile.X, tile.Y, 300); Thread.Sleep(300);
        Mouse(0x0002);
        Thread.Sleep(200);
        MoveTo(tile.X - 40, tile.Y, 200);
        MoveTo(tile.X - 100, tile.Y - 30, 300);
        if (shotName != null)
        {
            Thread.Sleep(400);
            Shot(shotName, W, H);
            Shot(shotName + "-cursor", W, H, aroundCursor: true);
        }
        MoveTo(to.X, to.Y, 400); Thread.Sleep(400);
        Mouse(0x0004);
        Thread.Sleep(1200);
        MoveTo(W - 700, H, 200); Thread.Sleep(500);
        return true;
    }

    /// <summary>Наполняет пустую стопку пятью элементами: ссылка на файл, на каталог, Owned, ссылка на файл, ссылка на png.</summary>
    List<ShelfEntry> FillShelf(string a, string folder, string refFile, int W, int H)
    {
        // наполняем: ссылка на файл, ссылка на каталог, Owned (виртуальный файл), ещё одна ссылка
        var one = new DataObject();
        one.SetData(DataFormats.FileDrop, new[] { a });
        DragToPanel(one, W, H);
        var s = WaitShelf(x => x.Count == 1);
        if (OpenCard(W, H)) Shot("2-one", W, H);
        MoveTo(W - 700, H, 200); Thread.Sleep(500);
        var dirDrop = new DataObject();
        dirDrop.SetData(DataFormats.FileDrop, new[] { folder });
        DragToPanel(dirDrop, W, H);
        DragToPanel(MakeVirtual("owned-out.bin", [1, 2, 3, 4, 5, 6, 7, 8]), W, H);
        var two = new DataObject();
        two.SetData(DataFormats.FileDrop, new[] { refFile });
        DragToPanel(two, W, H);
        var png = Path.Combine(_srcDir, "picture.png");
        using (var bmp = new Bitmap(200, 120))
        {
            using (var g = Graphics.FromImage(bmp)) g.FillRectangle(new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 200, 120), Color.OrangeRed, Color.RoyalBlue, 45f), 0, 0, 200, 120);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }
        var pngDrop = new DataObject();
        pngDrop.SetData(DataFormats.FileDrop, new[] { png });
        DragToPanel(pngDrop, W, H);
        return WaitShelf(x => x.Count == 5);
    }

    void DragOutScenarios(int W, int H, string a, string refFile, string folder)
    {
        // чистое состояние: перезапуск с пустым каталогом данных
        StopApp();
        try { Directory.Delete(_dataDir, true); } catch { }
        if (!StartApp()) { _fail++; return; }
        var otherCenter = new Point(_other.Left + 150, _other.Top + 75);
        if (Environment.GetEnvironmentVariable("SIDEBAR_DRIVER_FAN_ONLY") == "1") { FanScenarios(W, H, a, refFile, folder, otherCenter); return; }   // быстрый прогон: только веер (F1..F5)

        if (!OpenCard(W, H)) { Rep("FAIL card did not open"); _fail++; return; }
        Shot("1-empty", W, H);
        MoveTo(W - 700, H, 200); Thread.Sleep(500);

        var s = FillShelf(a, folder, refFile, W, H);
        Check("D0 shelf prepared: file ref, folder ref, owned, file ref, png ref", s.Count == 5 && s.Count(x => x.Kind == Owned) == 1, Dump(s));
        if (OpenCard(W, H)) Shot("3-stack", W, H);
        MoveTo(W - 700, H, 200); Thread.Sleep(500);
        var ids = s.Select(x => x.Id).ToList();
        var owned = s.First(x => x.Kind == Owned);

        // D1: принимающее окно отказывает -> None, элементы на месте
        _otherRejects = true;
        if (DragStackTo(otherCenter, W, H))
        {
            var log = AppLog();
            s = ReadShelf();
            Check("D1 drag-out onto a rejecting window -> DropResult None, items stay",
                _otherEffect == "" && s.Select(x => x.Id).SequenceEqual(ids) && log.Contains("DropResult=None"), Dump(s));
        }

        // D2: вынос на плитку Quick Share своей карточки = отправка; элементы остаются в стопке
        _otherRejects = false;
        if (DragStackTo(QsTile(W, H), W, H))
        {
            var log = AppLog();
            Rep("INFO D2 log: " + log.Replace("\n", " | "));
            s = ReadShelf();
            Check("D2 drag-out onto own Quick Share tile -> send starts (tile Searching), items stay in the shelf",
                s.Select(x => x.Id).SequenceEqual(ids) && log.Contains("items kept (share)") && log.Contains("share tile Searching") && log.Contains("share: searching") && log.Contains("file(s)"), Dump(s));
            MoveTo(_other.Left + 150, _other.Top + 75, 400); Thread.Sleep(100);
            Click();   // клик вне карточки отменяет поиск
            Check("D2a click outside the card cancels the search -> tile back to Idle", WaitLog("share tile Idle", 2500), "");
            Thread.Sleep(1000);
            s = ReadShelf();
            Check("D2c shelf unchanged after cancelled share", s.Select(x => x.Id).SequenceEqual(ids), Dump(s));
        }
        var shelfTile = ShelfTile(W, H);
        if (DragStackTo(new Point(shelfTile.X, shelfTile.Y - (int)(20 * _scale)), W, H))
        {
            s = ReadShelf();
            Check("D2b drag-out and drop on own shelf tile -> DropResult None, items stay, not duplicated",
                s.Select(x => x.Id).SequenceEqual(ids) && WaitNone(0)   /* DragStackTo начинается с OpenCard, который очищает лог */, Dump(s) + "\n" + AppLog());
        }

        if (Environment.GetEnvironmentVariable("SIDEBAR_DRIVER_SHARE_ONLY") == "1") { ShareScenarios(W, H, a); return; }

        // D3: принимающее окно -> Copy, элементы сняты, ссылки целы, Owned живёт ещё не менее 30 с
        if (DragStackTo(otherCenter, W, H, "4-drag"))
        {
            s = WaitShelf(x => x.Count == 0, 3000);
            var t0 = DateTime.UtcNow;   // элементы сняты не позже этого момента; 30 с отсчитываются от снятия
            var got = _received;
            bool names = got.Any(p => p.EndsWith("a.txt")) && got.Any(p => p.EndsWith("ref-original.txt")) && got.Any(p => p.EndsWith("owned-out.bin"))
                && got.Any(p => p.TrimEnd('\\').EndsWith("folder1"));
            Check("D3 drag-out onto accepting window -> Copy, all 5 items received (files + folder), shelf emptied, reference originals intact",
                _otherEffect == "Copy" && names && s.Count == 0 && File.Exists(a) && File.Exists(refFile) && Directory.Exists(folder)
                && AppLog().Contains("DropResult=Copy"), $"effect={_otherEffect} received=[{string.Join("; ", got)}]\n" + Dump(s));
            Thread.Sleep(2000);
            Check("D3b owned file is still there shortly after drag-out (receiver may copy asynchronously)", File.Exists(owned.Path), owned.Path);
            if (OpenCard(W, H)) Shot("5-after", W, H);
            int wait = 32_000 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
            Check("D3c owned file and its Shelf\\<guid> folder are deleted ~30 s after drag-out",
                !File.Exists(owned.Path) && !Directory.Exists(Path.GetDirectoryName(owned.Path)!), owned.Path);
        }
        FanScenarios(W, H, a, refFile, folder, otherCenter);
        ClearScenarios(W, H, a, Path.Combine(_srcDir, "b.txt"), Path.Combine(_srcDir, "c.txt"));
        DefaultDelayScenario(W, H, a, refFile, folder, otherCenter);
        PendingDeleteRestartScenario(W, H, a, refFile, folder, otherCenter);
    }

    // корзина полки: правый нижний угол плитки полки (карточка - поле 12 epx, у корзины отступ 4 epx и размер 22 epx)
    Point TrashPoint(int W, int H)
    {
        var r = CardRect(W, H);
        return new Point(r.Right - (int)(27 * _scale), r.Bottom - (int)(27 * _scale));
    }

    void FillThree(int W, int H, string a, string b, string c)
    {
        var files = new DataObject();
        files.SetData(DataFormats.FileDrop, new[] { a, b, c });
        DragToPanel(files, W, H);
    }

    // C1..C3: очистка полки корзиной только вторым кликом в течение 3 с; файлы на диске целы
    void ClearScenarios(int W, int H, string a, string b, string c)
    {
        StopApp();
        try { Directory.Delete(_dataDir, true); } catch { }
        if (!StartApp()) { _fail++; return; }
        FillThree(W, H, a, b, c);
        var s = WaitShelf(x => x.Count == 3);
        if (s.Count != 3 || !OpenCard(W, H)) { Check("C0 shelf prepared and card open", false, Dump(s)); return; }
        var t = TrashPoint(W, H);
        MoveTo(t.X, t.Y, 300); Thread.Sleep(300);
        Click(); Thread.Sleep(600);
        s = ReadShelf();
        Shot("c1-armed", W, H);
        Check("C1 first click on the trash only arms it: shelf still holds 3", s.Count == 3, Dump(s));
        Click();
        s = WaitShelf(x => x.Count == 0, 3000);
        Check("C2 second click within 3 s clears the shelf; the files stay on disk; log 'shelf cleared n=3'",
            s.Count == 0 && File.Exists(a) && File.Exists(b) && File.Exists(c) && AppLog().Contains("shelf cleared n=3"), Dump(s));
        MoveTo(W - 700, H, 200); Thread.Sleep(500);
        FillThree(W, H, a, b, c);
        s = WaitShelf(x => x.Count == 3);
        if (!OpenCard(W, H)) { Check("C3 card open", false, ""); return; }
        MoveTo(t.X, t.Y, 300); Thread.Sleep(300);
        Click(); Thread.Sleep(3500);
        Click(); Thread.Sleep(600);
        s = ReadShelf();
        Check("C3 click, wait 3.5 s, click again: the arming expired, the shelf is not cleared", s.Count == 3, Dump(s));
    }

    // D3e: отложенное удаление переживает перезапуск: перезапуск внутри окна задержки не удаляет папку, после срока она удаляется
    void PendingDeleteRestartScenario(int W, int H, string a, string refFile, string folder, Point otherCenter)
    {
        StopApp();
        try { Directory.Delete(_dataDir, true); } catch { }
        const int delaySec = 25;
        if (!StartApp(deleteDelaySec: delaySec)) { _fail++; return; }
        var s = FillShelf(a, folder, refFile, W, H);
        var owned = s.FirstOrDefault(x => x.Kind == Owned);
        if (s.Count != 5 || owned == null) { Check("D3e shelf prepared", false, Dump(s)); return; }
        var ownedDir = Path.GetDirectoryName(owned.Path)!;
        bool dragged = DragStackTo(otherCenter, W, H);
        s = WaitShelf(x => x.Count == 0, 3000);
        var pending = ReadPending();
        Check("D3e drag-out records the owned folder in shelf.json pendingDeletes",
            dragged && s.Count == 0 && pending.Any(p => Guid.TryParse(Path.GetFileName(ownedDir), out var dirGuid) && Guid.Parse(p.Guid) == dirGuid), string.Join("; ", pending));
        StopApp();
        bool restarted = StartApp(deleteDelaySec: delaySec);
        var deadline = pending.Select(p => p.After).DefaultIfEmpty(DateTimeOffset.UtcNow).Max();
        bool inWindow = DateTimeOffset.UtcNow < deadline - TimeSpan.FromSeconds(2);
        Check("D3e restart inside the delay window: owned folder and file are still there (not swept)",
            restarted && inWindow && Directory.Exists(ownedDir) && File.Exists(owned.Path), $"inWindow={inWindow} left={(deadline - DateTimeOffset.UtcNow).TotalSeconds:F1}s");
        var wait = deadline + TimeSpan.FromSeconds(3) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        Check("D3e after the saved deadline the re-armed timer deletes the owned folder", !Directory.Exists(ownedDir), ownedDir);
    }

    record PendingEntry(string Guid, DateTimeOffset After)
    {
        public override string ToString() => $"{Guid} after {After:O}";
    }

    List<PendingEntry> ReadPending()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(_shelfJson)) return [];
                using var doc = JsonDocument.Parse(File.ReadAllText(_shelfJson));
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("pendingDeletes", out var arr)) return [];
                return arr.EnumerateArray().Select(e => new PendingEntry(e.GetProperty("guid").GetString()!, e.GetProperty("deleteAfterUtc").GetDateTimeOffset())).ToList();
            }
            catch (Exception e) when (e is IOException or JsonException) { Thread.Sleep(100); }
        }
        return [];
    }

    // D3d: с боевой задержкой (10 мин) Owned-файл жив и через 60 с после выноса
    void DefaultDelayScenario(int W, int H, string a, string refFile, string folder, Point otherCenter)
    {
        StopApp();
        try { Directory.Delete(_dataDir, true); } catch { }
        if (!StartApp(defaultDeleteDelay: true)) { _fail++; return; }
        var s = FillShelf(a, folder, refFile, W, H);
        var owned = s.FirstOrDefault(x => x.Kind == Owned);
        if (s.Count != 5 || owned == null) { Check("D3d shelf prepared", false, Dump(s)); return; }
        bool dragged = DragStackTo(otherCenter, W, H);
        s = WaitShelf(x => x.Count == 0, 3000);
        var t0 = DateTime.UtcNow;
        int wait = 60_000 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        Thread.Sleep(Math.Max(wait, 0));
        Check("D3d default delay: owned file still exists 60 s after drag-out", dragged && s.Count == 0 && File.Exists(owned.Path), owned.Path);
    }

    // ---- веер (Task 5) ----
    static readonly System.Text.RegularExpressions.Regex FanLog = new(@"fan (open (-?\d+),(-?\d+),(-?\d+),(-?\d+)|closed)");

    /// <summary>Последняя зона веера из лога приложения (физические px); null — веер закрыт или не открывался.</summary>
    Rectangle? FanZone()
    {
        var m = FanLog.Matches(AppLog());
        if (m.Count == 0 || m[^1].Value == "fan closed") return null;
        var g = m[^1].Groups;
        int l = int.Parse(g[2].Value), t = int.Parse(g[3].Value), r = int.Parse(g[4].Value), b = int.Parse(g[5].Value);
        return new Rectangle(l, t, r - l, b - t);
    }

    Rectangle? WaitFan(bool open, int ms = 3000)
    {
        var z = FanZone();
        for (int t = 0; t < ms && (z != null) != open; t += 50) { Thread.Sleep(50); z = FanZone(); }
        return z;
    }

    nint _fgBeforeFan;

    /// <summary>Окно переднего плана принадлежит процессу SideBar (карточка или веер)?</summary>
    bool ForegroundIsApp(out string cls)
    {
        var h = GetForegroundWindow();
        var sb = new System.Text.StringBuilder(256);
        GetClassNameW(h, sb, 256);
        cls = sb.ToString();
        GetWindowThreadProcessId(h, out var pid);
        return _app != null && pid == (uint)_app.Id;
    }

    /// <summary>Сколько раз в логе приложения встретился результат выноса None (лог накапливается, поэтому сравниваем счётчики до/после).</summary>
    int NoneCount() => System.Text.RegularExpressions.Regex.Count(AppLog(), "DropResult=None");

    /// <summary>Ждёт, пока счётчик DropResult=None превысит <paramref name="before"/> (DropCompleted приходит чуть позже конца drag'а).</summary>
    bool WaitNone(int before, int ms = 4000)
    {
        for (int t = 0; t < ms && NoneCount() <= before; t += 100) Thread.Sleep(100);
        return NoneCount() > before;
    }

    static void Click() { Mouse(0x0002); Thread.Sleep(80); Mouse(0x0004); }

    /// <summary>Открывает карточку и кликом по стопке — веер; ждёт, пока пружина успокоится.</summary>
    Rectangle? OpenFan(int W, int H)
    {
        if (!OpenCard(W, H)) return null;
        var tile = ShelfTile(W, H);
        MoveTo(tile.X, tile.Y, 300); Thread.Sleep(300);
        _fgBeforeFan = GetForegroundWindow();
        Click();
        var z = WaitFan(true);
        Thread.Sleep(900);
        return z;
    }

    void ShotRegion(string name, Rectangle r)
    {
        try
        {
            var dir = Path.Combine(_work, "shots");
            Directory.CreateDirectory(dir);
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e) { Rep("    (shot " + name + " failed: " + e.Message + ")"); }
    }

    /// <summary>X вне карточки и веера (веер при масштабе 1.75 тянется на 448 epx от края, W - 700 px попадало бы в него).</summary>
    int FarX(int W) => W - (int)(520 * _scale);

    /// <summary>Центр строки <paramref name="row"/> (0 — верхняя) веера с зоной <paramref name="z"/>.</summary>
    Point FanRow(Rectangle z, int row) => new(z.Left + (int)(108 * _scale), z.Top + (int)((4 + 48 * row + 24) * _scale));

    void DragFanRow(Rectangle z, int row, Point to, int W, int H)
    {
        _otherEffect = ""; _received = [];
        var p = FanRow(z, row);
        MoveTo(p.X, p.Y, 300); Thread.Sleep(300);
        Mouse(0x0002);
        Thread.Sleep(200);
        MoveTo(p.X - 30, p.Y, 200);
        MoveTo(to.X, to.Y, 400); Thread.Sleep(400);
        Mouse(0x0004);
        Thread.Sleep(1200);
        MoveTo(FarX(W), H, 200); Thread.Sleep(500);
    }

    void FanScenarios(int W, int H, string a, string refFile, string folder, Point otherCenter)
    {
        var s = FillShelf(a, folder, refFile, W, H);
        if (s.Count != 5) { Rep("FAIL fan: shelf was not prepared"); _fail++; return; }
        var ids = s.Select(x => x.Id).ToList();
        var card = CardRect(W, H);

        // F1: клик по стопке -> веер слева от карточки (зазор 8 epx), по центру, 5 строк по 48 epx
        var z = OpenFan(W, H);
        if (z is not { } fan) { Check("F1 click on the stack opens the fan", false, "no 'fan open' in the log"); return; }
        int expH = (int)Math.Round((5 * 48 + 8) * _scale), expLeft = card.Left - (int)Math.Round(224 * _scale);
        int cardCenter = card.Top + card.Height / 2;
        nint fgAfterOpen = GetForegroundWindow();
        ShotRegion("6-fan", new Rectangle(Math.Max(0, fan.Left - 30), Math.Max(0, Math.Min(fan.Top, card.Top) - 20), W - Math.Max(0, fan.Left - 30), Math.Max(fan.Height, card.Height) + 40));
        Check("F1 click on the stack -> fan zone left of the card (8 epx gap), vertically centred, 5 rows of 48 epx",
            Math.Abs(fan.Left - expLeft) <= 3 && Math.Abs(fan.Right - card.Left) <= 3 && Math.Abs(fan.Height - expH) <= 3
            && Math.Abs(fan.Top + fan.Height / 2 - cardCenter) <= 3, $"fan={fan} card={card} expLeft={expLeft} expH={expH}");

        Check("F1b clicking the stack and opening the fan does not change the foreground window", fgAfterOpen == _fgBeforeFan, $"before={_fgBeforeFan} after={fgAfterOpen}");

        // F1c: повторный клик по стопке при открытом веере закрывает его (переключатель)
        Click();
        var toggled = WaitFan(false, 1500);
        Check("F1c second click on the stack while the fan is open -> fan closed", toggled == null, AppLog());
        fan = OpenFan(W, H) ?? default;
        if (fan.IsEmpty) { Check("F1d fan re-opens after toggle", false, "no fan"); return; }

        // F2: вынос одной строки (вторая сверху = ref-original.txt) в принимающее окно -> Copy, снят только он
        nint fgBeforeDrag = GetForegroundWindow();
        DragFanRow(fan, 1, otherCenter, W, H);
        nint fgAfterDrag = GetForegroundWindow();
        bool fgAppAfterDrag = ForegroundIsApp(out var fgClass);
        var got = _received;
        var left = ReadShelf();
        var removed = ids.Except(left.Select(x => x.Id)).ToList();
        Check("F2 drag one fan row onto an accepting window -> Copy, only that item received and removed, original intact",
            _otherEffect == "Copy" && got.Length == 1 && got[0].EndsWith("ref-original.txt") && left.Count == 4 && removed.Count == 1
            && s.First(x => x.Id == removed[0]).Path.EndsWith("ref-original.txt") && File.Exists(refFile) && AppLog().Contains("DropResult=Copy"),
            $"effect={_otherEffect} received=[{string.Join("; ", got)}] removed={removed.Count}" + "\n" + Dump(left));

        // приёмник drop'а вправе сам выйти на передний план (Проводник так делает): важно, что не карточка и не веер
        Check("F2b after a fan row drag-out the foreground window is not the card or the fan", !fgAppAfterDrag, $"before={fgBeforeDrag} after={fgAfterDrag} class={fgClass}");

        // F2c: строка, брошенная на свою же полку (карточку) или на сам веер, остаётся в стопке
        var idsNow = ReadShelf().Select(x => x.Id).ToList();
        fan = OpenFan(W, H) ?? default;
        if (fan.IsEmpty) { Check("F2c fan re-opens", false, "no fan"); return; }
        DragFanRow(fan, 0, ShelfTile(W, H), W, H);
        var afterCard = ReadShelf().Select(x => x.Id).ToList();
        bool cardNone = AppLog().Contains("DropResult=None");
        Check("F2c fan row dropped onto the card (own shelf) -> DropResult None, item stays", afterCard.SequenceEqual(idsNow) && cardNone, $"log={AppLog()}");
        fan = OpenFan(W, H) ?? default;
        if (fan.IsEmpty) { Check("F2d fan re-opens", false, "no fan"); return; }
        int noneBeforeD = NoneCount();
        DragFanRow(fan, 0, FanRow(fan, 2), W, H);
        Check("F2d fan row dropped onto the fan itself -> DropResult None, item stays",
            ReadShelf().Select(x => x.Id).SequenceEqual(idsNow) && WaitNone(noneBeforeD), $"log={AppLog()}");

        // F3: клик вне карточки и веера -> веер закрыт
        fan = OpenFan(W, H) ?? default;
        if (fan.IsEmpty) { Check("F3 fan re-opens with 4 items", false, "no fan"); return; }
        Check("F3a fan shrinks to 4 rows", Math.Abs(fan.Height - (int)Math.Round((4 * 48 + 8) * _scale)) <= 3, $"fan={fan}");
        MoveTo(otherCenter.X, otherCenter.Y, 400); Thread.Sleep(100);
        Click();
        var closed = WaitFan(false, 1500);
        Check("F3 click outside card and fan -> fan closed", closed == null, AppLog());

        // F4: курсор с карточки уходит в веер -> карточка остаётся; уход с обоих -> скрытие примерно через 300 мс
        fan = OpenFan(W, H) ?? default;
        if (fan.IsEmpty) { Check("F4 fan re-opens", false, "no fan"); return; }
        var inside = FanRow(fan, 1);
        MoveTo(inside.X, inside.Y, 400);
        Thread.Sleep(1200);
        string log = AppLog();
        bool stillOpen = !log.Contains("edge Hidden") && FanZone() != null;
        Check("F4a cursor over the fan (not over the card) for 1.2 s -> card stays open, fan stays", stillOpen, log);
        var t0 = DateTime.Now;
        MoveTo(FarX(W), H, 200);
        for (int i = 0; i < 40 && !AppLog().Contains("edge Hidden"); i++) Thread.Sleep(50);
        Thread.Sleep(200);
        var hid = System.Text.RegularExpressions.Regex.Match(AppLog(), @"(\d\d):(\d\d):(\d\d)\.(\d\d\d) edge Hidden");
        double ms = hid.Success ? (new DateTime(t0.Year, t0.Month, t0.Day, int.Parse(hid.Groups[1].Value), int.Parse(hid.Groups[2].Value), int.Parse(hid.Groups[3].Value), int.Parse(hid.Groups[4].Value)) - t0).TotalMilliseconds : -1;
        Check("F4b cursor leaves card and fan -> card hides 250..800 ms after the move starts (300 ms delay inside the 200 ms move) and the fan closes",
            hid.Success && FanZone() == null && ms >= 250 && ms <= 800, $"hidden {ms:F0} ms after the move started");

        // F5: последний элемент вынесен из веера -> веер закрывается сам
        var rest = ReadShelf();
        for (int k = 0; k < rest.Count && ReadShelf().Count > 0; k++)
        {
            fan = OpenFan(W, H) ?? default;
            if (fan.IsEmpty) break;
            DragFanRow(fan, 0, otherCenter, W, H);
        }
        Check("F5 all rows dragged out one by one -> shelf empty and fan closed", ReadShelf().Count == 0 && FanZone() == null, Dump(ReadShelf()));
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
                // новый формат — объект { items, pendingDeletes }, старый — голый массив
                var items = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.GetProperty("items") : doc.RootElement;
                return items.EnumerateArray().Select(e => new ShelfEntry(
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
    bool DragToPanel(object data, int W, int H, Point? dropAt = null)
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
        if (dropAt is { } at) { MoveTo(at.X, at.Y, 300); Thread.Sleep(400); }
        else { MoveTo(W - (int)((8 + 108) * _scale), H, 300); Thread.Sleep(400); }   // центр карточки
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
