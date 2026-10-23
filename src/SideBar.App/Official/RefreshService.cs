using System.Diagnostics;
using SideBar.Core.Official;

namespace SideBar.Official;

/// <summary>Тайминги цикла; значения по умолчанию — боевые, тесты ужимают их.</summary>
internal sealed record RefreshTimings
{
    public TimeSpan MarkerTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Пауза после маркера перед закрытием: публикация и запись в leveldb успевают завершиться.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ExitWait { get; init; } = CloseRetryPolicy.ExitWait;
    public TimeSpan RetryDelay { get; init; } = CloseRetryPolicy.RetryDelay;
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Точки подключения собственного стека (M4); любая может быть null.</summary>
internal sealed class RefreshHooks
{
    /// <summary>Перед запуском официального приложения: остановить рекламу/приём.</summary>
    public Func<Task>? PauseStack { get; init; }
    /// <summary>После цикла (в том числе неудачного): перечитать сертификаты.</summary>
    public Action? ReloadCertificates { get; init; }
    /// <summary>После перезагрузки сертификатов: вернуть свой стек и перерегистрировать рекламу.</summary>
    public Action? ResumeStack { get; init; }
    /// <summary>&gt; 72 ч без успешного обновления: показать единственное уведомление.</summary>
    public Action? WarnStale { get; init; }
    /// <summary>Идёт приём или отправка: плановый цикл откладывается (иначе пауза сорвёт живую сессию). Ручной запуск не спрашивает.</summary>
    public Func<bool>? StackBusy { get; init; }
    /// <summary>Чужой экземпляр официального приложения работает непрерывно &gt; 10 мин: он сам обновляет сертификаты, предупреждать не нужно.</summary>
    public Func<bool>? UserAppResident { get; init; }
}

/// <summary>
/// Фоновое обновление сертификатов: раз в 5 мин <see cref="RefreshScheduler.Decide"/>, при «пора» — скрытый запуск
/// официального приложения, ожидание маркера публикации, мягкое закрытие. Лог приложения читается только на флаги, содержимое не пишется.
/// </summary>
internal sealed class RefreshService : IDisposable
{
    readonly OfficialAppController _app;
    readonly AppStateStore _store;
    readonly RefreshHooks _hooks;
    readonly Action<string> _log;
    readonly RefreshTimings _t;
    readonly Func<DateTimeOffset> _now;
    readonly Func<TimeSpan> _idle;
    readonly RefreshScheduler _scheduler = new();
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    int _busy;
    readonly object _stateGate = new();
    readonly object _leftoverGate = new();
    LaunchedProcess? _leftover;   // наш процесс, оставшийся после DidNotExit/UserTookOver; дескриптор открыт, поэтому PID не переиспользуется
    DateTimeOffset? _lastWarned;
    // Копия части state.json в памяти (под _stateGate): сбой чтения или записи файла не должен стирать успех и паузу после неудач.
    DateTimeOffset? _lastSuccess, _lastAttempt;
    int _failedAttempts;

    public RefreshService(OfficialAppController app, AppStateStore store, RefreshHooks hooks, Action<string> log,
        RefreshTimings? timings = null, Func<DateTimeOffset>? now = null, Func<TimeSpan>? idle = null)
    {
        _app = app;
        _store = store;
        _hooks = hooks;
        _log = log;
        _t = timings ?? new RefreshTimings();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _idle = idle ?? Native.IdleTime;
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    public void Dispose()
    {
        lock (_leftoverGate) { _leftover?.Dispose(); _leftover = null; }
        _cts.Cancel();   // идущий цикл (в том числе по пункту трея) тоже получает отмену; _cts не освобождаем: поздний вызов не должен падать на disposed
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }

    /// <summary>Цикл идёт прямо сейчас (для блокировки пункта трея).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    async Task LoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await Task.Delay(_t.StartDelay, ct);
            using var timer = new PeriodicTimer(_t.TickInterval);
            do { await TickAsync(ct); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log($"official: loop stopped, {Describe(e)}"); }
    }

