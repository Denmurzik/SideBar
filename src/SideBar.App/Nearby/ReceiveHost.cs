using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using SideBar.Core.Nearby;
using SideBar.Core.Settings;
using SideBar.Nearby;
using SideBar.Nearby.Discovery;
using SideBar.Nearby.Identity;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;

namespace SideBar.Sharing;

/// <summary>
/// Приём Quick Share: слушатель и mDNS-реклама работают, только пока видимость «Свои устройства» или «Все» и сеть Private/Domain
/// (интерфейс с маршрутом по умолчанию). В «Своих» реклама без имени с сертификатом, неверифицированные входящие отклоняются
/// без вопроса, верифицированные принимаются сами. Перепроверка при смене сети, смене сертификатов и раз в 30 с. До 3 параллельных сессий.
/// </summary>
sealed class ReceiveHost : IDisposable
{
    const int MaxSessions = 3;
    const int TickSeconds = 5, RecheckEveryTicks = 6;   // 5 с — истечение видимости, 30 с — проверка сети и слушателя

    readonly VisibilityModel _visibility;
    readonly IdentityProvider _identity;
    readonly SettingsStore _settings;
    readonly IStackPause _pause;
    readonly object _gate = new();   // _visibility
    readonly DispatcherQueue _ui;
    readonly Notifications _notifications;
    readonly Action _bounce;
    readonly nint _clipboardOwner;
    string PcName => DeviceNames.Resolve(_settings.Current, Environment.MachineName);   // настройка «Имя этого ПК»; читается при каждой регистрации и сессии
    readonly SemaphoreSlim _evaluate = new(1, 1);
    readonly Timer _timer;
    readonly CancellationTokenSource _lifetime = new();
    readonly NetworkCategoryTracker _network = new();   // под _evaluate
    bool _blockNoted = true;                                    // под _gate: заметка «приём выключен» уже показана для этого включения
    bool _degradeNoted;                                  // под _gate: заметка «сертификаты недоступны» уже показана в этом запуске
    int _ticks;
    int _sessions;
    readonly HashSet<Task> _running = [];   // под _gate

    /// <summary>Живая сессия: её токен отменяется по <see cref="ReceivePolicy"/>; Accepted — пользователь уже согласился.</summary>
    sealed class Live(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public volatile bool Accepted;
    }

    readonly List<Live> _live = [];   // под _gate
    bool _disposed;

    // состояние слушателя — только под _evaluate
    TcpListener? _listener;
    QuickShareAdvertiser? _advertiser;
    // под какое состояние зарегистрирована реклама: смена адреса, режима или идентичности (перезагрузка сертификатов) — новая регистрация со свежей солью
    (IPAddress Address, int Index, Visibility Mode, CertificateIdentity? Identity, string? AdvertisedName)? _bound;

    public ReceiveHost(DispatcherQueue ui, Notifications notifications, nint clipboardOwner, Action bounce, IdentityProvider identity, IStackPause pause, SettingsStore settings)
    {
        _settings = settings;
        _ui = ui;
        _pause = pause;
        _identity = identity;
        _visibility = new VisibilityModel(identity.Current is not null, VisibilitySettings.Load(settings));
        _notifications = notifications;
        _clipboardOwner = clipboardOwner;
        _bounce = bounce;
        NearbyLog.Log = e => Log.Write($"nearby {e.Kind}{(e.Size is { } s ? " " + s : "")}{(e.Value is null ? "" : " " + e.Value)}");
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        // модель уже создана; подписка, затем повторное чтение Current: перезагрузка между чтением и подпиской не теряется
        identity.Changed += OnIdentityChanged;
        pause.Changed += OnPauseChanged;
        _settings.Changed += OnSettingsChanged;
        _lastPcName = settings.Current.PcName;
        lock (_gate) _visibility.SetOwnDevicesAvailable(identity.Current is not null);
        Log.Write($"visibility {_visibility.Current}");
        NoteDegraded();
        _timer = new Timer(_ => OnTick(), null, TimeSpan.FromSeconds(TickSeconds), TimeSpan.FromSeconds(TickSeconds));
        if (_visibility.Current != Visibility.Hidden) _ = Task.Run(() => EvaluateAsync());   // «Свои устройства» по умолчанию: слушать сразу, а не через 30 с
    }

