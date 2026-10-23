using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SideBar.Core;
using SideBar.Core.Edge;
using SideBar.Core.Layout;
using SideBar.Core.Nearby;
using SideBar.Edge;
using SideBar.Core.Settings;
using SideBar.Nearby;
using SideBar.Settings;
using SideBar.Shelf;
using SideBar.Sharing;
using SideBar.Tray;
using WinRT.Interop;

namespace SideBar;

public partial class App : Application
{
    CardWindow? _window;
    DispatcherQueue? _ui;
    MouseHook? _hook;
    EdgeStateMachine? _machine;
    readonly MemoryTrimmer _trimmer = new();

    // _machine, _layout, _mode, _primary, _scale, _generation трогают рабочий поток хука и UI-поток — под _gate
    readonly object _gate = new();
    CardLayout _layout = null!;
    EdgeMode _mode;
    PxRect _primary;
    int _generation;
    double _scale;
    Timer? _startupTrim;
    MessageWindow? _messages;
    TrayIcon? _tray;
    ShelfService? _shelf;
    OleDropTarget.DropRegistration? _dropTarget;
    FanWindow? _fan;
    Notifications? _notifications;
    ReceiveHost? _receive;
    SettingsStore? _settings;
    readonly StackPause _stack = new();
    OfficialAppWatcher? _official;
    IdentityProvider? _identity;
    readonly BumpGate _bump = new();
    readonly AttentionBounce _attention = new();
    readonly ReceiveTileModel _recv = new();
#if DEBUG
    Timer? _debugNote;
    void DebugNote(string kind)
    {
        switch (kind)
        {
            case "file": _notifications?.Received(["C:/x/photo.jpg"], "Pixel 9"); break;
            case "unsafe": _notifications?.Received(["C:/x/setup.exe"], "Pixel 9"); break;
            case "link": _notifications?.SharedText("Pixel 9", SideBar.Nearby.Session.TextKind.Url, "https://example.com/page"); break;
            case "text": _notifications?.SharedText("Pixel 9", SideBar.Nearby.Session.TextKind.Text, "Привет с телефона"); break;
            case "cancel": _notifications?.Info("cancelled", Strings.SenderCancelled); break;
        }
    }
#endif
    bool _debugOpen = false;   // DEBUG: SIDEBAR_DEBUG_OPEN=1 держит карточку открытой для скриншотов
    ShareTileModel? _share;
    NearbyService? _nearby;
    DispatcherTimer? _tileTimer;
    volatile bool _shareActive;   // KeepsCardOpen для рабочего потока хука (клик вне карточки отменяет отправку)
    PxRect? _fanZone;   // под _gate: зона веера для проверки клика вне (рабочий поток хука)

