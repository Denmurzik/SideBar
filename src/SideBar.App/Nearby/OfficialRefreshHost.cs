using SideBar.Core.Nearby;
using SideBar.Core.Official;
using SideBar.Official;

namespace SideBar.Sharing;

/// <summary>
/// Подключает фоновое обновление сертификатов (<see cref="RefreshService"/>) к приложению: пауза стека M4, перезагрузка
/// <see cref="IdentityProvider"/>, заметки в плитке. Результат показывается только для ручного запуска; фоновые отказы идут в лог.
/// Всё, что может блокировать (перезагрузка сертификатов, чтение лога), выполняется на потоке пула, не на UI.
/// </summary>
sealed class OfficialRefreshHost : IDisposable
{
    static readonly TimeSpan ResidentThreshold = TimeSpan.FromMinutes(10);

    readonly RefreshService _service;
    readonly Notifications _notes;
    readonly OfficialAppWatcher _watcher;
    readonly ResidentAppDetector _resident = new(ResidentThreshold);
    readonly object _residentGate = new();
    Timer? _residentTimer;

    /// <param name="officialExePath">Путь из настроек (Settings Task 3: <c>AppSettings.OfficialExePath</c>); null — стандартный. В Debug перекрывается SIDEBAR_OFFICIAL_EXE.</param>
    public OfficialRefreshHost(IStackPause stack, IdentityProvider identity, Notifications notes, OfficialAppWatcher watcher, string? officialExePath = null)
    {
        _notes = notes;
        _watcher = watcher;
        var app = OfficialAppController.ForApp(officialExePath, Log.Write);
        var hooks = new RefreshHooks
        {
            PauseStack = () => { stack.Pause(); return Task.CompletedTask; },
            ReloadCertificates = identity.Reload,   // зовётся на потоке пула (RunOnceAsync запускается через Task.Run)
            ResumeStack = stack.Resume,             // снимает только свою паузу: пока жив официальный процесс, держит OfficialAppWatcher
            WarnStale = () => notes.Info("refresh-stale", Strings.CertsStale),
        };
        _service = new RefreshService(app, new AppStateStore(StatePath()), hooks, Log.Write);
    }

    // %LocalAppData%\SideBar\state.json; SIDEBAR_DATA_DIR (автотест) уводит в свой каталог, как полку и лог
    static string StatePath()
    {
        var dir = Environment.GetEnvironmentVariable("SIDEBAR_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBar");
        return Path.Combine(dir, "state.json");
    }

    /// <summary>Идёт цикл обновления (пункт трея недоступен).</summary>
    public bool Busy => _service.IsBusy;

    public void Start()
    {
        _service.Start();
        _residentTimer = new Timer(_ =>
        {
            try { CheckResident(); }
            catch (Exception e) { Log.Write("official: resident check failed, " + Log.Describe(e)); }   // исключение из таймера уронило бы приложение
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>«Обновить сертификаты сейчас»: цикл на потоке пула, итог — заметкой в плитке.</summary>
    public async Task RefreshNowAsync()
    {
        string? text;
        try { text = Describe(await Task.Run(() => _service.RunOnceAsync())); }
        catch (OperationCanceledException) { return; }   // приложение закрывается
        catch (Exception e)
        {
            Log.Write("official: manual refresh failed, " + Log.Describe(e));
            text = Strings.RefreshFailed;
        }
        if (text is not null) _notes.Info("refresh", text);
    }

    static string? Describe(RefreshOutcome outcome) => RefreshNotes.For(outcome) switch
    {
        RefreshNote.Done => Strings.RefreshDone,
        RefreshNote.NotInstalled => Strings.RefreshNotInstalled,
        RefreshNote.OfficialOpen => Strings.RefreshOfficialOpen,
        RefreshNote.Deferred => Strings.RefreshDeferred,
        _ => null,
    };

    // Официальное приложение постоянно в трее: наш стек на паузе, цикл не идёт — скажем об этом один раз за запуск.
    void CheckResident()
    {
        ResidentNote note;
        lock (_residentGate)
        {
            bool ours = _watcher.Running && _service.OurLeftoverAlive();
            note = _resident.Observe(_watcher.Running, DateTimeOffset.UtcNow, _service.IsBusy, ours);
        }
        if (note == ResidentNote.None) return;
        Log.Write($"official app resident > 10 min, {note}");
        _notes.Info("official-resident", note == ResidentNote.OurLeftover ? Strings.OfficialLeftover : Strings.OfficialResident);
    }

    public void Dispose()
    {
        _residentTimer?.Dispose();
        _service.Dispose();
    }
}