    /// <summary>Есть живая сессия приёма (в том числе ждущая согласия).</summary>
    public bool Busy => Volatile.Read(ref _sessions) > 0;

    public Visibility Mode
    {
        get { lock (_gate) return _visibility.Current; }
    }

    /// <summary>«Свои устройства» можно выбрать: сертификаты официального Quick Share прочитаны.</summary>
    public bool OwnDevicesAvailable
    {
        get { lock (_gate) return _visibility.OwnDevicesAvailable; }
    }

    /// <summary>Пункт меню трея. «Все» действует 10 минут, затем возвращается прежний режим; выбор Hidden/OwnDevices запоминается.</summary>
    public void SetMode(Visibility mode, bool persist = true)
    {
        Visibility persisted;
        lock (_gate)
        {
            if (!_visibility.Select(mode, DateTimeOffset.UtcNow)) return;
            if (mode == Visibility.Everyone) _blockNoted = false;   // заметка о блокировке — только после явного включения «Все»
            persisted = _visibility.Persisted;
            Log.Write($"visibility {_visibility.Current}");
        }
        if (persist) VisibilitySettings.Save(_settings, persisted);
        RaiseModeChanged();
        _ = Task.Run(() => EvaluateAsync());
    }

    /// <summary>
    /// Папка для новой передачи: SIDEBAR_DOWNLOADS_DIR, затем папка из настроек, иначе «Загрузки». Существование проверяется с таймаутом
    /// (сетевой диск не должен подвесить приём). Смена папки в настройках действует только на передачи, начатые после неё.
    /// </summary>
    string ReceiveDirectory()
    {
        var target = ReceiveFolders.Resolve(ReceivedFileWriter.DownloadsOverride(), _settings.Current.ReceiveFolder,
            ReceivedFileWriter.DownloadsFolder, SideBar.Settings.PathProbe.DirectoryWritable);
        if (target.FellBack) Log.Write("receive: chosen folder unavailable, using Downloads");
        return target.Path;
    }

    /// <summary>Режим видимости изменился (выбор, смена сертификатов, истечение «Все»); поднимается не на UI-потоке.</summary>
    public event Action? ModeChanged;

    void RaiseModeChanged()
    {
        try { ModeChanged?.Invoke(); }
        catch (Exception e) { Log.Write("mode changed handler failed: " + Log.Describe(e)); }
    }

    volatile string? _lastPcName;

    // имя ПК изменилось: реклама с именем регистрируется заново (новая соль), идущие сессии не трогаем
    void OnSettingsChanged(AppSettings s)
    {
        if (s.PcName == _lastPcName) return;
        _lastPcName = s.PcName;
        Log.Write("receive: pc name changed");
        _ = Task.Run(() => EvaluateAsync());
    }

    // сертификаты перечитаны (файлы изменились или истёк срок): доступность режима и свежая реклама
    void OnIdentityChanged()
    {
        lock (_gate)
        {
            _visibility.SetOwnDevicesAvailable(_identity.Current is not null);
            Log.Write($"visibility {_visibility.Current}");
        }
        NoteDegraded();
        RaiseModeChanged();
        _ = Task.Run(() => EvaluateAsync());
    }

    // «Свои устройства» нужны, но сертификатов нет: одна заметка за запуск
    void NoteDegraded()
    {
        var failure = _identity.Failure;   // берёт блокировку загрузки: не под _gate, иначе UI ждёт перезагрузку
        lock (_gate)
        {
            // Missing: Quick Share не установлен, говорить нечего
            if (!_visibility.OwnDevicesDegraded || _degradeNoted || failure is null or CertLoadFailure.Missing) return;
            _degradeNoted = true;
        }
        Log.Write("own devices unavailable, working as hidden");
        _ui.TryEnqueue(() => _notifications.Info("certs", Strings.CertsUnavailable));
    }

    // официальное приложение запустили или закрыли (или M5 поставил/снял паузу): остановить стек либо поднять заново со свежей солью
    void OnPauseChanged()
    {
        Log.Write(_pause.IsPaused ? "receive paused" : "receive resumed");
        _ = Task.Run(() => EvaluateAsync());
    }

