using System.Diagnostics;
using SideBar.Core.Official;

namespace SideBar.Official;

internal enum RefreshOutcome { Succeeded, NotInstalled, AlreadyRunning, MarkerTimeout, UserTookOver, DidNotExit }

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
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _cts.Dispose();
    }

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

    /// <summary>Один шаг расписания: 72-часовое предупреждение и, если пора, цикл. Не бросает (кроме отмены).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        try
        {
            var now = _now();
            var loaded = _store.Load();
            var state = loaded.ClampedTo(now);
            if (state.FirstSeen is null) state = state with { FirstSeen = now };
            if (state != loaded) Save(state);   // зажатые «будущие» времена сохраняем, иначе они снова зажмутся к следующему now

            // Без успехов опорой служит первое наблюдение: иначе при отсутствии приложения предупреждения не будет никогда.
            var anchor = state.LastSuccess ?? state.FirstSeen;
            if (_scheduler.ShouldWarn(now, anchor, state.LastWarned))
            {
                _log("official: no successful refresh for 72h, warning");
                try { _hooks.WarnStale?.Invoke(); } catch (Exception e) { _log($"official: warn hook {Describe(e)}"); }
                state = state with { LastWarned = now };
                Save(state);
            }

            var decision = _scheduler.Decide(now, state.LastSuccess, _idle(), _app.IsInstalled, _app.IsRunning());
            if (decision == RefreshDecision.RunNow) await RunOnceAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { _log($"official: tick failed, {Describe(e)}"); }
    }

    /// <summary>Один цикл обновления (расписание и пункт трея «Обновить сертификаты сейчас»). Параллельный вызов — <see cref="RefreshOutcome.AlreadyRunning"/>.</summary>
    public async Task<RefreshOutcome> RunOnceAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return RefreshOutcome.AlreadyRunning;
        try
        {
            var outcome = await CycleAsync(ct);
            _log($"official: refresh {outcome}");
            if (outcome == RefreshOutcome.Succeeded) RecordSuccess();
            return outcome;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    async Task<RefreshOutcome> CycleAsync(CancellationToken ct)
    {
        if (!_app.IsInstalled) return RefreshOutcome.NotInstalled;
        if (_app.IsRunning()) return RefreshOutcome.AlreadyRunning;

        var scanner = new LogTailScanner(_app.LogDir);
        LaunchedProcess? proc = null;
        try
        {
            if (_hooks.PauseStack is { } pause) await pause();
            var snapshot = scanner.Snapshot();
            proc = _app.Launch();
            if (proc is null) return RefreshOutcome.NotInstalled;

            var markerSeen = false;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < _t.MarkerTimeout)
            {
                var scan = scanner.Scan(snapshot);
                if (scan.UserActivity) return RefreshOutcome.UserTookOver;
                if (scan.MarkerSeen) { markerSeen = true; break; }
                if (proc.HasExited) return RefreshOutcome.MarkerTimeout;   // вышел сам, не опубликовав
                await Task.Delay(_t.Poll, ct);
            }

            await Task.Delay(_t.Settle, ct);
            var closed = await CloseAsync(proc, scanner, snapshot, ct);
            return closed switch
            {
                CloseResult.UserTookOver => RefreshOutcome.UserTookOver,
                CloseResult.DidNotExit => RefreshOutcome.DidNotExit,
                _ => markerSeen ? RefreshOutcome.Succeeded : RefreshOutcome.MarkerTimeout,
            };
        }
        finally
        {
            proc?.Dispose();
            // Сертификаты могли измениться, даже если цикл не дошёл до конца; стек возвращаем всегда.
            if (proc is not null) Hook(_hooks.ReloadCertificates, "reload");
            Hook(_hooks.ResumeStack, "resume");
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
            if (scanner.Scan(snapshot).UserActivity) return CloseResult.UserTookOver;
            var quit = _app.RequestQuit(proc);
            if (quit == QuitResult.WindowsVisible) return CloseResult.UserTookOver;
            _log($"official: close attempt {attempt}, {quit}");
            if (await WaitExitAsync(proc, _t.ExitWait, ct)) return CloseResult.Exited;
        }
        return CloseResult.DidNotExit;
    }

    async Task<bool> WaitExitAsync(LaunchedProcess proc, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!proc.HasExited)
        {
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, (timeout - sw.Elapsed).TotalMilliseconds))), ct);
        }
        return true;
    }

    void RecordSuccess()
    {
        var now = _now();
        Save((_store.Load() with { LastSuccess = now }).ClampedTo(now));
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