    /// <summary>Один шаг расписания: если пора, цикл, затем 72-часовое предупреждение (после попытки: удачный цикл предупреждение отменяет). Не бросает (кроме отмены).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        try
        {
            var now = _now();
            AppState state;
            lock (_stateGate)   // чтение-изменение-запись state.json не должны перемежаться с RecordAttempt
            {
                var loaded = _store.Load();
                state = Merge(loaded.ClampedTo(now), now);
                if (state.FirstSeen is null) state = state with { FirstSeen = now };
                if (state != loaded) Save(state);   // зажатые «будущие» времена сохраняем, иначе они снова зажмутся к следующему now
            }

            var decision = _scheduler.Decide(now, state.LastSuccess, _idle(), _app.IsInstalled, _app.IsRunning());
            if (decision == RefreshDecision.RunNow
                && !RefreshScheduler.InBackoff(now, state.LastAttempt, state.FailedAttempts)   // после неудачи не чаще, чем раз в 1-2-4-8-16-20 ч
                && !Query(_hooks.StackBusy, "busy"))   // живая сессия приёма/отправки: цикл позже, не сейчас
                await RunOnceAsync(ct);

            WarnIfStale();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { _log($"official: tick failed, {Describe(e)}"); }
    }

    void WarnIfStale()
    {
        var now = _now();
        lock (_stateGate)
        {
            var state = Merge(_store.Load().ClampedTo(now), now);   // заново: цикл мог только что удаться
            // Без успехов опорой служит первое наблюдение: иначе при отсутствии приложения предупреждения не будет никогда.
            var anchor = state.LastSuccess ?? state.FirstSeen;
            var warned = (state.LastWarned, _lastWarned) switch { ({ } x, { } y) => x > y ? x : y, var (x, y) => x ?? y };   // и в памяти: сбой Save не должен повторять уведомление каждые 5 мин
            // Пока чужой экземпляр живёт в трее, он сам обновляет сертификаты, а наш LastSuccess стоит на месте.
            if (!_scheduler.ShouldWarn(now, anchor, warned) || Query(_hooks.UserAppResident, "resident")) return;
            _lastWarned = now;
            _log("official: no successful refresh for 72h, warning");
            try { _hooks.WarnStale?.Invoke(); } catch (Exception e) { _log($"official: warn hook {Describe(e)}"); }
            Save(state with { LastWarned = now });
        }
    }

