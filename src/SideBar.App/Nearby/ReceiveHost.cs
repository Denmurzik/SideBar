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
    const int TickSeconds = 5, RecheckEveryTicks = 6;   // 5 с — истечение видимости, 30 с — сеть и повторная реклама

    readonly VisibilityModel _visibility = new();
    readonly object _gate = new();   // _visibility
    readonly DispatcherQueue _ui;
    readonly Notifications _notifications;
    readonly Action _peek;
    readonly nint _clipboardOwner;
    readonly string _deviceName = Environment.MachineName;
    readonly SemaphoreSlim _evaluate = new(1, 1);
    readonly Timer _timer;
    readonly CancellationTokenSource _lifetime = new();
    int _ticks;
    int _sessions;
    bool _disposed;

    // состояние слушателя — только под _evaluate
    TcpListener? _listener;
    QuickShareAdvertiser? _advertiser;
    (IPAddress Address, int Index)? _bound;
    CancellationTokenSource? _sessionsCts;

    public ReceiveHost(DispatcherQueue ui, Notifications notifications, nint clipboardOwner, Action peek)
    {
        _ui = ui;
        _notifications = notifications;
        _clipboardOwner = clipboardOwner;
        _peek = peek;
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
            else _visibility.EnableEveryone(DateTimeOffset.UtcNow);
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
        if (expired || ++_ticks % RecheckEveryTicks == 0) _ = Task.Run(() => EvaluateAsync(reannounce: !expired));
    }

    async Task EvaluateAsync(bool reannounce = false)
    {
        if (_disposed) return;
        try { await _evaluate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            bool everyone;
            lock (_gate) everyone = _visibility.Current == Visibility.Everyone;

            (IPAddress Address, int Index)? lan = null;
            var allowed = false;
            if (everyone)
            {
                var route = Route();
                if (route is { } r)
                {
                    lan = (r.Address, r.InterfaceIndex);
                    var category = NetworkInfo.CategoryOf(r.InterfaceIndex);
                    lock (_gate) allowed = _visibility.ShouldListen(category);
                    Log.Write($"receive network {category}");
                }
                else Log.Write("receive network none");
            }

            if (!allowed || lan is null)
            {
                // «Скрыто» не рвёт уже идущие передачи; сеть Public/Unknown или пропавший маршрут — рвёт
                Stop(cancelSessions: everyone);
                return;
            }
            if (_listener is not null && _bound is { } b && b.Address.Equals(lan.Value.Address) && b.Index == lan.Value.Index)
            {
                if (reannounce) await ReannounceAsync();
                return;
            }
            Stop(cancelSessions: true);
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

    async Task ReannounceAsync()
    {
        // Android может пропустить сервис, зарегистрированный до начала его поиска
        try { if (_advertiser is { } a) await a.ReannounceAsync(_lifetime.Token); }
        catch (Exception e) { Log.Write("reannounce failed: " + Log.Describe(e)); }
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
        _sessionsCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Log.Write("receive listening");
        _ = AcceptLoopAsync(listener, _sessionsCts.Token);
    }

    void Stop(bool cancelSessions)
    {
        if (_listener is null && _advertiser is null) return;
        var listener = _listener;
        var advertiser = _advertiser;
        var sessions = _sessionsCts;
        _listener = null;
        _advertiser = null;
        _bound = null;
        _sessionsCts = null;
        try { listener?.Stop(); } catch (Exception) { }
        try { advertiser?.Dispose(); } catch (Exception) { }
        if (cancelSessions) { try { sessions?.Cancel(); } catch (Exception) { } }
        Log.Write("receive stopped");
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }   // слушатель остановлен
            if (Interlocked.Increment(ref _sessions) > MaxSessions)
            {
                Interlocked.Decrement(ref _sessions);
                client.Dispose();
                Log.Write("receive connection refused (busy)");
                continue;
            }
            _ = Task.Run(() => RunSessionAsync(client, ct));
        }
    }

    async Task RunSessionAsync(TcpClient client, CancellationToken ct)
    {
        IncomingTransfer? offer = null;
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
                    (o, token) => { offer = o; return ConsentAsync(o, token); }, null, ct);
                Log.Write($"receive session {result.Status} files={result.Files.Count} texts={result.ReceivedTexts.Count}");
                _ui.TryEnqueue(() => ShowResult(result, offer));
            }
        }
        catch (Exception e)
        {
            Log.Write("receive session failed: " + Log.Describe(e));
        }
        finally { Interlocked.Decrement(ref _sessions); }
    }

    Task<bool> ConsentAsync(IncomingTransfer offer, CancellationToken token)
    {
        _ui.TryEnqueue(() => _peek());
#if DEBUG
        if (Environment.GetEnvironmentVariable("SIDEBAR_AUTO_ACCEPT") == "1")
        {
            Log.Write("consent auto-accepted");
            return Task.FromResult(true);
        }
#endif
        string device = string.IsNullOrWhiteSpace(offer.SenderName) ? Strings.UnknownDevice : offer.SenderName.Trim();
        bool onlyLinks = offer.Texts.Count > 0 && offer.Texts.All(t => t.Kind == TextKind.Url);
        string line = Strings.WantsToSend(device, offer.Files.Count, offer.Texts.Count, onlyLinks, offer.Pin);
        return _notifications.AskAccept(line, token);
    }

    // UI-поток
    void ShowResult(TransferResult result, IncomingTransfer? offer)
    {
        string device = string.IsNullOrWhiteSpace(offer?.SenderName) ? Strings.UnknownDevice : offer!.SenderName!.Trim();
        switch (result.Status)
        {
            case TransferStatus.Completed:
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
            case TransferStatus.TimedOut when offer is not null:
                _notifications.Info("timeout", Strings.TimedOut);
                break;
            case TransferStatus.ProtocolError or TransferStatus.Failed when offer is not null:
                _notifications.Info("failed", Strings.ReceiveFailed);
                break;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _timer.Dispose();
        try { _lifetime.Cancel(); } catch (Exception) { }
        try { Stop(cancelSessions: true); } catch (Exception) { }
    }
}