    void OnNetworkChanged(object? sender, EventArgs e) => _ = Task.Run(() => EvaluateAsync());

    void OnTick()
    {
        bool expired;
        lock (_gate)
        {
            var before = _visibility.Current;
            _visibility.Tick(DateTimeOffset.UtcNow);
            expired = before != _visibility.Current;
            if (expired) Log.Write($"visibility {_visibility.Current}");
        }
        if (expired) RaiseModeChanged();
        if (expired || ++_ticks % RecheckEveryTicks == 0) _ = Task.Run(() => EvaluateAsync());
    }

    async Task EvaluateAsync()
    {
        if (_disposed) return;
        try { await _evaluate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_pause.IsPaused)
            {
                // не принятые сессии отменяются, принятые доигрывают (но сетевая политика их по-прежнему обрывает); слушатель и реклама сняты
                CancelSessions(new ReceiveDecision(Listen: false, CancelUnaccepted: true, CancelAccepted: false));
                bool accepted;
                lock (_gate) accepted = _live.Any(l => l.Accepted);
                if (accepted)
                {
                    var (_, pausedCategory) = ObserveNetwork();
                    var policy = ReceivePolicy.Decide(Visibility.Hidden, pausedCategory, true);
                    CancelSessions(policy with { CancelUnaccepted = false });
                }
                Stop();
                return;
            }
            Visibility visibility;
            bool anyAccepted, useCertificates, named;
            lock (_gate)
            {
                visibility = _visibility.Current;
                useCertificates = _visibility.UsesCertificates;
                named = _visibility.AdvertisesName;
                anyAccepted = _live.Any(l => l.Accepted);
            }
            // сертификатная идентичность в «Своих» и в «Все» (если сертификаты есть); читается один раз на проход
            var identity = useCertificates ? _identity.Current : null;

            // сеть проверяем и в «Скрыто», пока жива принятая передача: Public/Unknown её обрывает
            (IPAddress Address, int Index)? lan = null;
            NetworkCategory? category = null;
            var networkChecked = visibility != Visibility.Hidden || anyAccepted;
            if (networkChecked) (lan, category) = ObserveNetwork();
            else _network.Reset();
            var decision = ReceivePolicy.Decide(visibility, category, networkChecked);
            CancelSessions(decision);
            if (visibility == Visibility.Everyone) NoteBlocked(ReceiveBlockReason.Of(visibility, lan is not null, category));

            if (!decision.Listen || lan is null || (visibility == Visibility.OwnDevices && identity is null))
            {
                Stop();
                return;
            }
            // реклама не перерегистрируется, пока адрес, режим и идентичность те же: иначе ПК мигает в списке телефона
            if (_listener is not null && _bound is { } b && b.Address.Equals(lan.Value.Address) && b.Index == lan.Value.Index
                && b.Mode == visibility && ReferenceEquals(b.Identity, identity) && b.AdvertisedName == AdvertisedNameFor(identity, named))
                return;
            Stop();
            await StartAsync(lan.Value, visibility, identity, named);
        }
        catch (Exception e)
        {
            Log.Write("receive evaluate failed: " + Log.Describe(e));
        }
        finally { _evaluate.Release(); }
    }

    // под _evaluate
    ((IPAddress Address, int Index)? Lan, NetworkCategory? Category) ObserveNetwork()
    {
        var route = Route();
        if (route is not { } r)
        {
            _network.Reset();
            Log.Write("receive network none");
            return (null, null);
        }
        var lookup = NetworkInfo.LookupCategory(r.InterfaceIndex);
        var category = _network.Observe(lookup, DateTimeOffset.UtcNow);   // один сбой поиска не равен Unknown от NLM
        Log.Write(lookup is null ? $"receive network lookup failed, using {category}" : $"receive network {category}");
        return ((r.Address, r.InterfaceIndex), category);
    }

    static (IPAddress Address, int InterfaceIndex)? Route()
    {
#if DEBUG
        // автотест на машине с VPN: маршрут по умолчанию ведёт в туннель (Public), поэтому интерфейс можно указать явно
        if (int.TryParse(Environment.GetEnvironmentVariable("SIDEBAR_RECEIVE_IFINDEX"), out var index))
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                if (!nic.Supports(NetworkInterfaceComponent.IPv4) || props.GetIPv4Properties().Index != index) continue;
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (v4 is not null) return (v4, index);
            }
            return null;
        }
