using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using SideBar.Core.Nearby;
using SideBar.Nearby;
using SideBar.Nearby.Discovery;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;

namespace SideBar.Nearby;

/// <summary>
/// Отправка с плитки Quick Share: поиск устройств (mDNS), TCP-соединение, <see cref="SendSession"/>.
/// Один общий сеанс за раз; события приходят на UI-поток и только от актуального сеанса (устаревшие отбрасываются).
/// Отправляет анонимно (режим «Все»); сертификаты придут в M4.
/// </summary>
sealed class NearbyService : IDisposable
{
    /// <summary>Приёмник ограничивает Introduction ста файлами.</summary>
    public const int MaxFiles = 100;

    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    readonly DispatcherQueue _ui;
    readonly object _gate = new();
    readonly Dictionary<string, DiscoveredEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    QuickShareBrowser? _browser;
    CancellationTokenSource? _cts;
    List<string> _files = [];
    int _epoch;

    public event Action<DeviceView>? DeviceFound;
    public event Action<string>? DeviceLost;
    public event Action<double>? Progress;
    public event Action<bool, string?>? Finished;

    public NearbyService(DispatcherQueue ui)
    {
        _ui = ui;
        // белый список: тип кадра, размеры, enum-поля; имена файлов и устройств сюда не попадают
        NearbyLog.Log = e => Log.Write($"nearby {e.Kind}{(e.Size is { } n ? " " + n : "")}{(e.Value is { } v ? " " + v : "")}");
    }

    /// <summary>Каталоги раскрываются в файлы (до <see cref="MaxFiles"/>); недоступные пути пропускаются.</summary>
    public static List<string> ExpandFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (files.Count >= MaxFiles) break;
            try
            {
                if (File.Exists(path)) files.Add(path);
                else if (Directory.Exists(path))
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        if (files.Count >= MaxFiles) break;
                        files.Add(f);
                    }
            }
            catch (Exception e) { Log.Write("share path skipped: " + Log.Describe(e)); }
        }
        return files;
    }

    /// <summary>Начинает поиск устройств; предыдущий сеанс отменяется. UI-поток.</summary>
    public void StartShare(IReadOnlyList<string> paths)
    {
        Cancel();
        int epoch;
        lock (_gate)
        {
            epoch = ++_epoch;
            _files = ExpandFiles(paths);
            _endpoints.Clear();
        }
        if (_files.Count == 0)
        {
            Log.Write("share: nothing to send");
            Post(epoch, () => Finished?.Invoke(false, Strings.NothingToSend));
            return;
        }
        Log.Write($"share: searching, {_files.Count} file(s)");
        try
        {
            var browser = new QuickShareBrowser();
            browser.Found += e => OnFound(epoch, e);
            browser.Lost += name => OnLost(epoch, name);
            browser.Start();
            lock (_gate) _browser = browser;
        }
        catch (Exception e)
        {
            Log.Write("share: discovery failed: " + Log.Describe(e));
            Post(epoch, () => Finished?.Invoke(false, Strings.SendFailed));
        }
    }

    // рабочий поток DNS-SD
    void OnFound(int epoch, DiscoveredEndpoint endpoint)
    {
        if (!IncludeLocal && QuickShareAdvertiser.IsLocalAddress(endpoint.Address)) return;   // свои же объявления
        if (!PassesDebugNameFilter(endpoint.DisplayName)) return;
        lock (_gate)
        {
            if (epoch != _epoch) return;
            _endpoints[endpoint.InstanceName] = endpoint;
        }
        Log.Write($"share: device found, kind {endpoint.Kind}");
        var view = new DeviceView(endpoint.InstanceName, endpoint.DisplayName, Map(endpoint.Kind));
        Post(epoch, () => DeviceFound?.Invoke(view));
    }

    void OnLost(int epoch, string name)
    {
        lock (_gate)
        {
            if (epoch != _epoch || !_endpoints.Remove(name)) return;
        }
        Post(epoch, () => DeviceLost?.Invoke(name));
    }

    static bool IncludeLocal =>
