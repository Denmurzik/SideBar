namespace SideBar.Core.Updates;

/// <summary>
/// Логика проверки обновлений без сети и таймеров (тестируется): политика 24 ч, запись времени проверки, сохранение и восстановление найденного релиза.
/// Автоматическая проверка (<see cref="TickAsync"/>) пишет LastCheck при любом исходе, включая сбой сети: не чаще одного автоматического запроса в 24 ч.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateStateStore _state;
    private readonly Version _current;
    private readonly Func<CancellationToken, Task<(int Status, byte[]? Body)>> _fetch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _log;
    private readonly DateTimeOffset _startedAt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ReleaseInfo? _found;

    public UpdateService(UpdateStateStore state, Version current, Func<CancellationToken, Task<(int Status, byte[]? Body)>> fetch,
        Func<DateTimeOffset> clock, Func<bool> enabled, Action<string>? log = null)
    {
        _state = state;
        _current = current;
        _fetch = fetch;
        _clock = clock;
        _enabled = enabled;
        _log = log ?? (_ => { });
        _startedAt = clock();
    }

    /// <summary>Релиз новее текущей версии для пункта трея; при выключенной настройке — null.</summary>
    public ReleaseInfo? Available => _enabled() ? _found : null;

    /// <summary>Восстанавливает сохранённый релиз: он снова проходит проверку тега/адреса и «новее текущей», иначе отбрасывается (и стирается из файла).</summary>
    public void RestorePersisted()
    {
        var st = _state.Load();
        if (st.Tag is null && st.HtmlUrl is null) return;
        if (ReleaseInfo.TryCreate(st.Tag, st.HtmlUrl, out var info) && AppVersion.IsNewer(_current, info.Version)) { _found = info; return; }
        _state.Save(st with { Tag = null, HtmlUrl = null });
    }

    /// <summary>Тик таймера: выключено или рано по политике — запроса нет.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!_enabled()) return;
        if (!UpdatePolicy.ShouldCheck(_clock(), _startedAt, _state.LastCheck)) return;
        await CheckAsync(ct);
    }

    /// <summary>Проверка вне политики (кнопка «Проверить сейчас»): работает и при выключенной настройке. Не бросает.</summary>
    public async Task<UpdateOutcome> CheckAsync(CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return new UpdateOutcome(UpdateKind.Failed, Detail: "canceled"); }
        try
        {
            UpdateOutcome outcome;
            try
            {
                var (status, body) = await _fetch(ct);
                outcome = UpdateOutcome.Evaluate(status, body, _current);
            }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested) return new UpdateOutcome(UpdateKind.Failed, Detail: "canceled");
                outcome = new UpdateOutcome(UpdateKind.Failed, Detail: e.GetType().Name);   // в лог — только тип ошибки
            }
            _log(outcome.LogLine);

            // найденное обновление снимают только «последняя версия» и 404; сбой его сохраняет
            var persisted = _state.Load();
            switch (outcome.Kind)
            {
                case UpdateKind.Available:
                    _found = outcome.Release;
                    persisted = persisted with { Tag = outcome.Release!.Tag, HtmlUrl = outcome.Release.HtmlUrl };
                    break;
                case UpdateKind.UpToDate or UpdateKind.NotFound:
                    _found = null;
                    persisted = persisted with { Tag = null, HtmlUrl = null };
                    break;
            }
            _state.Save(persisted with { LastCheck = _clock() });
            return outcome;
        }
        finally { _gate.Release(); }
    }
}