    public App()
    {
        // второй экземпляр (автозапуск + ручной) завершается до создания любого окна
        if (!SingleInstance.TryAcquire()) Environment.Exit(0);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        ShareStaging.SweepStale();   // остатки аварийно завершённого прошлого запуска
        _shelf = new ShelfService();
        _shelf.Load();
        _settings = new SettingsStore(SettingsPath());
        VisibilitySettings.MigrateLegacy(_settings);   // режим видимости M4 из реестра: один раз, до создания ReceiveHost
        _window = new CardWindow();
        _window.BindShelf(_shelf);
        _window.FanRequested += OnFanRequested;
        _window.ShelfCleared += () => _fan?.Hide();
        _identity = new IdentityProvider();   // сертификаты нужны и плитке (метка «своё»), и приёму
        _identity.Start();
        InitShare();
        _window.Settled += s =>
        {
            if (s != PanelState.Hidden) { RefreshTileHold(); return; }   // карточка показана: заметкам снова нужен таймер
            DragOut.Reset();
            CardHidden();
        };

        var (layout, mode, scale, primary) = Measure();
        _layout = layout;
        _mode = mode;
        _scale = scale;
        _primary = primary;
        _window.Initialize(layout, mode, scale);
        _machine = new EdgeStateMachine(layout, mode);
        CardHidden();
        // после первого Show (его ставит в очередь Initialize): к этому времени у окна есть дочерние HWND WinUI
        _ui.TryEnqueue(() =>
        {
            try
            {
                _dropTarget = OleDropTarget.Register(WindowNative.GetWindowHandle(_window), _shelf, () => DragOut.IsActive, OnForeignDrag, IsOverShareTile, () => _share?.State != ShareTileState.Sending, OnShareDrop);
            }
            catch (Exception e)
            {
                Log.Write("drop target registration failed, shelf drop disabled: " + Log.Describe(e));   // только полка отключается, карточка работает
            }
            Log.Write("ready");
#if DEBUG
            // скриншот глубокой стопки без ввода: SIDEBAR_DEBUG_FILL=<папка> кладёт на полку файлы папки (ссылками)
            if (Environment.GetEnvironmentVariable("SIDEBAR_DEBUG_FILL") is { Length: > 0 } fillDir && Directory.Exists(fillDir) && _shelf is not null)
                foreach (var f in Directory.EnumerateFiles(fillDir).Order()) _shelf.Model.Add(f, SideBar.Core.Shelf.ShelfItemKind.Reference);
            if (Environment.GetEnvironmentVariable("SIDEBAR_DEBUG_OPEN") == "1")
            {
                _debugOpen = true;   // автомат мыши отключён, карточка открыта для скриншотов
                _trimmer.OnShown();
                _window?.SetState(PanelState.Open);
                if (Environment.GetEnvironmentVariable("SIDEBAR_DEBUG_CLEAR") == "armed") _window?.DebugArmClear();   // скриншот корзины в вооружённом виде
            }
#endif
        });
        _hook = new MouseHook(OnMouse, OnPrimaryDown);
        string exe = Environment.ProcessPath!;
        var autostart = new Autostart();
        try { autostart.RepairPath(exe); } catch { /* политика/доступ к реестру: запуск не должен падать из-за автозапуска */ }
        _messages = new MessageWindow(OnDisplaysChanged);
        _notifications = new Notifications(_ui, _messages.Handle, _recv);
        _official = new OfficialAppWatcher();
        _official.RunningChanged += running =>
        {
            Log.Write(running ? "official app running" : "official app stopped");
            _stack.SetOfficialRunning(running);
        };
        _official.Poll();   // первое решение приёма уже учитывает процесс
        _receive = new ReceiveHost(_ui, _notifications, _messages.Handle, BounceOnce, _identity, _stack, _settings);
        _official.Start();
#if DEBUG
        if (Environment.GetEnvironmentVariable("SIDEBAR_AUTO_EVERYONE") == "1") _receive.SetMode(SideBar.Core.Nearby.Visibility.Everyone, persist: false);   // автотест без клика по меню трея
        // скриншоты заметок: каждые 6 с показывает заметку file | unsafe | link | text | cancel (с SIDEBAR_DEBUG_OPEN=1)
        if (Environment.GetEnvironmentVariable("SIDEBAR_DEBUG_NOTE") is { Length: > 0 } noteKind)
            _debugNote = new Timer(_ => _ui?.TryEnqueue(() => DebugNote(noteKind)), null, 3000, 6000);
#endif
        _tray = new TrayIcon(_messages, autostart, exe, () => _ui?.TryEnqueue(ExitApp), () => _receive.Mode, () => _receive.OwnDevicesAvailable, m => _receive.SetMode(m), () => _stack.IsPaused) { OpenSettings = () => ShowSettings() };
#if DEBUG
        // скриншоты без ввода: SIDEBAR_DEBUG_SETTINGS=<0..4> открывает окно настроек на нужном разделе
        if (int.TryParse(Environment.GetEnvironmentVariable("SIDEBAR_DEBUG_SETTINGS"), out var debugSection)) _ui.TryEnqueue(() => ShowSettings(debugSection));
#endif

        // к первой обрезке окно ещё достраивается: повторяем через 2 с, если карточка всё ещё скрыта
        _startupTrim = new Timer(_ => _ui?.TryEnqueue(() => { if (_machine?.State == PanelState.Hidden) CardHidden(); }), null, 2000, Timeout.Infinite);
    }

