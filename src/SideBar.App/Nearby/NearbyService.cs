using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using SideBar.Core.Nearby;
using SideBar.Core.Settings;
using SideBar.Nearby;
using SideBar.Nearby.Discovery;
using SideBar.Nearby.Identity;
using SideBar.Nearby.Session;
using SideBar.Nearby.Transport;
using SideBar.Nearby.Wire;
using SideBar.Sharing;

namespace SideBar.Nearby;

/// <summary>
/// Отправка с плитки Quick Share: поиск устройств (mDNS), TCP-соединение, <see cref="SendSession"/>.
/// Один общий сеанс за раз; события приходят на UI-поток и только от актуального сеанса (устаревшие отбрасываются).
/// Своим устройствам (сертификат найден по рекламе) отправляет с <see cref="CertificateIdentity"/>, остальным анонимно, с PIN, как в M3.
/// </summary>
sealed class NearbyService : IDisposable
{
    /// <summary>Приёмник ограничивает Introduction ста файлами.</summary>
    public const int MaxFiles = 100;

    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Устройство, пропавшее из mDNS, исчезает из плитки не сразу: повторное объявление (Lost/Found) не должно мигать.</summary>
    static readonly TimeSpan LostGrace = TimeSpan.FromSeconds(2);

    readonly DispatcherQueue _ui;
    readonly IdentityProvider _identity;
    readonly IStackPause _pause;
    readonly SettingsStore _settings;
    readonly object _gate = new();
    readonly Dictionary<string, DiscoveredEndpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    QuickShareBrowser? _browser;
    CancellationTokenSource? _cts;
    readonly Dictionary<string, Timer> _lostGrace = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Свои устройства, увиденные по BLE (Bluetooth-плитки "ble:" + Key), не по mDNS.</summary>
    readonly Dictionary<string, CertificateIdentity.BluetoothTarget> _bleTargets = new(StringComparer.Ordinal);
    BleOwnDeviceWatcher? _ble;
    Task<(List<string> Files, bool Truncated)> _files = Task.FromResult((new List<string>(), false));
    CancellationTokenSource? _expandCts;
    int _epoch;

    public event Action<DeviceView>? DeviceFound;
    public event Action<string>? DeviceLost;
    public event Action<double>? Progress;
    public event Action<bool, string?>? Finished;

    public NearbyService(DispatcherQueue ui, IdentityProvider identity, IStackPause pause, SettingsStore settings)
    {
        _settings = settings;
        _ui = ui;
        _pause = pause;
        _identity = identity;
        _identity.Changed += OnIdentityChanged;
        // белый список: тип кадра, размеры, enum-поля; имена файлов и устройств сюда не попадают
        NearbyLog.Log = e => Log.Write($"nearby {e.Kind}{(e.Size is { } n ? " " + n : "")}{(e.Value is { } v ? " " + v : "")}");
    }