#if DEBUG
        Environment.GetEnvironmentVariable("SIDEBAR_NEARBY_INCLUDE_LOCAL") == "1";
#else
        false;
#endif

    // автотест: только Debug, чтобы клик по первой иконке не ушёл на настоящий телефон в сети
    static bool PassesDebugNameFilter(string name)
    {
#if DEBUG
        return Environment.GetEnvironmentVariable("SIDEBAR_NEARBY_ONLY_NAME") is not { Length: > 0 } only || name == only;
#else
        return true;
#endif
    }

    static ShareDeviceKind Map(DeviceKind kind) => kind switch
    {
        DeviceKind.Phone or DeviceKind.Foldable => ShareDeviceKind.Phone,
        DeviceKind.Tablet => ShareDeviceKind.Tablet,
        DeviceKind.Laptop => ShareDeviceKind.Laptop,
        _ => ShareDeviceKind.Other,
    };

    /// <summary>Выбрано устройство: поиск останавливается, начинается отправка. UI-поток.</summary>
    public void Choose(string id)
    {
        DiscoveredEndpoint? target;
        int epoch;
        CancellationToken ct;
        List<string> files;
        QuickShareBrowser? browser;
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(id, out target)) return;
            epoch = _epoch;
            files = _files;
            browser = _browser;
            _browser = null;
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
        }
        try { browser?.Dispose(); } catch (Exception e) { Log.Write("share: browser dispose failed: " + Log.Describe(e)); }
        _ = Task.Run(() => SendAsync(epoch, target, files, ct));
    }

    async Task SendAsync(int epoch, DiscoveredEndpoint target, List<string> files, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(target.Address, target.Port, connectCts.Token).ConfigureAwait(false);
            }
            var options = new SessionOptions(Environment.MachineName, DeviceKind.Laptop, new AnonymousIdentity()) { RemoteEndpointInfo = target.Info };
            var observer = new ProgressObserver(f => Post(epoch, () => Progress?.Invoke(f)));
            var result = await SendSession.SendFilesAsync(client.GetStream(), options, files, observer, ct).ConfigureAwait(false);
            Log.Write($"share: result {result.Status}");
            var reason = result.Status switch
            {
                TransferStatus.Completed => null,
                TransferStatus.RejectedByPeer => Strings.SendRejected,
                TransferStatus.CancelledByPeer => Strings.SendCancelled,
                _ => Strings.SendFailed,
            };
            Post(epoch, () => Finished?.Invoke(result.Status == TransferStatus.Completed, reason));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Write("share: cancelled");   // пользователь отменил: плитка уже в покое
        }
        catch (Exception e)
        {
            Log.Write("share: send failed: " + Log.Describe(e));
            Post(epoch, () => Finished?.Invoke(false, Strings.SendFailed));
        }
    }

    /// <summary>Отмена поиска или передачи (SendSession сам шлёт Cancel получателю). Идемпотентна.</summary>
    public void Cancel()
    {
        QuickShareBrowser? browser;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _epoch++;
            browser = _browser;
            cts = _cts;
            _browser = null;
            _cts = null;
            _endpoints.Clear();
        }
        try { cts?.Cancel(); } catch (Exception e) { Log.Write("share: cancel failed: " + Log.Describe(e)); }
        try { browser?.Dispose(); } catch (Exception e) { Log.Write("share: browser dispose failed: " + Log.Describe(e)); }
    }

    public void Dispose() => Cancel();

    // событие уходит на UI-поток и там ещё раз сверяется с актуальным сеансом
    void Post(int epoch, Action action) => _ui.TryEnqueue(() =>
    {
        lock (_gate) { if (epoch != _epoch) return; }
        action();
    });

    sealed class ProgressObserver(Action<double> report) : ITransferObserver
    {
        double _last = -1;

        public void Progress(TransferProgress p)
        {
            double f = p.TotalSize > 0 ? (double)p.TotalBytes / p.TotalSize : 0;
            if (f - _last < 0.01 && f < 1) return;   // не заваливаем UI-поток кадрами на каждые 512 КиБ
            _last = f;
            report(f);
        }
    }
}
