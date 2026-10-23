using System.Reflection;
using System.Runtime.InteropServices;
using SideBar.Core.Settings;
using SideBar.Core.Updates;

namespace SideBar.Updates;

/// <summary>
/// Проверка новой версии на GitHub: раз в час таймер спрашивает <see cref="UpdatePolicy.ShouldCheck"/> (не чаще раза в 24 ч, не раньше 2 мин после старта).
/// Без токенов; любой неуспех (сеть, 404 у приватного репозитория, разбор) тих — в лог идёт только тип итога. Выключатель — AppSettings.CheckForUpdates.
/// </summary>
sealed partial class UpdateChecker : IDisposable
{
    const string Url = "https://api.github.com/repos/Denmurzik/SideBar/releases/latest";
    static readonly TimeSpan Tick = TimeSpan.FromHours(1), RequestTimeout = TimeSpan.FromSeconds(10);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecute(nint hwnd, string verb, string file, string? parameters, string? directory, int show);

    readonly SettingsStore _settings;
    readonly UpdateStateStore _state;
    readonly Version? _current;
    readonly string _versionText;
    readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);
    Timer? _timer;
    volatile ReleaseInfo? _available;
    bool _disposed;

    /// <summary>Версия сборки из AssemblyInformationalVersion («0.1.0+hash» → 0.1.0); не из GetName().Version (под AOT бывает 1.0.0).</summary>
    public static Version? CurrentVersion { get; } =
        AppVersion.TryParse(typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var v) ? v : null;

    /// <summary>Найденный релиз новее текущей версии (для пункта трея), иначе null.</summary>
    public ReleaseInfo? Available => _available;

    public UpdateChecker(SettingsStore settings, UpdateStateStore state)
    {
        _settings = settings;
        _state = state;
        _current = CurrentVersion;
        _versionText = _current?.ToString() ?? "";
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SideBar/" + (_versionText.Length > 0 ? _versionText : "0.0.0"));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public void Start()
    {
        if (_current is null) { Log.Write("update: version unknown, checks disabled"); return; }
        _settings.Changed += OnSettings;
        Apply(first: true);
    }

    void OnSettings(AppSettings _) => Apply(first: false);

    void Apply(bool first)
    {
        if (_disposed) return;
        if (!_settings.Current.CheckForUpdates) { _timer?.Dispose(); _timer = null; return; }
        if (_timer is not null) return;
        // первая проверка — после льготных 2 мин (DEBUG: SIDEBAR_UPDATE_NOW=1 — через секунду); включённая позже — почти сразу, ShouldCheck всё равно решает
        var due = first ? UpdatePolicy.StartupGrace + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
#if DEBUG
        if (Environment.GetEnvironmentVariable("SIDEBAR_UPDATE_NOW") == "1") due = TimeSpan.FromSeconds(1);
#endif
        _timer = new Timer(_ => _ = OnTick(), null, due, Tick);
    }

    async Task OnTick()
    {
        try
        {
            if (!_settings.Current.CheckForUpdates) return;
            var now = DateTimeOffset.UtcNow;
#if DEBUG
            var forced = Environment.GetEnvironmentVariable("SIDEBAR_UPDATE_NOW") == "1";
#else
            const bool forced = false;
#endif
            if (!forced && !UpdatePolicy.ShouldCheck(now, _startedAt, _state.LastCheck)) return;
            await CheckNowAsync();
        }
        catch (Exception e) { Log.Write("update: failed (" + e.GetType().Name + ")"); }
    }

    /// <summary>Проверка вне политики 24 ч (кнопка «Проверить сейчас»). Не бросает.</summary>
    public async Task<UpdateOutcome> CheckNowAsync()
    {
        if (_current is not { } current || _disposed) return new UpdateOutcome(UpdateKind.Failed, Detail: "disabled");
        await _gate.WaitAsync();
        try
        {
            var (outcome, network) = await RequestAsync(current);
            Log.Write(outcome.LogLine);
            // сетевой сбой повторяется со следующим тиком; любой ответ сервера (в т. ч. 404) — следующая попытка через 24 ч
            if (!network) _state.SaveLastCheck(DateTimeOffset.UtcNow);
            _available = outcome.Kind == UpdateKind.Available ? outcome.Release : null;
            return outcome;
        }
        finally { _gate.Release(); }
    }

    async Task<(UpdateOutcome Outcome, bool Network)> RequestAsync(Version current)
    {
        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await _http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (status != 200) return (UpdateOutcome.Evaluate(status, default, current), false);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[ReleaseInfo.MaxJsonBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token);
                if (n == 0) break;
                total += n;
            }
            return (UpdateOutcome.Evaluate(status, buffer.AsSpan(0, total), current), false);
        }
        catch (Exception e)
        {
            return (new UpdateOutcome(UpdateKind.Failed, Detail: e.GetType().Name), true);   // в лог — только тип ошибки
        }
    }

    /// <summary>Открывает страницу релиза; адрес уже проверен <see cref="ReleaseInfo"/> (https://github.com/Denmurzik/SideBar/releases/…).</summary>
    public static void OpenRelease(ReleaseInfo release) => ShellExecute(0, "open", release.HtmlUrl, null, null, 1);

    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnSettings;
        _timer?.Dispose();
        _http.Dispose();
    }
}