    static string SettingsPath() => System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("SIDEBAR_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBar"),
        "settings.json");

    /// <summary>Окно настроек: одно на процесс, по требованию; пока оно открыто, обрезка working set стоит.</summary>
    void ShowSettings(int? section = null)
    {
        if (_settings is null || _receive is null || _identity is null || _shelf is null) return;
        SettingsWindow.ShowOrActivate(new SettingsDeps(_settings, new Autostart(), Environment.ProcessPath!, _receive, _identity,
            () => { _shelf.Clear(); _fan?.Hide(); }, _trimmer.OnShown, _trimmer.OnHidden), section);
    }

    // пока карточка открыта (в том числе удерживается запросом), обрезка working set не идёт: OnShown снимает таймер
    void CardHidden() => _trimmer.OnHidden();

    void InitShare()
    {
        _share = new ShareTileModel();
        _nearby = new NearbyService(_ui!, _identity!, _stack);
        _window!.BindShare(_share, _recv, (id, accept) => _notifications?.Decide(id, accept));
        _window.OwnItemsDropped += OnShareDrop;
        _share.Changed += OnShareChanged;
        _nearby.DeviceFound += _share.DeviceFound;
        _nearby.DeviceLost += _share.DeviceLost;
        _nearby.Progress += _share.Progress;
        _nearby.Finished += _share.Finished;
        _window.ShareDeviceChosen += id =>
        {
            _share.Choose(id);
            if (_share.State == ShareTileState.Sending) _nearby.Choose(id);
        };
        _recv.Changed += OnTileChanged;
        _tileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tileTimer.Tick += (_, _) => OnTileTick();
    }

    // состояние приёма в плитке изменилось: удержание карточки и таймер
    void OnTileChanged() => RefreshTileHold();

    void OnTileTick()
    {
        var now = DateTimeOffset.UtcNow;
        if (_share is { } share) share.Covered = _recv.HasConsent;   // запрос закрывает плитку: таймаут выбора стоит
        _share?.Tick(now);
        _recv.Tick(now, NoteVisible());
        RefreshTileHold();
        AttentionTick();
    }

    // заметка на экране: карточка Open/Peek, плитка показывает именно её
    bool NoteVisible()
    {
        PanelState state;
        lock (_gate) state = _machine?.State ?? PanelState.Hidden;
        // Slide: Peek — полоска 10 epx, заметку не прочесть; InPlace: Peek показывает карточку целиком
        bool shown = _debugOpen || state == PanelState.Open || (_mode == EdgeMode.InPlace && state != PanelState.Hidden);
        return shown && _recv.Content(_share?.State ?? ShareTileState.Idle) == TileContent.Note;
    }

    // карточка открыта, пока идёт отправка/показ итога или ждёт запрос (плюс 1.5 с после последнего решения)
    void RefreshTileHold()
    {
        var now = DateTimeOffset.UtcNow;
        bool hold = (_share?.HoldsCardOpen ?? false) || _recv.HoldsCardOpen(now);
        lock (_gate) { if (_machine is not null) _machine.HoldOpen = hold; }
        bool needTick = (_share?.State ?? ShareTileState.Idle) != ShareTileState.Idle || _recv.NeedsTick(now, NoteVisible());
        if (needTick)
        {
            if (_tileTimer is { IsEnabled: false }) { _recv.Resume(now); _tileTimer.Start(); }   // паузу таймера заметкам не засчитываем
        }
        else _tileTimer?.Stop();
    }

    // запрос ждёт решения, карточка спрятана: подпрыгивает сразу и затем раз в 10 с (как привлечение внимания в Dock)
    void AttentionTick()
    {
        var machine = _machine;
        if (machine is null || _window is null) return;
        PanelState state;
        PxRect primary;
        lock (_gate) { state = machine.State; primary = _primary; }
        if (!_attention.Due(Environment.TickCount64, _recv.HasConsent, state, () => FullscreenProbe.IsBusy(primary))) return;
        if (_window.StartAttention())
        {
            Log.Write("edge attention cue");
            _trimmer.OnShown();   // Settled(Hidden) в конце прыжка вернёт обрезку
        }
    }

    bool IsOverShareTile(int x, int y) => _window?.QsTileScreenRect() is { } r && r.Contains(x, y);

    /// <summary>Drop файлов (или переносимых элементов стопки) на плитку Quick Share: UI-поток.</summary>
    void OnShareDrop(IReadOnlyList<string> paths)
    {
        if (_share is null || _nearby is null) return;
        if (_stack.IsPaused)
        {
            ShareStaging.TakePending();   // копии временных файлов не нужны
            if (_share.State is ShareTileState.Searching or ShareTileState.Choosing or ShareTileState.Sending)
                Log.Write("share: drop ignored (paused)");   // идущий сеанс не трогаем
            _share.ShowOfficialAppRunning(DateTimeOffset.UtcNow);
            return;
        }
        _share.BeginShare(paths, DateTimeOffset.UtcNow);
        var staged = ShareStaging.TakePending();   // копии временных файлов делаются в фоне, отправка их дожидается
        if (_share.State == ShareTileState.Searching) _nearby.StartShare(paths, staged);
    }

    void OnShareChanged()
    {
        bool keep = _share!.KeepsCardOpen;
        _shareActive = keep;
        RefreshTileHold();
        // поиск и соединение больше не нужны ни при итоге, ни при отмене; Cancel идемпотентна
        if (!keep) { _nearby?.Cancel(); ShareStaging.Cleanup(); }   // копии временных файлов больше не нужны
        Log.Write($"share tile {_share.State}");
    }

    // порядок важен: сначала снять хук, затем убрать иконку
    void ExitApp()
    {
        try
        {
            try { _official?.Dispose(); } catch { }
            try { _nearby?.Dispose(); } catch { }
            try { _hook?.Dispose(); } catch { }
            try { _dropTarget?.Dispose(); } catch { }
            try { _receive?.Dispose(); } catch { }
            try { _identity?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _messages?.Dispose(); } catch { }
        }
        finally { Environment.Exit(0); }
    }

    /// <summary>
    /// Чужой drag над карточкой (UI-поток). Пока кнопка зажата, автомат держит Open сам; из Peek (виден край в 10 epx) —
    /// имитируем касание края, иначе первый же шаг мыши за полосу вернул бы Hidden.
    /// </summary>
    void OnForeignDrag(bool active)
    {
        var machine = _machine;
        if (!active || machine is null) return;
        PanelState? changed;
        lock (_gate)
        {
            if (machine.State != PanelState.Peek) return;
            changed = machine.Update(new EdgeInput(_layout.ActivationBand.Right - 1, _layout.CenterY, true, false, Environment.TickCount64));
        }
        if (changed is { } s)
        {
            Log.Write($"edge {s} (drag)");
            _trimmer.OnShown();
            _window?.SetState(s);
        }
    }

    /// <summary>Один прыжок скрытой карточки (UI-поток), например по завершении принятой передачи; открытую или выглядывающую не трогаем.</summary>
    void BounceOnce()
    {
        var machine = _machine;
        if (machine is null || _window is null) return;
        PxRect primary;
        lock (_gate) { if (machine.State != PanelState.Hidden) return; primary = _primary; }
        if (FullscreenProbe.IsBusy(primary)) return;   // то же подавление, что у края
        if (!_bump.TryBump(Environment.TickCount64)) return;
        if (!_window.StartAttention()) return;   // карточка уже в движении
        Log.Write("edge attention cue (incoming)");
        _trimmer.OnShown();   // Settled(Hidden) в конце прыжка вернёт обрезку working set
    }

    /// <summary>Клик по стопке (UI-поток): показать веер слева от открытой карточки.</summary>
    void OnFanRequested()
    {
        if (_shelf is null || _machine is null) return;
        PxRect card;
        double scale;
        lock (_gate)
        {
            if (_machine.State != PanelState.Open) return;
            if (_fanZone is not null) { _ui?.TryEnqueue(() => _fan?.Hide()); return; }   // повторный клик по стопке закрывает веер
            card = _layout.CardRectAt(_layout.LeftFor(PanelState.Open));
            scale = _scale;
        }
        if (_fan is null)
        {
            _fan = new FanWindow();
            _fan.Bind(_shelf);
            _fan.ZoneChanged += OnFanZone;
        }
        _fan.ShowNextTo(card, scale);
    }

    // пока веер открыт, курсор над ним (и над зазором до карточки) не даёт карточке скрыться
    void OnFanZone(PxRect? zone)
    {
        lock (_gate)
        {
            _fanZone = zone;
            _machine?.SetKeepOpenRect(zone);
        }
        Log.Write(zone is { } z ? $"fan open {z.Left},{z.Top},{z.Right},{z.Bottom}" : "fan closed");
    }

    // рабочий поток хука: нажатие основной кнопки вне карточки и веера закрывает веер (активацию окон для этого не используем)
    void OnPrimaryDown(int x, int y)
    {
        PxRect? zone;
        PxRect card;
        lock (_gate)
        {
            zone = _fanZone;
            card = _layout.CardRectAt(_layout.LeftFor(PanelState.Open));
        }
        if (card.Contains(x, y) || (zone is { } z && z.Contains(x, y))) return;
        if (_shareActive) _ui?.TryEnqueue(() => _share?.Cancel());   // клик вне карточки отменяет поиск и передачу
        if (zone is not null) _ui?.TryEnqueue(() => _fan?.Hide());
    }

    /// <summary>Пересчёт по WM_DISPLAYCHANGE / WM_DPICHANGED / WM_SETTINGCHANGE. Только из UI-потока.</summary>
    public void OnDisplaysChanged()
    {
        if (_window is null || _machine is null) return;
        var (layout, mode, scale, primary) = Measure();
        lock (_gate)
        {
            // WM_SETTINGCHANGE шлют часто (обои, тема, переменные среды): без реальных изменений карточку не трогаем
            if (primary.Equals(_primary) && scale == _scale && mode == _mode) return;
            _generation++;
            _scale = scale;
            _primary = primary;   // отбрасывает SetState, уже поставленные в очередь по старой геометрии
            _layout = layout;
            _mode = mode;
            _machine.Relayout(layout, mode);
        }
        _fan?.Hide();
        // смена геометрии отменяет только поиск и выбор: идущая отправка доходит до конца (плитка перерисуется после Relayout)
        if (_share?.State is ShareTileState.Searching or ShareTileState.Choosing) _share.Cancel();
        _window.Relayout(layout, mode, scale);
        _dropTarget?.Refresh();   // WinUI может пересоздать дочерние HWND при смене DPI/дисплея
        CardHidden();   // Relayout не поднимает Settled
    }

    (CardLayout layout, EdgeMode mode, double scale, PxRect primary) Measure()
    {
        var primary = Displays.Primary();
        double scale = Displays.PrimaryScale();
        var layout = new CardLayout(primary, scale);
        var band = layout.ActivationBand;
        var mode = MonitorTopology.HasRightNeighbor(primary, Displays.All(), band.Top, band.Bottom) ? EdgeMode.InPlace : EdgeMode.Slide;
        return (layout, mode, scale, primary);
    }

    // рабочий поток хука; WinUI-объекты не трогает
    void OnMouse(int x, int y)
    {
        var machine = _machine;
        var hook = _hook;
        if (machine is null || _debugOpen) return;

        bool button = PrimaryButton.IsDown();
        PxRect primary;
        bool hidden;
        lock (_gate) { primary = _primary; hidden = machine.State == PanelState.Hidden; }
        // дорогая проверка — только из Hidden и только внутри полосы активации
        bool busy = false;
        if (hidden)
        {
            CardLayout layout;
            lock (_gate) layout = _layout;
            var b = layout.ActivationBand;
            if (y >= b.Top && y <= b.Bottom && x >= b.Left && x < b.Right) busy = FullscreenProbe.IsBusy(primary);
        }

        PanelState? changed;
        int gen;
        bool fast;
        lock (_gate)
        {
            changed = machine.Update(new EdgeInput(x, y, button, busy, Environment.TickCount64));
            gen = _generation;
            // без движения мыши автомату нужны таймеры: задержка скрытия (Open) и dwell in-place (Hidden у края)
            var band = _layout.ActivationBand;
            bool inBand = y >= band.Top && y <= band.Bottom && x >= band.Left && x < band.Right;
            fast = machine.State == PanelState.Open || (_mode == EdgeMode.InPlace && inBand);
        }
        if (hook is not null) hook.FastTick = fast;

        if (changed is { } s)
        {
            _ui?.TryEnqueue(() =>
            {
                lock (_gate) { if (gen != _generation) return; }
                Log.Write($"edge {s}");
                if (s != PanelState.Hidden) _trimmer.OnShown();
                else _fan?.Hide();
                _window?.SetState(s);
            });
        }
    }
}
