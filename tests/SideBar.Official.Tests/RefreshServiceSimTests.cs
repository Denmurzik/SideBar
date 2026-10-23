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
    const string NeverRuns = "sidebar_test_never_runs";   // вместо nearby_share: настоящий Quick Share на машине не влияет на тесты
    static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "SideBar.Tests", Guid.NewGuid().ToString("N"));
    readonly List<string> _log = [];
    readonly List<string> _hooks = [];
    readonly List<RefreshService> _services = [];
    DateTimeOffset _now = T0;
    readonly Stopwatch _sw = Stopwatch.StartNew();

    public RefreshServiceSimTests()
    {
        Directory.CreateDirectory(_dir);
        CloseSims();   // остатки прошлого теста (в CI закрытие окна иногда не успевает)
        Assert.Empty(SimProcesses());   // чужих симуляторов быть не должно
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SIDEBAR_SIM_MODE", null);
        foreach (var svc in _services) { try { svc.Dispose(); } catch (Exception) { } }   // цикл сервиса мог ещё держать симулятор
        CloseSims();
        for (var i = 0; i < 100 && SimProcesses().Length > 0; i++) Thread.Sleep(100);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static string SimExe()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tools", "SideBar.OfficialSim"))) d = d.Parent;
        Assert.NotNull(d);
        // та же конфигурация и TFM, что у сборки тестов: bin/<Config>/<tfm>/
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var exe = Path.Combine(d.FullName, "tools", "SideBar.OfficialSim", "bin", tfmDir.Parent!.Name, tfmDir.Name, SimName + ".exe");
        Assert.True(File.Exists(exe), exe);
        return exe;
    }

    static Process[] SimProcesses() => Process.GetProcessesByName(SimName);

    // Симулятор — наш процесс: закрываем его WM_CLOSE окну (не TerminateProcess).
    static void CloseSims()
    {
        foreach (var p in SimProcesses())
        {
            foreach (var w in Native.WindowsOf(p.Id)) PostMessageW(w.Hwnd, 0x0010, 0, 0);
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }   // свой симулятор теста; закрытие окном не сработало
                p.WaitForExit(5000);
            }
            p.Dispose();
        }
    }

    [DllImport("user32.dll")] static extern int PostMessageW(nint hwnd, uint msg, nint w, nint l);

    string SharingDir => Path.Combine(_dir, "Sharing");
    string StatePath => Path.Combine(_dir, "state.json");

    static void SetEnv(string mode, string sharingDir, string exe)
    {
        Environment.SetEnvironmentVariable("SIDEBAR_SIM_MODE", mode);
        Environment.SetEnvironmentVariable("SIDEBAR_SHARING_DIR", sharingDir);
        Environment.SetEnvironmentVariable("SIDEBAR_OFFICIAL_EXE", exe);
    }

    static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(50); }
        return true;
    }

    RefreshService Make(string mode, RefreshTimings? timings = null, string? exe = null, Func<TimeSpan>? idle = null)
    {
        SetEnv(mode, SharingDir, exe ?? SimExe());
        var app = OfficialAppController.FromEnvironment(s => _log.Add($"{_sw.ElapsedMilliseconds}ms " + s), null, NeverRuns);
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
        var made = new RefreshService(app, new AppStateStore(StatePath), hooks, s => _log.Add($"{_sw.ElapsedMilliseconds}ms " + s), t, () => _now, idle);
        _services.Add(made);
        return made;
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
    public async Task Parallel_call_reports_cycle_in_progress()
    {
        var svc = Make("normal");
        var first = svc.RunOnceAsync();
        Assert.True(svc.IsBusy);
        Assert.Equal(RefreshOutcome.CycleInProgress, await svc.RunOnceAsync());
        Assert.Equal(RefreshOutcome.Succeeded, await first);
        Assert.False(svc.IsBusy);
    }

    [Fact]
    public async Task Service_stop_skips_reload_and_resume_and_leaves_process()
    {
        var svc = Make("silent");
        var run = svc.RunOnceAsync();
        Assert.True(WaitFor(() => SimProcesses().Any(p => Native.WindowsOf(p.Id).Count > 0)));   // окно уже есть: Dispose теста сможет закрыть симулятор
        svc.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["pause"], _hooks);   // приложение закрывается: ни перезагрузки, ни возобновления
        Assert.Contains(_log, l => l.Contains("stop quit, Posted"));   // одна просьба выйти, без ожидания и без убийства
        Assert.True(WaitFor(() => SimProcesses().Length == 0));   // симулятор послушался WM_QUIT
        Assert.False(svc.IsBusy);
    }

    [Fact]
    public async Task Caller_cancellation_resumes_stack_but_skips_reload()
    {
        var svc = Make("silent");
        using var cts = new CancellationTokenSource();
        var run = svc.RunOnceAsync(cts.Token);
        Assert.True(WaitFor(() => SimProcesses().Any(p => Native.WindowsOf(p.Id).Count > 0)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["pause", "resume"], _hooks);
        Assert.Single(SimProcesses());
    }

    [Fact]
    public async Task Already_cancelled_token_never_launches()
    {
        var svc = Make("normal");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RunOnceAsync(cts.Token));
        Assert.Empty(SimProcesses());
        Assert.Equal(["pause", "resume"], _hooks);
    }

    [Fact]
    public async Task Leftover_process_is_reported_as_ours()
    {
        var svc = Make("hung", new RefreshTimings { MarkerTimeout = TimeSpan.FromSeconds(10), Settle = TimeSpan.FromMilliseconds(100), ExitWait = TimeSpan.FromMilliseconds(300), RetryDelay = TimeSpan.FromMilliseconds(300) });
        Assert.Equal(RefreshOutcome.DidNotExit, await svc.RunOnceAsync());
        Assert.True(svc.OurLeftoverAlive());
        CloseSims();   // оставшийся процесс вышел
        Assert.True(WaitFor(() => !svc.OurLeftoverAlive()));
        Assert.False(svc.OurLeftoverAlive());
    }

    [Fact]
    public async Task Already_running_is_not_launched_again()
    {
        var first = Make("user");
        Assert.Equal(RefreshOutcome.UserTookOver, await first.RunOnceAsync());   // оставляет один живой симулятор
        var logs = Directory.GetFiles(Path.Combine(SharingDir, "Logs")).Length;
        _hooks.Clear();

        var second = Make("normal");
        Assert.Equal(RefreshOutcome.OfficialAlreadyRunning, await second.RunOnceAsync());
        Assert.Single(SimProcesses());
        Assert.Equal(logs, Directory.GetFiles(Path.Combine(SharingDir, "Logs")).Length);
        Assert.Empty(_hooks);   // стек не трогали
    }

    [Fact]
    public void Quit_is_scoped_to_our_pid_and_leaves_another_instance_alone()
    {
        SetEnv("normal", SharingDir, SimExe());
        var app = OfficialAppController.FromEnvironment(s => _log.Add(s), null, NeverRuns);
        using var other = Process.Start(new ProcessStartInfo(SimExe()) { UseShellExecute = false, CreateNoWindow = true })!;   // «чужой» экземпляр
        using var ours = app.Launch()!;
        Assert.True(WaitFor(() => Native.WindowsOf(ours.Pid).Any(w => w.ClassName == OfficialAppController.WindowClass)));
        Assert.True(WaitFor(() => Native.WindowsOf(other.Id).Any(w => w.ClassName == OfficialAppController.WindowClass)));

        Assert.Equal(QuitResult.Posted, app.RequestQuit(ours));
        Assert.True(WaitFor(() => ours.HasExited));
        Assert.False(other.HasExited);
        Assert.Contains(Native.WindowsOf(other.Id), w => w.ClassName == OfficialAppController.WindowClass);
    }

    [Fact]
    public async Task Briefly_visible_window_latches_and_we_never_quit()
    {
        var svc = Make("show");
        Assert.Equal(RefreshOutcome.UserTookOver, await svc.RunOnceAsync());
        Assert.Single(SimProcesses());
        Assert.DoesNotContain(_log, l => l.Contains("close attempt"));
        Assert.Null(new AppStateStore(StatePath).Load().LastSuccess);
        Assert.Contains(_log, l => l.Contains("visible window latched, class " + OfficialAppController.WindowClass));   // класс окна в логе
    }

    [Fact]
    public async Task Service_stop_after_user_activity_leaves_process_alone()
    {
        var svc = Make("user");
        Assert.Equal(RefreshOutcome.UserTookOver, await svc.RunOnceAsync());
        svc.Dispose();
        Assert.DoesNotContain(_log, l => l.Contains("stop quit"));
        Assert.Single(SimProcesses());
    }

    [Fact]
    public async Task Unreadable_log_dir_aborts_cycle_without_launching()
    {
        Directory.CreateDirectory(SharingDir);
        File.WriteAllText(Path.Combine(SharingDir, "Logs"), "not a dir");   // вместо каталога логов - файл
        var svc = Make("normal");
        Assert.Equal(RefreshOutcome.MarkerTimeout, await svc.RunOnceAsync());
        Assert.Empty(SimProcesses());
        Assert.Equal(["pause", "resume"], _hooks);   // пауза снята, сертификаты не перечитывались
        var state = new AppStateStore(StatePath).Load();
        Assert.Null(state.LastSuccess);
        Assert.Equal(1, state.FailedAttempts);   // считается попыткой
    }

    [Fact]
    public async Task Failed_cycle_backs_off_1h_then_retries_and_manual_run_bypasses_it()
    {
        var silent = new RefreshTimings
        {
            MarkerTimeout = TimeSpan.FromSeconds(1),
            Settle = TimeSpan.FromMilliseconds(100),
            ExitWait = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
        };
        var svc = Make("silent", silent);
        await svc.TickAsync();   // T0: MarkerTimeout
        Assert.Equal(["pause", "reload", "resume"], _hooks);
        Assert.Contains(_log, l => l.Contains("refresh MarkerTimeout"));
        Assert.Empty(SimProcesses());

        _hooks.Clear();
        _now = T0.AddMinutes(5);
        await svc.TickAsync();
        Assert.Empty(_hooks);   // в паузе: ничего не запускали

        _now = T0.AddMinutes(59);
        await svc.TickAsync();
        Assert.Empty(_hooks);

        Assert.Equal(RefreshOutcome.MarkerTimeout, await svc.RunOnceAsync());   // ручной запуск паузу обходит
        Assert.Equal(["pause", "reload", "resume"], _hooks);
        Assert.Equal(2, new AppStateStore(StatePath).Load().FailedAttempts);

        // новый экземпляр сервиса (перезапуск SideBar) читает паузу из файла: попытка была в T0+59 мин, пауза теперь 2 ч
        _hooks.Clear();
        _now = T0.AddMinutes(59 + 90);
        var restarted = Make("silent", silent);
        await restarted.TickAsync();
        Assert.Empty(_hooks);

        _now = T0.AddMinutes(59 + 121);
        await restarted.TickAsync();
        Assert.Equal(["pause", "reload", "resume"], _hooks);   // пауза прошла: запускает снова
    }

    [Fact]
    public async Task Backoff_survives_unwritable_state_file_via_memory()
    {
        var svc = Make("silent", new RefreshTimings
        {
            MarkerTimeout = TimeSpan.FromSeconds(1),
            Settle = TimeSpan.FromMilliseconds(100),
            ExitWait = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
        });
        Directory.CreateDirectory(StatePath);   // state.json - каталог: Load даёт пустое состояние, Save не удаётся
        await svc.TickAsync();
        Assert.Equal(["pause", "reload", "resume"], _hooks);
        _hooks.Clear();
        _now = T0.AddMinutes(5);
        await svc.TickAsync();
        Assert.Empty(_hooks);   // без файла пауза держится по памяти
    }

    [Fact]
    public async Task Busy_stack_defers_scheduled_cycle_but_not_manual_run()
    {
        var busy = true;
        SetEnv("normal", SharingDir, SimExe());
        var app = OfficialAppController.FromEnvironment(s => _log.Add(s), null, NeverRuns);
        var hooks = new RefreshHooks
        {
            PauseStack = () => { _hooks.Add("pause"); return Task.CompletedTask; },
            ReloadCertificates = () => _hooks.Add("reload"),
            ResumeStack = () => _hooks.Add("resume"),
            StackBusy = () => busy,
        };
        var svc = new RefreshService(app, new AppStateStore(StatePath), hooks, s => _log.Add(s),
            new RefreshTimings { MarkerTimeout = TimeSpan.FromSeconds(10), Settle = TimeSpan.FromMilliseconds(100), ExitWait = TimeSpan.FromSeconds(5) }, () => _now, () => TimeSpan.FromMinutes(10));
        _services.Add(svc);
        await svc.TickAsync();
        Assert.Empty(_hooks);   // сессия идёт: ждём
        Assert.Equal(RefreshOutcome.Succeeded, await svc.RunOnceAsync());   // ручной запуск не спрашивает
        Assert.Equal(["pause", "reload", "resume"], _hooks);

        busy = false;
        _hooks.Clear();
        _now = T0.AddHours(25);
        await svc.TickAsync();
        Assert.Equal(["pause", "reload", "resume"], _hooks);
    }

    [Fact]
    public async Task Resident_user_app_suppresses_stale_warning()
    {
        var resident = true;
        SetEnv("normal", SharingDir, Path.Combine(_dir, "missing", "nearby_share.exe"));
        var app = OfficialAppController.FromEnvironment(s => _log.Add(s), null, NeverRuns);
        var hooks = new RefreshHooks { WarnStale = () => _hooks.Add("warn"), UserAppResident = () => resident };
        var svc = new RefreshService(app, new AppStateStore(StatePath), hooks, s => _log.Add(s), null, () => _now);
        _services.Add(svc);
        await svc.TickAsync();   // FirstSeen
        _now = T0.AddHours(80);
        await svc.TickAsync();
        Assert.Empty(_hooks);   // чужой экземпляр в трее: не предупреждаем
        resident = false;
        await svc.TickAsync();
        Assert.Equal(["warn"], _hooks);
    }

    [Fact]
    public async Task Stale_warning_is_decided_after_the_cycle_attempt()
    {
        new AppStateStore(StatePath).Save(new AppState { LastSuccess = T0, FirstSeen = T0 });
        _now = T0.AddHours(80);   // > 72 ч без успеха, но цикл сейчас удастся
        var svc = Make("normal", idle: () => TimeSpan.FromMinutes(10));
        await svc.TickAsync();
        Assert.Equal(["pause", "reload", "resume"], _hooks);   // без warn: успех отменил предупреждение
        Assert.Equal(_now, new AppStateStore(StatePath).Load().LastSuccess);

        // а если цикл не удался, предупреждение приходит в том же тике
        _hooks.Clear();
        new AppStateStore(StatePath).Save(new AppState { LastSuccess = T0, FirstSeen = T0 });
        var failing = Make("silent", new RefreshTimings
        {
            MarkerTimeout = TimeSpan.FromSeconds(1),
            Settle = TimeSpan.FromMilliseconds(100),
            ExitWait = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
        }, idle: () => TimeSpan.FromMinutes(10));
        await failing.TickAsync();
        Assert.Equal(["pause", "reload", "resume", "warn"], _hooks);
    }

    [Fact]
    public void Is_running_sees_the_other_process_name_when_configured_exe_is_named_differently()
    {
        SetEnv("normal", SharingDir, SimExe());
        var starter = OfficialAppController.FromEnvironment(_ => { }, null, NeverRuns);
        using var running = starter.Launch()!;
        Assert.True(WaitFor(() => Native.WindowsOf(running.Pid).Count > 0));   // окно уже есть: Dispose теста сможет закрыть симулятор
        var custom = Path.Combine(_dir, "custom_name.exe");
        Assert.True(new OfficialAppController(custom, SharingDir, _ => { }, SimName).IsRunning());   // процесс назван SimName, настроенный exe - custom_name
        Assert.False(new OfficialAppController(custom, SharingDir, _ => { }, NeverRuns).IsRunning());
        Assert.False(new OfficialAppController(custom, SharingDir, _ => { }, null).IsRunning());
    }

    [Fact]
    public async Task Saved_warning_survives_restart_and_is_not_repeated()
    {
        new AppStateStore(StatePath).Save(new AppState { FirstSeen = T0, LastWarned = T0.AddHours(73) });
        _now = T0.AddHours(80);
        var svc = Make("normal", exe: Path.Combine(_dir, "missing", "nearby_share.exe"));   // новый экземпляр сервиса = перезапуск SideBar
        await svc.TickAsync();
        Assert.Empty(_hooks);
    }

    [Fact]
    public async Task Marker_from_before_launch_is_not_success()
    {
        var logs = Path.Combine(SharingDir, "Logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "nearby_share_2020-01-01-000000.log"), "I0101 PublishDevice succeeded. contact_removed: false\n");
        var svc = Make("silent", new RefreshTimings
        {
            MarkerTimeout = TimeSpan.FromSeconds(1),
            Settle = TimeSpan.FromMilliseconds(100),
            ExitWait = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
        });
        Assert.Equal(RefreshOutcome.MarkerTimeout, await svc.RunOnceAsync());
        Assert.Null(new AppStateStore(StatePath).Load().LastSuccess);
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
    public async Task Official_path_missing_is_not_installed()
    {
        var oldExe = Environment.GetEnvironmentVariable("SIDEBAR_OFFICIAL_EXE");   // другие тесты оставляют его в процессе; в Debug он приоритетнее настройки
        Environment.SetEnvironmentVariable("SIDEBAR_OFFICIAL_EXE", null);
        try { await OfficialPathMissingScenario(); }
        finally { Environment.SetEnvironmentVariable("SIDEBAR_OFFICIAL_EXE", oldExe); }
    }

    async Task OfficialPathMissingScenario()
    {
        var present = Path.Combine(_dir, "nearby_share.exe");
        File.WriteAllText(present, "x");
        var missing = Path.Combine(_dir, "gone", "nearby_share.exe");

        var app = OfficialAppController.FromEnvironment(s => _log.Add(s), present, NeverRuns);
        Assert.True(app.IsInstalled);
        app.UseSettingsPath(missing);   // настройка сменилась на ходу
        Assert.False(app.IsInstalled);
        Assert.Equal(missing, app.ExePath);
        app.UseSettingsPath(@"nearby_share.exe");   // относительный путь не считается установленным
        Assert.False(app.IsInstalled);
        app.UseSettingsPath(present);
        Assert.True(app.IsInstalled);
        Assert.Equal("nearby_share", OfficialAppController.UserProcessName);   // процесс пользователя не зависит от пути

        var svc = Make("normal", exe: missing);
        Assert.Equal(RefreshOutcome.NotInstalled, await svc.RunOnceAsync());
        Assert.Empty(_hooks);
        Assert.Empty(SimProcesses());   // ничего не запускалось
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
