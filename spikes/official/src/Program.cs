using System.Diagnostics;
using SideBar.Official;

// Usage: Official status | Official cycle <normal|minnoactive|minimized|hidden> [publishTimeoutSec] [closeTimeoutSec]
// Prints only process/window/log-marker facts, never log contents.
var ctl = new OfficialAppController();
string cmd = args.Length > 0 ? args[0] : "status";

if (cmd == "status")
{
    var ps = OfficialAppController.FindProcesses();
    Console.WriteLine($"running={ps.Length > 0} processes={ps.Length}");
    foreach (var p in ps)
        foreach (var (_, w) in OfficialAppController.WindowsOf(p.Id))
            Console.WriteLine($"  pid={p.Id} window class={w.ClassName} visible={w.Visible} iconic={w.Iconic} titled={w.HasTitle}");
    return 0;
}

if (cmd == "quit")
{
    bool ok = await OfficialAppController.QuitMessageLoopAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine($"WM_QUIT to UI thread: exited={ok}");
    return ok ? 0 : 1;
}

if (cmd == "cycle")
{
    var mode = (args.Length > 1 ? args[1] : "minnoactive") switch
    {
        "normal" => ShowMode.Normal,
        "minimized" => ShowMode.Minimized,
        "hidden" => ShowMode.Hidden,
        _ => ShowMode.MinimizedNoActivate,
    };
    var publishTimeout = TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 60);
    var closeTimeout = TimeSpan.FromSeconds(args.Length > 3 ? int.Parse(args[3]) : 20);

    if (OfficialAppController.IsRunning()) { Console.WriteLine("ABORT: nearby_share.exe is already running (user may be using it)"); return 2; }

    string sharing = Path.GetDirectoryName(ctl.LogDir)!;
    string db = Path.Combine(sharing, "NearbySharePublicCertificateDatabase");
    var before = Snapshot(sharing, db);
    var logs = ctl.SnapshotLogs();
    var sw = Stopwatch.StartNew();
    int pid = ctl.Launch(mode);
    Console.WriteLine($"launched mode={mode} pid={pid}");

    var publish = ctl.WaitForLogAsync(OfficialAppController.PublishMarker, logs, publishTimeout);
    var seen = new HashSet<string>();
    while (!publish.IsCompleted)
    {
        foreach (var p in OfficialAppController.FindProcesses())
            foreach (var (_, w) in OfficialAppController.WindowsOf(p.Id))
            {
                string s = $"class={w.ClassName} visible={w.Visible} iconic={w.Iconic}";
                if (seen.Add(s)) Console.WriteLine($"  t={sw.Elapsed.TotalSeconds:F1}s window {s}");
            }
        await Task.WhenAny(publish, Task.Delay(300));
    }
    Console.WriteLine($"PublishDevice succeeded seen={publish.Result} after {sw.Elapsed.TotalSeconds:F1}s");
    await Task.Delay(1500); // let leveldb/preferences writes settle

    foreach (var p in OfficialAppController.FindProcesses())
        foreach (var (_, w) in OfficialAppController.WindowsOf(p.Id))
            Console.WriteLine($"  before close: class={w.ClassName} visible={w.Visible} iconic={w.Iconic}");

    var tClose = Stopwatch.StartNew();
    bool closed = await OfficialAppController.QuitMessageLoopAsync(closeTimeout);
    Console.WriteLine($"WM_QUIT close: exited={closed} after {tClose.Elapsed.TotalSeconds:F1}s; processes left={OfficialAppController.FindProcesses().Length}");
    if (!closed)
        foreach (var p in OfficialAppController.FindProcesses())
            foreach (var (_, w) in OfficialAppController.WindowsOf(p.Id))
                Console.WriteLine($"  after close: class={w.ClassName} visible={w.Visible} iconic={w.Iconic}");
    await Task.Delay(500);
    var after = Snapshot(sharing, db);
    Console.WriteLine("-- diff (name: before -> after; size bytes, mtime) ");
    foreach (var k in before.Keys.Union(after.Keys).Order())
    {
        before.TryGetValue(k, out var b); after.TryGetValue(k, out var a);
        if (b != a) Console.WriteLine($"  {k}: {(b ?? "absent")} -> {(a ?? "absent")}");
    }
    bool exitMarker = await ctl.WaitForLogAsync(OfficialAppController.ExitMarker, logs, TimeSpan.FromSeconds(1));
    Console.WriteLine($"exit marker in log: {exitMarker}");
    return closed ? 0 : 1;
}

Console.WriteLine("unknown command");
return 64;

static Dictionary<string, string> Snapshot(string sharing, string db)
{
    var d = new Dictionary<string, string>();
    foreach (var f in Directory.GetFiles(db).Concat(new[] { "preferences.json", "preferences_bak.json", "nearby_sharing_preferences.pb", "nearby_sharing_flags.dat" }.Select(n => Path.Combine(sharing, n))))
    {
        var fi = new FileInfo(f);
        string name = f.StartsWith(db) ? "leveldb/" + fi.Name : fi.Name;
        d[name] = fi.Exists ? $"{fi.Length}B {fi.LastWriteTime:HH:mm:ss}" : "absent";
    }
    return d;
}