    /// <summary>
    /// Один цикл обновления (расписание и пункт трея «Обновить сертификаты сейчас»). Параллельный вызов — <see cref="RefreshOutcome.CycleInProgress"/>.
    /// Отмена (токен вызова или остановка сервиса) бросает <see cref="OperationCanceledException"/>; перезагрузка сертификатов и возобновление стека тогда пропускаются.
    /// </summary>
    public async Task<RefreshOutcome> RunOnceAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return RefreshOutcome.CycleInProgress;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var outcome = await CycleAsync(linked.Token);
            _log($"official: refresh {outcome}");
            RecordAttempt(outcome);
            return outcome;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    async Task<RefreshOutcome> CycleAsync(CancellationToken ct)
    {
        if (!_app.IsInstalled) return RefreshOutcome.NotInstalled;
        if (_app.IsRunning()) return RefreshOutcome.OfficialAlreadyRunning;

        var scanner = new LogTailScanner(_app.LogDir);
        LaunchedProcess? proc = null;
        IReadOnlyDictionary<string, long>? snapshot = null;
        try
        {
            if (_hooks.PauseStack is { } pause) await pause();
            if (_app.IsRunning()) return RefreshOutcome.OfficialAlreadyRunning;   // пользователь мог открыть приложение, пока ставили паузу
            snapshot = scanner.Snapshot();
            if (snapshot is null)
            {
                _log("official: log dir unreadable, cycle aborted");   // без базы Scan принял бы старые логи за новые
                return RefreshOutcome.MarkerTimeout;
            }
            ct.ThrowIfCancellationRequested();   // отмена за время паузы: приложение не запускаем
            proc = _app.Launch();
            if (proc is null) return RefreshOutcome.NotInstalled;
            SetLeftover(null);

            var markerSeen = false;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < _t.MarkerTimeout)
            {
                var scan = scanner.Scan(snapshot);
                if (scan.UserActivity) return Leftover(proc, RefreshOutcome.UserTookOver);
                _app.Observe(proc);
                if (proc.SawVisible) return Leftover(proc, RefreshOutcome.UserTookOver);
                if (scan.MarkerSeen) { markerSeen = true; break; }
                if (proc.HasExited) return RefreshOutcome.MarkerTimeout;   // вышел сам, не опубликовав
                await Task.Delay(_t.Poll, ct);
            }

            await Task.Delay(_t.Settle, ct);
            var closed = await CloseAsync(proc, scanner, snapshot, ct);
            if (closed != CloseResult.Exited) SetLeftover(proc);
            return closed switch
            {
                CloseResult.UserTookOver => RefreshOutcome.UserTookOver,
                CloseResult.DidNotExit => RefreshOutcome.DidNotExit,
                _ => markerSeen ? RefreshOutcome.Succeeded : RefreshOutcome.MarkerTimeout,
            };
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested && proc is not null && snapshot is not null)
        {
            QuitOnStop(proc, scanner, snapshot);   // остановка сервиса: не оставляем наш скрытый экземпляр без единой попытки закрытия
            throw;
        }
        finally
        {
            lock (_leftoverGate) { if (proc is not null && !ReferenceEquals(proc, _leftover)) proc.Dispose(); }
            // Сертификаты могли измениться, даже если цикл не дошёл до конца. Любая отмена пропускает перезагрузку; стек возвращаем всегда,
            // кроме остановки сервиса (приложение закрывается). Отмена только токеном вызова стек всё равно возобновляет.
            if (!ct.IsCancellationRequested && proc is not null) Hook(_hooks.ReloadCertificates, "reload");
            if (!_cts.IsCancellationRequested) Hook(_hooks.ResumeStack, "resume");
        }
    }

    /// <summary>Одна просьба выйти без ожидания: если им не пользуются (нет видимого окна и активности в логе). Процесс дальше не трогаем.</summary>
    void QuitOnStop(LaunchedProcess proc, LogTailScanner scanner, IReadOnlyDictionary<string, long> snapshot)
    {
        try
        {
            if (proc.HasExited) return;
            _app.Observe(proc);
            if (proc.SawVisible || scanner.Scan(snapshot).UserActivity) return;
            _log($"official: stop quit, {_app.RequestQuit(proc)}");
        }
        catch (Exception e) { _log($"official: stop quit {Describe(e)}"); }
    }

    RefreshOutcome Leftover(LaunchedProcess proc, RefreshOutcome outcome)
    {
        SetLeftover(proc);
        return outcome;
    }

    void SetLeftover(LaunchedProcess? proc)
    {
        lock (_leftoverGate)
        {
            if (!ReferenceEquals(_leftover, proc)) _leftover?.Dispose();
            _leftover = proc;
        }
    }

    /// <summary>Жив ли оставшийся после неудачного цикла процесс, запущенный именно нами (по открытому дескриптору: PID не переиспользуется).</summary>
    public bool OurLeftoverAlive()
    {
        lock (_leftoverGate)
        {
            if (_leftover is null) return false;
            if (!_leftover.HasExited) return true;
            _leftover.Dispose();
            _leftover = null;
            return false;
        }
    }

    enum CloseResult { Exited, UserTookOver, DidNotExit }