    /// <summary>
    /// Каталоги раскрываются в файлы; перечисление останавливается на (<see cref="MaxFiles"/> + 1)-м файле (он доказывает усечение),
    /// огромное дерево в память не собирается. Недоступные пути пропускаются. Вызывать вне UI-потока.
    /// </summary>
    public static (List<string> Files, bool Truncated) ExpandFiles(IEnumerable<string> paths, CancellationToken ct)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) files.Add(path);
                else if (Directory.Exists(path))
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        files.Add(f);
                        if (files.Count > MaxFiles) break;
                    }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log.Write("share path skipped: " + Log.Describe(e)); }
            ct.ThrowIfCancellationRequested();
            if (files.Count > MaxFiles) break;
        }
        bool truncated = files.Count > MaxFiles;
        if (truncated) files.RemoveRange(MaxFiles, files.Count - MaxFiles);
        return (files, truncated);
    }

    /// <summary>Идёт поиск устройств или отправка.</summary>
    public bool Busy
    {
        get { lock (_gate) return _browser is not null || _cts is not null; }
    }

    /// <summary>Начинает поиск устройств; предыдущий сеанс отменяется. UI-поток.</summary>
    public void StartShare(IReadOnlyList<string> paths, Task<IReadOnlyList<string>>? staged = null)
    {
        Cancel();
        int epoch;
        Task<(List<string> Files, bool Truncated)> files;
        if (_pause.IsPaused)
        {
            // официальный Quick Share держит порт и рекламу: свой стек молчит
            Log.Write("share: refused, stack paused");
            lock (_gate) epoch = ++_epoch;
            Post(epoch, () => Finished?.Invoke(false, Strings.CloseOfficialApp));
            return;
        }
        lock (_gate)
        {
            epoch = ++_epoch;
            _endpoints.Clear();
            _bleTargets.Clear();
            _expandCts = new CancellationTokenSource();
            var token = _expandCts.Token;
            // перечисление каталогов может быть долгим: не на UI-потоке, отменяется вместе с сеансом
            files = _files = Task.Run(async () =>
            {
                // временные файлы копируются в фоне (дескрипторы открыты в момент drop); поиск устройств тем временем идёт
                var resolved = staged is null ? paths : await staged.WaitAsync(token).ConfigureAwait(false);
                return ExpandFiles(resolved, token);
            }, token);
        }
        _ = files.ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (t.IsFaulted || t.Result.Files.Count == 0)
            {
                Log.Write("share: nothing to send");
                Post(epoch, () => Finished?.Invoke(false, Strings.NothingToSend));
            }
            else Log.Write($"share: {t.Result.Files.Count} file(s)");
        }, TaskScheduler.Default);
        Log.Write("share: searching");
        try
        {
            var browser = new QuickShareBrowser();
            browser.Found += e => OnFound(epoch, e);
            browser.Lost += name => OnLost(epoch, name);
            browser.Start();
            bool stale;
            lock (_gate) { stale = epoch != _epoch; if (!stale) _browser = browser; }
            if (stale) browser.Dispose();
        }
        catch (Exception e)
        {
            Log.Write("share: discovery failed: " + Log.Describe(e));
            Post(epoch, () => Finished?.Invoke(false, Strings.SendFailed));
        }
        // своё устройство на заблокированном экране гасит mDNS, но продолжает рекламировать себя по BLE: без
        // сертификатов (_identity.Current == null) слушать всё равно нечем, искать некого
        if (_identity.Current is not null)
        {
            try
            {
                var ble = new BleOwnDeviceWatcher(() => _identity.Current);
                ble.Found += presence => OnBlePresenceFound(epoch, presence);
                ble.Lost += key => OnBleLost(epoch, key);
                ble.Start();
                bool stale;
                lock (_gate) { stale = epoch != _epoch; if (!stale) _ble = ble; }
                if (stale) ble.Dispose();
            }
            catch (Exception e)
            {
                // BLE — дополнение к mDNS, а не замена: его отказ не должен мешать обычному поиску
                Log.Write("share: ble watch failed: " + Log.Describe(e));
            }
        }
    }

    // рабочий поток DNS-SD
    void OnFound(int epoch, DiscoveredEndpoint endpoint)
    {
        if (!IncludeLocal && QuickShareAdvertiser.IsLocalAddress(endpoint.Address)) return;   // свои же объявления
        if (!PassesDebugNameFilter(endpoint.DisplayName)) return;
        var view = ViewOf(endpoint);   // не трогает _gate-состояние: крипто-проверка идёт до критической секции
        lock (_gate)
        {
            if (epoch != _epoch) return;
            _endpoints[endpoint.InstanceName] = endpoint;
            if (_lostGrace.Remove(endpoint.InstanceName, out var pending)) pending.Dispose();
            // mDNS увидел то же своё устройство, что уже показано по BLE: mDNS в приоритете, BLE-тайл убирается.
            // Post здесь же, под тем же _gate, что и мутация _bleTargets/_endpoints: иначе порядок доставки на
            // UI-поток мог разойтись с порядком захвата лока между потоками mDNS и BLE (два независимых
            // источника могут захватить лок в одном порядке, но позвать Post — в другом).
            if (KeyOf(endpoint) is { } bleKey && _bleTargets.Remove(bleKey))
                Post(epoch, () => DeviceLost?.Invoke("ble:" + bleKey));
            Post(epoch, () => DeviceFound?.Invoke(view));
        }
        Log.Write($"share: device found, kind {endpoint.Kind}");
    }

    // устройство из mDNS как "своё" (для сравнения с BLE-presence по общему Key, не по InstanceName)
    string? KeyOf(DiscoveredEndpoint endpoint) => OwnIdentity(endpoint)?.TryGetPeerKey(endpoint.Info);

    // рабочий поток BLE-наблюдателя: своё устройство увидено по рекламе Nearby (FEF3), не по mDNS — обычно экран
    // заблокирован. mDNS в приоритете: если устройство уже показано через него, BLE-тайл не создаётся (дедуп).
    void OnBlePresenceFound(int epoch, BlePresence presence)
    {
        var view = new DeviceView("ble:" + presence.Key, DisplayText.SanitizeName(presence.DeviceName, Strings.OwnDevice), ShareDeviceKind.Phone, IsOwn: true);
        lock (_gate)
        {
            if (epoch != _epoch) return;
            if (_endpoints.Values.Any(e => KeyOf(e) == presence.Key)) return;
            var target = _identity.Current?.FindBluetoothTargets().FirstOrDefault(t => t.Key == presence.Key);
            if (target is null) return;
            _bleTargets[presence.Key] = target;
            Post(epoch, () => DeviceFound?.Invoke(view));   // внутри лока по той же причине, что в OnFound
        }
        Log.Write("share: device found over bluetooth");
    }

    void OnBleLost(int epoch, string key)
    {
        lock (_gate)
        {
            if (epoch != _epoch || !_bleTargets.Remove(key)) return;
            Post(epoch, () => DeviceLost?.Invoke("ble:" + key));
        }
    }

    // своё устройство рекламирует себя без имени: имя берётся из его сертификата; чужое называется по mDNS (имя задаёт пир)
    DeviceView ViewOf(DiscoveredEndpoint endpoint)
    {
        var own = OwnIdentity(endpoint);
        string name = own is null
            ? DisplayText.SanitizeName(endpoint.DisplayName, Strings.UnknownDevice)
            : DisplayText.SanitizeName(own.TryGetPeerName(endpoint.Info), Strings.OwnDevice);
        return new DeviceView(endpoint.InstanceName, name, Map(endpoint.Kind), own is not null);
    }

    // сертификаты перечитаны: метка «своё» и имена уже показанных устройств пересчитываются (поток пула)
    void OnIdentityChanged()
    {
        int epoch;
        List<DiscoveredEndpoint> shown;
        lock (_gate) { epoch = _epoch; shown = [.. _endpoints.Values]; }
        foreach (var endpoint in shown)
        {
            var view = ViewOf(endpoint);
            Post(epoch, () => DeviceFound?.Invoke(view));
        }
    }

    /// <summary>Текущая идентичность, если сертификат этого устройства среди наших; иначе null. Сбой проверки = не своё.</summary>
    CertificateIdentity? OwnIdentity(DiscoveredEndpoint endpoint)
    {
        var identity = _identity.Current;
        try { return identity is not null && identity.Identify(endpoint.Info) ? identity : null; }
        catch (Exception e)
        {
            Log.Write("share: identify failed: " + Log.Describe(e));
            return null;
        }
    }

    void OnLost(int epoch, string name)
    {
        lock (_gate)
        {
            if (epoch != _epoch || !_endpoints.ContainsKey(name) || _lostGrace.ContainsKey(name)) return;
            _lostGrace[name] = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (epoch != _epoch || !_lostGrace.Remove(name, out var t)) return;
                    t.Dispose();
                    _endpoints.Remove(name);
                }
                Post(epoch, () => DeviceLost?.Invoke(name));
            }, null, LostGrace, Timeout.InfiniteTimeSpan);
        }
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

    /// <summary>Выбрано устройство: поиск останавливается, начинается отправка. Повторный вызов в том же сеансе игнорируется. UI-поток.</summary>
    public void Choose(string id)
    {
        if (id.StartsWith("ble:", StringComparison.Ordinal)) { ChooseBluetooth(id["ble:".Length..]); return; }
        DiscoveredEndpoint? target;
        int epoch;
        CancellationToken ct;
        Task<(List<string> Files, bool Truncated)> files;
        QuickShareBrowser? browser;
        lock (_gate)
        {
            if (_cts is not null) return;   // отправка уже идёт
            epoch = _epoch;
            if (_pause.IsPaused)
            {
                // официальное приложение запустили после начала поиска: отправка не начинается
                Log.Write("share: choose refused, stack paused");
                Post(epoch, () => Finished?.Invoke(false, Strings.CloseOfficialApp));
                return;
            }
            if (!_endpoints.TryGetValue(id, out target))
            {
                // устройство пропало между кликом и выбором: плитка не должна остаться в «отправке»
                Log.Write("share: chosen device is gone");
                Post(epoch, () => Finished?.Invoke(false, Strings.SendFailed));
                return;
            }
            files = _files;
            browser = _browser;
            _browser = null;
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
        }
        try { browser?.Dispose(); } catch (Exception e) { Log.Write("share: browser dispose failed: " + Log.Describe(e)); }
        _ = Task.Run(() => SendAsync(epoch, target, files, ct));
    }

    /// <summary>Выбрано своё устройство по BLE-плитке ("ble:" + Key): поиск останавливается, начинается отправка по RFCOMM.</summary>
    void ChooseBluetooth(string key)
    {
        CertificateIdentity.BluetoothTarget? target;
        int epoch;
        CancellationToken ct;
        Task<(List<string> Files, bool Truncated)> files;
        QuickShareBrowser? browser;
        BleOwnDeviceWatcher? ble;
        lock (_gate)
        {
            if (_cts is not null) return;   // отправка уже идёт
            epoch = _epoch;
            if (_pause.IsPaused)
            {
                Log.Write("share: choose refused, stack paused");
                Post(epoch, () => Finished?.Invoke(false, Strings.CloseOfficialApp));
                return;
            }
            if (!_bleTargets.TryGetValue(key, out target))
            {
                Log.Write("share: chosen device is gone");
                Post(epoch, () => Finished?.Invoke(false, Strings.SendFailed));
                return;
            }
            files = _files;
            browser = _browser;
            ble = _ble;
            _browser = null;
            _ble = null;
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
        }
        try { browser?.Dispose(); } catch (Exception e) { Log.Write("share: browser dispose failed: " + Log.Describe(e)); }
        try { ble?.Dispose(); } catch (Exception e) { Log.Write("share: ble dispose failed: " + Log.Describe(e)); }
        _ = Task.Run(() => SendBluetoothAsync(epoch, target, files, ct));
    }

    async Task SendAsync(int epoch, DiscoveredEndpoint target, Task<(List<string> Files, bool Truncated)> filesTask, CancellationToken ct)
    {
        try
        {
            var (files, truncated) = await filesTask.WaitAsync(ct).ConfigureAwait(false);
            if (files.Count == 0) return;   // «нечего отправлять» уже показано
            using var client = new TcpClient { NoDelay = true };
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(target.Address, target.Port, connectCts.Token).ConfigureAwait(false);
            }
            IShareIdentity identity = OwnIdentity(target) ?? (IShareIdentity)new AnonymousIdentity();   // идентичность фиксируется на старте отправки
            // метка «своё» построена по рекламе, которую можно переиграть: для своих отправка требует проверенного ключа пира
            var options = new SessionOptions(DeviceNames.Resolve(_settings.Current, Environment.MachineName), DeviceKind.Laptop, identity)   // имя берётся на старте отправки
            {
                RemoteEndpointInfo = target.Info,
                RequireVerifiedPeer = identity is CertificateIdentity,
            };
            await CompleteSendAsync(epoch, client.GetStream(), options, files, truncated, ct).ConfigureAwait(false);
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

    async Task SendBluetoothAsync(int epoch, CertificateIdentity.BluetoothTarget target, Task<(List<string> Files, bool Truncated)> filesTask, CancellationToken ct)
    {
        try
        {
            var (files, truncated) = await filesTask.WaitAsync(ct).ConfigureAwait(false);
            if (files.Count == 0) return;   // «нечего отправлять» уже показано
            Stream stream;
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeout);
                stream = await RfcommClient.ConnectAsync(target.Mac, connectCts.Token).ConfigureAwait(false);
            }
            using (stream)
            {
                // цель из FindBluetoothTargets: своё устройство, верификация обязательна, как для mDNS own
                var options = new SessionOptions(DeviceNames.Resolve(_settings.Current, Environment.MachineName), DeviceKind.Laptop, _identity.Current)
                {
                    RemoteEndpointInfo = target.RemoteInfo,
                    RequireVerifiedPeer = true,
                };
                await CompleteSendAsync(epoch, stream, options, files, truncated, ct).ConfigureAwait(false);
            }
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

    // общая часть TCP- и Bluetooth-пути: сам обмен файлами и маппинг результата на строки
    async Task CompleteSendAsync(int epoch, Stream stream, SessionOptions options, List<string> files, bool truncated, CancellationToken ct)
    {
        var observer = new ProgressObserver(f => Post(epoch, () => Progress?.Invoke(f)));
        var result = await SendSession.SendFilesAsync(stream, options, files, observer, ct).ConfigureAwait(false);
        Log.Write($"share: result {result.Status}");
        var reason = result.Status switch
        {
            TransferStatus.Completed => truncated ? Strings.OnlyFirstFiles : null,
            TransferStatus.Failed when result.Detail == TransferDetails.Unverified => Strings.SendUnverified,
            TransferStatus.TimedOut => Strings.SendTimedOut,
            TransferStatus.RejectedByPeer => Strings.SendRejected,
            TransferStatus.CancelledByPeer => Strings.SendCancelled,
            _ => Strings.SendFailed,
        };
        Post(epoch, () => Finished?.Invoke(result.Status == TransferStatus.Completed, reason));
    }

    /// <summary>Отмена поиска или передачи (SendSession сам шлёт Cancel получателю). Идемпотентна.</summary>
    public void Cancel()
    {
        QuickShareBrowser? browser;
        BleOwnDeviceWatcher? ble;
        CancellationTokenSource? cts, expand;
        lock (_gate)
        {
            _epoch++;
            expand = _expandCts;
            _expandCts = null;
            browser = _browser;
            ble = _ble;
            cts = _cts;
            _browser = null;
            _ble = null;
            _cts = null;
            _endpoints.Clear();
            _bleTargets.Clear();
            foreach (var t in _lostGrace.Values) t.Dispose();
            _lostGrace.Clear();
        }
        try { cts?.Cancel(); } catch (Exception e) { Log.Write("share: cancel failed: " + Log.Describe(e)); }
        try { expand?.Cancel(); } catch (Exception e) { Log.Write("share: cancel failed: " + Log.Describe(e)); }
        try { browser?.Dispose(); } catch (Exception e) { Log.Write("share: browser dispose failed: " + Log.Describe(e)); }
        try { ble?.Dispose(); } catch (Exception e) { Log.Write("share: ble dispose failed: " + Log.Describe(e)); }
    }

    public void Dispose()
    {
        _identity.Changed -= OnIdentityChanged;
        Cancel();
    }

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