#endif
        return NetworkInfo.DefaultRouteInterface();
    }

    /// <summary>Один раз за включение «Все»: почему ПК не виден, хотя видимость включена.</summary>
    void NoteBlocked(ReceiveBlock block)
    {
        if (block == ReceiveBlock.None) return;
        lock (_gate)
        {
            if (_blockNoted) return;
            _blockNoted = true;
        }
        var text = block == ReceiveBlock.NoLan ? Strings.ReceiveNoLan : Strings.ReceivePublicNetwork;
        Log.Write($"receive blocked {block}");
        _ui.TryEnqueue(() => _notifications.Info("blocked", text));
    }

    // имя попадает в рекламу в «Все» (анонимно) и в «Своих» с named; без имени смена настройки рекламу не трогает
    string? AdvertisedNameFor(CertificateIdentity? identity, bool named) => identity is null || named ? PcName : null;

    async Task StartAsync((IPAddress Address, int Index) lan, Visibility mode, CertificateIdentity? identity, bool named)
    {
        TcpListener? listener = null;
        QuickShareAdvertiser? advertiser = null;
        var deviceName = PcName;
        try
        {
            listener = new TcpListener(lan.Address, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // новая соль при каждой регистрации; «Все» — анонимно с именем, «Свои» — без имени, подписано сертификатом
            var info = identity is null
                ? new AnonymousIdentity().CreateEndpointInfo(deviceName, DeviceKind.Laptop)
                : identity.CreateEndpointInfo(deviceName, DeviceKind.Laptop, named);
            advertiser = new QuickShareAdvertiser(MdnsName.NewEndpointId(), info.Encode(), port, lan.Address, lan.Index);
            await advertiser.RegisterAsync(_lifetime.Token);
        }
        catch (Exception e)
        {
            Log.Write("receive start failed: " + Log.Describe(e));
            try { advertiser?.Dispose(); } catch (Exception) { }
            try { listener?.Stop(); } catch (Exception) { }
            return;
        }
        _listener = listener;
        _advertiser = advertiser;
        _bound = (lan.Address, lan.Index, mode, identity, identity is null || named ? deviceName : null);
        Log.Write($"receive listening {mode}");
        _ = AcceptLoopAsync(listener, _lifetime.Token);
    }

    /// <summary>Цикл приёма умер сам (не по остановке слушателя): сбросить привязку, чтобы ближайшая проверка подняла его заново.</summary>
    async Task ResetIfDeadAsync(TcpListener listener)
    {
        try { await _evaluate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (!ReferenceEquals(_listener, listener)) return;
            Log.Write("receive accept loop ended, restarting");
            Stop();
        }
        finally { _evaluate.Release(); }
        _ = Task.Run(() => EvaluateAsync());
    }

    void Stop()
    {
        if (_listener is null && _advertiser is null) return;
        var listener = _listener;
        var advertiser = _advertiser;
        _listener = null;
        _advertiser = null;
        _bound = null;
        try { listener?.Stop(); } catch (Exception) { }
        try { advertiser?.Dispose(); } catch (Exception) { }
        Log.Write("receive stopped");
    }

    void CancelSessions(ReceiveDecision decision)
    {
        if (!decision.CancelUnaccepted && !decision.CancelAccepted) return;
        List<Live> victims;
        lock (_gate) victims = _live.Where(l => l.Accepted ? decision.CancelAccepted : decision.CancelUnaccepted).ToList();
        foreach (var l in victims) { try { l.Cts.Cancel(); } catch (ObjectDisposedException) { } }
        if (victims.Count > 0) Log.Write($"receive sessions cancelled {victims.Count}");
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try { await AcceptLoopCoreAsync(listener, ct); }
        catch (Exception e) { Log.Write("receive accept loop failed: " + Log.Describe(e)); }
        if (!ct.IsCancellationRequested) await ResetIfDeadAsync(listener);
    }

    async Task AcceptLoopCoreAsync(TcpListener listener, CancellationToken ct)
    {
        int failures = 0;
        long lastLogMs = long.MinValue / 2;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception e)
            {
                // остановка слушателя — конец цикла; сброс одного входящего соединения (ConnectionReset из backlog) — нет
                if (ct.IsCancellationRequested || AcceptFailure.ListenerStopped(e)) return;
                failures++;
                if (Environment.TickCount64 - lastLogMs >= AcceptFailure.LogInterval.TotalMilliseconds)
                {
                    lastLogMs = Environment.TickCount64;
                    Log.Write($"receive accept failed: {e.GetType().Name} (x{failures})");
                }
                try { await Task.Delay(AcceptFailure.Backoff(failures), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            failures = 0;
            if (_pause.IsPaused)
            {
                client.Dispose();   // принято прямо перед паузой
                Log.Write("receive connection dropped (paused)");
                continue;
            }
            if (Interlocked.Increment(ref _sessions) > MaxSessions)
            {
                Interlocked.Decrement(ref _sessions);
                client.Dispose();
                Log.Write("receive connection refused (busy)");
                continue;
            }
            Task? task = null;
            lock (_gate)
            {
                task = Task.Run(() => RunSessionAsync(client, ct));
                _running.Add(task);
            }
            _ = task.ContinueWith(t => { lock (_gate) _running.Remove(t); }, TaskScheduler.Default);
        }
    }

    async Task RunSessionAsync(TcpClient client, CancellationToken ct)
    {
        IncomingTransfer? offer = null;
        var accepted = false;
        var saved = new SavedFiles();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var live = new Live(cts);
        lock (_gate) _live.Add(live);
        try
        {
            using (client)
            {
                if (_pause.IsPaused)
                {
                    Log.Write("receive session dropped (paused)");   // пауза началась между приёмом соединения и его регистрацией
                    return;
                }
                client.NoDelay = true;
                Log.Write("receive session start");
                // режим и идентичность фиксируются на старте сессии: смена сертификатов её не трогает
                bool useCertificates, reject;
                lock (_gate) { useCertificates = _visibility.UsesCertificates; reject = _visibility.RejectsUnverified; }
                var options = new SessionOptions(PcName) { Identity = useCertificates ? _identity.Current : null, RejectUnverified = reject };
#if DEBUG
                if (int.TryParse(Environment.GetEnvironmentVariable("SIDEBAR_DECISION_TIMEOUT_S"), out var seconds))
                    options = options with { DecisionTimeout = TimeSpan.FromSeconds(seconds) };   // автотест таймаута решения
#endif
                var result = await ReceiveSession.ReceiveAsync(client.GetStream(), options, ReceiveDirectory(),
                    async (o, token) => { offer = o; accepted = await ConsentAsync(o, token); live.Accepted = accepted; return accepted; }, saved, cts.Token);
                Log.Write($"receive session {result.Status} files={result.Files.Count} texts={result.ReceivedTexts.Count}");
                _ui.TryEnqueue(() => ShowResult(result, offer, accepted));
            }
        }
        catch (OperationCanceledException) when (live.Accepted && cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // принятую передачу оборвала политика (сеть стала Public/Unknown или пропал маршрут); .part удалён сессией
            Log.Write("receive session cancelled by policy");
            var files = saved.Snapshot();
            _ui.TryEnqueue(() =>
            {
                _notifications.Info("interrupted", Strings.TransferInterrupted);
                if (files.Count > 0) _notifications.Received(files);
            });
        }
        catch (Exception e)
        {
            Log.Write("receive session failed: " + Log.Describe(e));
        }
        finally
        {
            lock (_gate) _live.Remove(live);
            Interlocked.Decrement(ref _sessions);
        }
    }

    /// <summary>Файлы, уже записанные под итоговыми именами: нужны и тогда, когда сессия оборвана исключением.</summary>
    sealed class SavedFiles : ITransferObserver
    {
        readonly List<string> _paths = [];
        public void FileSaved(string path) { lock (_paths) _paths.Add(path); }
        public IReadOnlyList<string> Snapshot() { lock (_paths) return [.. _paths]; }
    }

    static string DeviceName(string? raw) => DisplayText.SanitizeName(raw, Strings.UnknownDevice);

    // верифицированный пир называется по сертификату (без имени — "Ваше устройство"), остальные — по рекламе
    static string PeerName(IncomingTransfer? offer) =>
        DisplayText.PeerName(offer?.Verified == true, offer?.VerifiedName, offer?.SenderName, Strings.OwnDevice, Strings.UnknownDevice);

    Task<bool> ConsentAsync(IncomingTransfer offer, CancellationToken token)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("SIDEBAR_AUTO_ACCEPT") == "1")
        {
            Log.Write("consent auto-accepted");
            return Task.FromResult(true);
        }
#endif
        if (offer.Verified)
        {
            // своё устройство: сертификат пира проверен до Introduction; ни запроса, ни прыжка внимания
            Log.Write("consent auto-accepted (own device)");
            return Task.FromResult(true);
        }
        string device = DeviceName(offer.SenderName);
        bool onlyLinks = offer.Texts.Count > 0 && offer.Texts.All(t => t.Kind == TextKind.Url);
        string line = Strings.WantsToSend(device, offer.Files.Count, offer.Texts.Count, onlyLinks);
        return _notifications.AskAccept(line, offer.Pin, token);
    }

    // UI-поток
    void ShowResult(TransferResult result, IncomingTransfer? offer, bool accepted)
    {
        string device = PeerName(offer);
        string? ownDevice = offer?.Verified == true ? device : null;   // «<имя>: получено N» только для своих
        switch (result.Status)
        {
            case TransferStatus.Completed:
                if (accepted) _bounce();   // принятая передача завершилась: один прыжок карточки
                if (result.Files.Count > 0) _notifications.Received(result.Files, ownDevice);
                var texts = result.ReceivedTexts;
                if (texts.Count > 0) ClipboardText.Set(texts[0].Value, _clipboardOwner);
                foreach (var t in texts.Take(3)) _notifications.SharedText(device, t.Kind, t.Value);
                break;
            case TransferStatus.RejectedByPeer when result.Detail == TransferDetails.WifiUnsupported:
                _notifications.Info("wifi", Strings.WifiUnsupported);
                break;
            case TransferStatus.RejectedByPeer when result.Detail == TransferDetails.NotEnoughSpace:
                _notifications.Info("no-space", Strings.NotEnoughSpace);
                break;
            case TransferStatus.RejectedByPeer:
                break;   // пользователь сам отклонил
            case TransferStatus.CancelledByPeer when offer is not null:
                _notifications.Info("cancelled", Strings.SenderCancelled);
                break;
            case TransferStatus.TimedOut when accepted:
                _notifications.Info("interrupted", Strings.TransferInterrupted);   // пир замолчал посреди передачи
                break;
            case TransferStatus.TimedOut when offer is not null:
                _notifications.Info("timeout", Strings.TimedOut);
                break;
            case TransferStatus.ProtocolError or TransferStatus.Failed when offer is not null:
                _notifications.Info("failed", Strings.ReceiveFailed);
                break;
        }
        // оборванная передача: уже сохранённые файлы остаются в «Загрузках», о них тоже говорим
        if (result.Status is TransferStatus.CancelledByPeer or TransferStatus.TimedOut or TransferStatus.ProtocolError or TransferStatus.Failed
            && result.Files.Count > 0)
            _notifications.Received(result.Files, ownDevice);
    }

    public void Dispose()
    {
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _identity.Changed -= OnIdentityChanged;
        _pause.Changed -= OnPauseChanged;
        _settings.Changed -= OnSettingsChanged;
        _timer.Dispose();
        try { _lifetime.Cancel(); } catch (Exception) { }   // прерывает идущий StartAsync/RegisterAsync и все сессии
        // тот же затвор, что у Evaluate: Stop не гоняется с StartAsync
        var gated = false;
        try { gated = _evaluate.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        try { Stop(); } catch (Exception) { }
        if (gated) _evaluate.Release();
        // сессии успевают удалить .part-файлы до Environment.Exit
        Task[] running;
        lock (_gate) running = [.. _running];
        try { Task.WaitAll(running, TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}
