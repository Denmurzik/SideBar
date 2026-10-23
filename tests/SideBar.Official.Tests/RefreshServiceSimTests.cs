using System.Diagnostics;
using System.Runtime.InteropServices;
using SideBar.Core.Official;
using SideBar.Official;

namespace SideBar.Official.Tests;

// Все сценарии идут на симуляторе nearby_share_sim.exe (скрытые окна, временный каталог вместо Sharing).
// Один класс = последовательно: процессы симулятора общие по имени.
public sealed class RefreshServiceSimTests : IDisposable
{
    const string SimName = "nearby_share_sim";
    static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "SideBar.Tests", Guid.NewGuid().ToString("N"));
    readonly List<string> _log = [];
    readonly List<string> _hooks = [];
    DateTimeOffset _now = T0;
    readonly Stopwatch _sw = Stopwatch.StartNew();

    public RefreshServiceSimTests()
    {
        Directory.CreateDirectory(_dir);
        Assert.Empty(SimProcesses());   // чужих симуляторов быть не должно
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SIDEBAR_SIM_MODE", null);
        CloseSims();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static string SimExe()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tools", "SideBar.OfficialSim"))) d = d.Parent;
        Assert.NotNull(d);
        return Directory.GetFiles(Path.Combine(d.FullName, "tools", "SideBar.OfficialSim", "bin"), SimName + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    static Process[] SimProcesses() => Process.GetProcessesByName(SimName);

    // Симулятор — наш процесс: закрываем его WM_CLOSE окну (не TerminateProcess).
    static void CloseSims()
    {
        foreach (var p in SimProcesses())
        {
            foreach (var w in Native.WindowsOf(p.Id)) PostMessageW(w.Hwnd, 0x0010, 0, 0);
            p.WaitForExit(5000);
            p.Dispose();
        }
    }

    [DllImport("user32.dll")] static extern int PostMessageW(nint hwnd, uint msg, nint w, nint l);

    string SharingDir => Path.Combine(_dir, "Sharing");
    string StatePath => Path.Combine(_dir, "state.json");

    RefreshService Make(string mode, RefreshTimings? timings = null, string? exe = null, Func<TimeSpan>? idle = null)
    {
        Environment.SetEnvironmentVariable("SIDEBAR_SIM_MODE", mode);
        Environment.SetEnvironmentVariable("SIDEBAR_SHARING_DIR", SharingDir);
        Environment.SetEnvironmentVariable("SIDEBAR_OFFICIAL_EXE", exe ?? SimExe());
        var app = OfficialAppController.FromEnvironment(s => _log.Add($"{_sw.ElapsedMilliseconds}ms " + s));
        var hooks = new RefreshHooks
        {
            PauseStack = () => { _hooks.Add("pause"); return Task.CompletedTask; },
            ReloadCertificates = () => _hooks.Add("reload"),
            ResumeStack = () => _hooks.Add("resume"),
            WarnStale = () => _hooks.Add("warn"),
        };
        var t = timings ?? new RefreshTimings
        {
            MarkerTimeout = TimeSpan.FromSeconds(10),
            Settle = TimeSpan.FromMilliseconds(100),
            ExitWait = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
        };
        return new RefreshService(app, new AppStateStore(StatePath), hooks, s => _log.Add($"{_sw.ElapsedMilliseconds}ms " + s), t, () => _now, idle);
    }

    [Fact]
    public async Task Normal_cycle_marker_then_quit_and_exit()
    {
        var svc = Make("normal");
        Assert.Equal(RefreshOutcome.Succeeded, await svc.RunOnceAsync());
        Assert.Empty(SimProcesses());
        Assert.Equal(["pause", "reload", "resume"], _hooks);
        Assert.Equal(T0, new AppStateStore(StatePath).Load().LastSuccess);
        Assert.Contains(_log, l => l.Contains("close attempt 1, Posted"));
    }

    [Fact]
    public async Task Log_rotation_during_wait_still_finds_marker()
    {
        var svc = Make("rotate");
        Assert.Equal(RefreshOutcome.Succeeded, await svc.RunOnceAsync());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(SharingDir, "Logs"), "nearby_share*.log").Length);
        Assert.Empty(SimProcesses());
    }

    [Fact]
    public async Task User_opened_app_is_not_closed()
    {
        var svc = Make("user");
        Assert.Equal(RefreshOutcome.UserTookOver, await svc.RunOnceAsync());
        Assert.Single(SimProcesses());   // жив: WM_QUIT не слали
        Assert.DoesNotContain(_log, l => l.Contains("close attempt"));
        Assert.Null(new AppStateStore(StatePath).Load().LastSuccess);
        Assert.Equal(["pause", "reload", "resume"], _hooks);
    }

    [Fact]
    public async Task Hung_process_gets_three_attempts_and_is_never_killed()
    {
        var svc = Make("hung", new RefreshTimings
        {
            MarkerTimeout = TimeSpan.FromSeconds(10),
            Settle = TimeSpan.FromMilliseconds(100),
            ExitWait = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromSeconds(1),
        });
        var outcome = await svc.RunOnceAsync();
        Assert.True(outcome == RefreshOutcome.DidNotExit, outcome + ": " + string.Join(" | ", _log));
        Assert.Equal(3, _log.Count(l => l.Contains("close attempt")));
        Assert.Single(SimProcesses());
        Assert.Null(new AppStateStore(StatePath).Load().LastSuccess);
    }

    [Fact]
    public async Task Marker_timeout_closes_our_process_and_reports_timeout()
    {
        var svc = Make("silent", new RefreshTimings
        {
            MarkerTimeout = TimeSpan.FromSeconds(1),
            Settle = TimeSpan.FromMilliseconds(100),
            ExitWait = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
        });
        Assert.Equal(RefreshOutcome.MarkerTimeout, await svc.RunOnceAsync());
        Assert.Empty(SimProcesses());
        Assert.Null(new AppStateStore(StatePath).Load().LastSuccess);
    }

    [Fact]
    public async Task Already_running_is_not_launched_again()
    {
        var first = Make("user");
        Assert.Equal(RefreshOutcome.UserTookOver, await first.RunOnceAsync());   // оставляет один живой симулятор
        var logs = Directory.GetFiles(Path.Combine(SharingDir, "Logs")).Length;
        _hooks.Clear();

        var second = Make("normal");
        Assert.Equal(RefreshOutcome.AlreadyRunning, await second.RunOnceAsync());
        Assert.Single(SimProcesses());
        Assert.Equal(logs, Directory.GetFiles(Path.Combine(SharingDir, "Logs")).Length);
        Assert.Empty(_hooks);   // стек не трогали
    }

    [Fact]
    public async Task Not_installed_is_silent()
    {
        var svc = Make("normal", exe: Path.Combine(_dir, "missing", "nearby_share.exe"));
        Assert.Equal(RefreshOutcome.NotInstalled, await svc.RunOnceAsync());
        Assert.Empty(_hooks);
        Assert.Empty(SimProcesses());
    }

    [Fact]
    public async Task Tick_runs_cycle_when_never_refreshed_and_skips_when_fresh()
    {
        var svc = Make("normal");
        await svc.TickAsync();
        Assert.Equal(["pause", "reload", "resume"], _hooks);
        Assert.Equal(T0, new AppStateStore(StatePath).Load().LastSuccess);

        _hooks.Clear();
        _now = T0.AddHours(5);   // < 20 ч
        await svc.TickAsync();
        Assert.Empty(_hooks);

        _now = T0.AddHours(21);   // ≥ 20 ч, пользователь занят
        var busy = Make("normal", idle: () => TimeSpan.FromMinutes(1));
        await busy.TickAsync();
        Assert.Empty(_hooks);
        var idle = Make("normal", idle: () => TimeSpan.FromMinutes(6));
        await idle.TickAsync();
        Assert.Equal(["pause", "reload", "resume"], _hooks);
    }

    [Fact]
    public async Task Stale_warning_once_after_72h_from_first_seen_without_app()
    {
        var svc = Make("normal", exe: Path.Combine(_dir, "missing", "nearby_share.exe"));
        await svc.TickAsync();   // фиксирует FirstSeen
        Assert.Equal(T0, new AppStateStore(StatePath).Load().FirstSeen);
        Assert.Empty(_hooks);

        _now = T0.AddHours(73);
        await svc.TickAsync();
        await svc.TickAsync();
        Assert.Equal(["warn"], _hooks);
    }

    [Fact]
    public async Task Future_last_success_is_clamped_and_does_not_block_forever()
    {
        new AppStateStore(StatePath).Save(new AppState { LastSuccess = T0.AddDays(30), FirstSeen = T0.AddDays(30) });
        var svc = Make("normal", idle: () => TimeSpan.FromMinutes(10));
        _now = T0.AddHours(25);   // часы «вернулись»: успех из будущего становится «сейчас-ish» и через 20+ ч цикл возможен
        await svc.TickAsync();
        Assert.Empty(_hooks);   // зажат до _now, т.е. возраст 0
        _now = T0.AddHours(50);
        await svc.TickAsync();
        Assert.Equal(["pause", "reload", "resume"], _hooks);
    }
}
