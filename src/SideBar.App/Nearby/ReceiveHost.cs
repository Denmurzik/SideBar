using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using SideBar.Core.Nearby;
using SideBar.Nearby;
using SideBar.Nearby.Discovery;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;

namespace SideBar.Sharing;

/// <summary>
/// Приём Quick Share: слушатель и mDNS-реклама работают, только пока видимость «Все» и сеть Private/Domain
/// (интерфейс с маршрутом по умолчанию). Перепроверка при смене сети и раз в 30 с. До 3 параллельных сессий.
/// </summary>
sealed class ReceiveHost : IDisposable
{
    const int MaxSessions = 3;
    const int TickSeconds = 5, RecheckEveryTicks = 6;   // 5 с — истечение видимости, 30 с — проверка сети и слушателя

    readonly VisibilityModel _visibility = new();
    readonly object _gate = new();   // _visibility
    readonly DispatcherQueue _ui;
    readonly Notifications _notifications;
    readonly Action _bounce;
    readonly nint _clipboardOwner;
    readonly string _deviceName = Environment.MachineName;
    readonly SemaphoreSlim _evaluate = new(1, 1);
    readonly Timer _timer;
    readonly CancellationTokenSource _lifetime = new();
    readonly NetworkCategoryTracker _network = new();   // под _evaluate
    bool _blockNoted;                                    // под _gate: заметка «приём выключен» уже показана для этого включения
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
    (IPAddress Address, int Index)? _bound;

    public ReceiveHost(DispatcherQueue ui, Notifications notifications, nint clipboardOwner, Action bounce)
    {
        _ui = ui;
        _notifications = notifications;
        _clipboardOwner = clipboardOwner;
        _bounce = bounce;
        NearbyLog.Log = e => Log.Write($"nearby {e.Kind}{(e.Size is { } s ? " " + s : "")}{(e.Value is null ? "" : " " + e.Value)}");
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _timer = new Timer(_ => OnTick(), null, TimeSpan.FromSeconds(TickSeconds), TimeSpan.FromSeconds(TickSeconds));
    }

    public bool IsEveryone
    {
        get { lock (_gate) return _visibility.Current == Visibility.Everyone; }
    }

    /// <summary>Пункт меню трея: включить «Все» на 10 минут или выключить.</summary>
    public void ToggleEveryone()
    {
        lock (_gate)
        {
            if (_visibility.Current == Visibility.Everyone) _visibility.Disable();
            else { _visibility.EnableEveryone(DateTimeOffset.UtcNow); _blockNoted = false; }
            Log.Write($"visibility {_visibility.Current}");
        }
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
        if (expired || ++_ticks % RecheckEveryTicks == 0) _ = Task.Run(() => EvaluateAsync());
    }

    async Task EvaluateAsync()
    {
        if (_disposed) return;
        try { await _evaluate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            Visibility visibility;
            bool anyAccepted;
            lock (_gate) { visibility = _visibility.Current; anyAccepted = _live.Any(l => l.Accepted); }

            // сеть проверяем и в «Скрыто», пока жива принятая передача: Public/Unknown её обрывает
            (IPAddress Address, int Index)? lan = null;
            NetworkCategory? category = null;
            var networkChecked = visibility == Visibility.Everyone || anyAccepted;
            if (networkChecked)
            {
                var route = Route();
                if (route is { } r)
                {
                    lan = (r.Address, r.InterfaceIndex);
                    var lookup = NetworkInfo.LookupCategory(r.InterfaceIndex);
                    category = _network.Observe(lookup);   // один сбой поиска не равен Unknown от NLM
                    Log.Write(lookup is null ? $"receive network lookup failed, using {category}" : $"receive network {category}");
                }
                else
                {
                    _network.Reset();
                    Log.Write("receive network none");
                }
            }
            else _network.Reset();
            var decision = ReceivePolicy.Decide(visibility, category, networkChecked);
            CancelSessions(decision);
            NoteBlocked(ReceiveBlockReason.Of(visibility, lan is not null, category));

            if (!decision.Listen || lan is null)
            {
                Stop();
                return;
            }
            // реклама не перерегистрируется, пока адрес и порт те же: иначе ПК мигает в списке телефона
            if (_listener is not null && _bound is { } b && b.Address.Equals(lan.Value.Address) && b.Index == lan.Value.Index)
                return;
            Stop();
            await StartAsync(lan.Value);
        }
        catch (Exception e)
        {
            Log.Write("receive evaluate failed: " + Log.Describe(e));
        }
        finally { _evaluate.Release(); }
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

    async Task StartAsync((IPAddress Address, int Index) lan)
    {
        TcpListener? listener = null;
        QuickShareAdvertiser? advertiser = null;
        try
        {
            listener = new TcpListener(lan.Address, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var info = new AnonymousIdentity().CreateEndpointInfo(_deviceName, DeviceKind.Laptop);
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
        _bound = lan;
        Log.Write("receive listening");
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
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception e)
            {
                // остановка слушателя — конец цикла; сброс одного входящего соединения (ConnectionReset из backlog) — нет
                if (ct.IsCancellationRequested || AcceptFailure.ListenerStopped(e)) return;
                Log.Write("receive accept failed: " + e.GetType().Name);
                try { await Task.Delay(AcceptFailure.RetryDelay, ct); }
                catch (OperationCanceledException) { return; }
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
                client.NoDelay = true;
                Log.Write("receive session start");
                var options = new SessionOptions(_deviceName);
#if DEBUG
                if (int.TryParse(Environment.GetEnvironmentVariable("SIDEBAR_DECISION_TIMEOUT_S"), out var seconds))
                    options = options with { DecisionTimeout = TimeSpan.FromSeconds(seconds) };   // автотест таймаута решения
#endif
                var result = await ReceiveSession.ReceiveAsync(client.GetStream(), options, ReceivedFileWriter.DownloadsFolder(),
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

    Task<bool> ConsentAsync(IncomingTransfer offer, CancellationToken token)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("SIDEBAR_AUTO_ACCEPT") == "1")
        {
            Log.Write("consent auto-accepted");
            return Task.FromResult(true);
        }
#endif
        string device = DeviceName(offer.SenderName);
        bool onlyLinks = offer.Texts.Count > 0 && offer.Texts.All(t => t.Kind == TextKind.Url);
        string line = Strings.WantsToSend(device, offer.Files.Count, offer.Texts.Count, onlyLinks);
        return _notifications.AskAccept(line, offer.Pin, token);
    }

    // UI-поток
    void ShowResult(TransferResult result, IncomingTransfer? offer, bool accepted)
    {
        string device = DeviceName(offer?.SenderName);
        switch (result.Status)
        {
            case TransferStatus.Completed:
                if (accepted) _bounce();   // принятая передача завершилась: один прыжок карточки
                if (result.Files.Count > 0) _notifications.Received(result.Files);
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
            _notifications.Received(result.Files);
    }

    public void Dispose()
    {
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
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
