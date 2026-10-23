using System.Reflection;
using System.Runtime.InteropServices;
using SideBar.Core.Settings;
using SideBar.Core.Updates;

namespace SideBar.Updates;

/// <summary>
/// Оболочка над <see cref="UpdateService"/>: HttpClient, почасовой таймер, выключатель AppSettings.CheckForUpdates.
/// Без токенов; любой неуспех (сеть, 404 у приватного репозитория, разбор) тих — в лог идёт только тип итога.
/// </summary>
sealed partial class UpdateChecker : IDisposable
{
    const string Url = "https://api.github.com/repos/Denmurzik/SideBar/releases/latest";
    static readonly TimeSpan Tick = TimeSpan.FromHours(1), RequestTimeout = TimeSpan.FromSeconds(10);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecute(nint hwnd, string verb, string file, string? parameters, string? directory, int show);

    readonly SettingsStore _settings;
    readonly UpdateService? _service;
    readonly HttpClient _http;
    readonly CancellationTokenSource _cts = new();
    readonly object _lock = new();
    Timer? _timer;
    volatile bool _disposed;

    /// <summary>Версия сборки из AssemblyInformationalVersion («0.1.0+hash» → 0.1.0); не из GetName().Version (под AOT бывает 1.0.0).</summary>
    public static Version? CurrentVersion { get; } =
        AppVersion.TryParse(typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var v) ? v : null;

    /// <summary>Найденный релиз новее текущей версии (для пункта трея), иначе null; при выключенной проверке — null.</summary>
    public ReleaseInfo? Available => _service?.Available;

    public UpdateChecker(SettingsStore settings, UpdateStateStore state)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SideBar/" + (CurrentVersion?.ToString() ?? "0.0.0"));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (CurrentVersion is { } cur)
            _service = new UpdateService(state, cur, FetchAsync, () => DateTimeOffset.UtcNow, () => _settings.Current.CheckForUpdates, Log.Write);
    }

    async Task<(int Status, byte[]? Body)> FetchAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(RequestTimeout);
        using var response = await _http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        var status = (int)response.StatusCode;
        if (status != 200) return (status, null);
        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        var buffer = new byte[ReleaseInfo.MaxJsonBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), linked.Token);
            if (n == 0) break;
            total += n;
        }
        return (status, buffer.AsSpan(0, total).ToArray());
    }

    public void Start()
    {
        if (_service is null) { Log.Write("update: version unknown, checks disabled"); return; }
        _service.RestorePersisted();
        _settings.Changed += OnSettings;
        Apply(first: true);
    }

    void OnSettings(AppSettings _) => Apply(first: false);

    void Apply(bool first)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (!_settings.Current.CheckForUpdates) { _timer?.Dispose(); _timer = null; return; }
            if (_timer is not null) return;
            // первая проверка — после льготных 2 мин (DEBUG: SIDEBAR_UPDATE_NOW=1 — через секунду); включённая позже — почти сразу, политика всё равно решает
            var due = first ? UpdatePolicy.StartupGrace + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
#if DEBUG
            if (Environment.GetEnvironmentVariable("SIDEBAR_UPDATE_NOW") == "1") due = TimeSpan.FromSeconds(1);
#endif
            _timer = new Timer(_ => _ = OnTick(), null, due, Tick);
        }
    }

    async Task OnTick()
    {
        try
        {
            if (_disposed || _service is null) return;
#if DEBUG
            if (Environment.GetEnvironmentVariable("SIDEBAR_UPDATE_NOW") == "1" && _settings.Current.CheckForUpdates) { await _service.CheckAsync(_cts.Token); return; }
#endif
            await _service.TickAsync(_cts.Token);
        }
        catch (Exception e) { Log.Write("update: failed (" + e.GetType().Name + ")"); }
    }

    /// <summary>Проверка вне политики 24 ч (кнопка «Проверить сейчас»; доступна и при выключенной настройке). Не бросает.</summary>
    public Task<UpdateOutcome> CheckNowAsync() =>
        _disposed || _service is null ? Task.FromResult(new UpdateOutcome(UpdateKind.Failed, Detail: "disabled")) : _service.CheckAsync(_cts.Token);

    /// <summary>Открывает страницу релиза; адрес уже проверен <see cref="ReleaseInfo"/> (https://github.com/Denmurzik/SideBar/releases/…).</summary>
    public static void OpenRelease(ReleaseInfo release) => ShellExecute(0, "open", release.HtmlUrl, null, null, 1);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _settings.Changed -= OnSettings;
            _timer?.Dispose();
            _timer = null;
        }
        try { _cts.Cancel(); } catch (Exception) { }
        _http.Dispose();
    }
}