    /// <summary>Закрытие нашего процесса: до 3 попыток (сразу, затем через 2 мин), перед каждой — проверка, что им не начали пользоваться.</summary>
    async Task<CloseResult> CloseAsync(LaunchedProcess proc, LogTailScanner scanner, IReadOnlyDictionary<string, long> snapshot, CancellationToken ct)
    {
        for (var attempt = 1; CloseRetryPolicy.DelayBefore(attempt) is { } delay; attempt++)
        {
            // расписание — CloseRetryPolicy; фактическая пауза берётся из таймингов (тесты ужимают)
            if (delay > TimeSpan.Zero && await WaitExitAsync(proc, _t.RetryDelay, ct)) return CloseResult.Exited;
            if (proc.HasExited) return CloseResult.Exited;
            _app.Observe(proc);
            if (proc.SawVisible) return CloseResult.UserTookOver;
            if (scanner.Scan(snapshot).UserActivity) return CloseResult.UserTookOver;
            ct.ThrowIfCancellationRequested();
            var quit = _app.RequestQuit(proc);
            if (quit == QuitResult.WindowsVisible) return CloseResult.UserTookOver;
            _log($"official: close attempt {attempt}, {quit}");
            if (await WaitExitAsync(proc, _t.ExitWait, ct)) return CloseResult.Exited;
            if (proc.SawVisible) return CloseResult.UserTookOver;
        }
        return CloseResult.DidNotExit;
    }

    async Task<bool> WaitExitAsync(LaunchedProcess proc, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!proc.HasExited)
        {
            _app.Observe(proc);
            if (proc.SawVisible || sw.Elapsed >= timeout) return false;   // видимое окно: ждать выхода незачем, процесс уже не наш
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, (timeout - sw.Elapsed).TotalMilliseconds))), ct);
        }
        return true;
    }

    /// <summary>
    /// Итог цикла в состояние: успех сбрасывает счётчик неудач, MarkerTimeout/UserTookOver/DidNotExit (и сбой запуска при установленном
    /// приложении) увеличивают его. OfficialAlreadyRunning и CycleInProgress попыткой не считаются.
    /// </summary>
    void RecordAttempt(RefreshOutcome outcome)
    {
        var success = outcome == RefreshOutcome.Succeeded;
        var failure = outcome is RefreshOutcome.MarkerTimeout or RefreshOutcome.UserTookOver or RefreshOutcome.DidNotExit
            || (outcome == RefreshOutcome.NotInstalled && _app.IsInstalled);
        if (!success && !failure) return;
        var now = _now();
        lock (_stateGate)
        {
            var state = Merge(_store.Load().ClampedTo(now), now);
            state = success
                ? state with { LastSuccess = now, LastAttempt = now, FailedAttempts = 0 }
                : state with { LastAttempt = now, FailedAttempts = Math.Min(state.FailedAttempts, 1000) + 1 };
            _lastSuccess = state.LastSuccess;
            _lastAttempt = state.LastAttempt;
            _failedAttempts = state.FailedAttempts;
            Save(state);
        }
    }

    /// <summary>Под _stateGate: состояние из файла, дополненное копией в памяти (по максимуму времени), и обратно в память.</summary>
    AppState Merge(AppState state, DateTimeOffset now)
    {
        var memSuccess = _lastSuccess is { } ms && ms > now ? now : _lastSuccess;
        var memAttempt = _lastAttempt is { } ma && ma > now ? now : _lastAttempt;
        if (memSuccess is { } a && (state.LastSuccess is not { } b || a > b)) state = state with { LastSuccess = a };
        // пара «время попытки + счётчик» берётся целиком у более свежей стороны
        if (memAttempt is { } t && (state.LastAttempt is not { } f || t >= f)) state = state with { LastAttempt = t, FailedAttempts = _failedAttempts };
        _lastSuccess = state.LastSuccess;
        _lastAttempt = state.LastAttempt;
        _failedAttempts = state.FailedAttempts;
        return state;
    }

    bool Query(Func<bool>? hook, string name)
    {
        if (hook is null) return false;
        try { return hook(); } catch (Exception e) { _log($"official: {name} hook {Describe(e)}"); return false; }
    }

    void Save(AppState state)
    {
        try { _store.Save(state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"official: state save {Describe(e)}"); }
    }

    void Hook(Action? hook, string name)
    {
        if (hook is null) return;
        try { hook(); } catch (Exception e) { _log($"official: {name} hook {Describe(e)}"); }
    }

    /// <summary>Тип и HResult без Message: в них бывают пути.</summary>
    static string Describe(Exception e) => $"{e.GetType().Name} 0x{e.HResult:X8}";
}
