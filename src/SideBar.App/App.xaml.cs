using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SideBar.Core;
using SideBar.Core.Edge;
using SideBar.Core.Layout;
using SideBar.Core.Nearby;
using SideBar.Edge;
using SideBar.Nearby;
using SideBar.Shelf;
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
    ShareTileModel? _share;
    NearbyService? _nearby;
    DispatcherTimer? _shareTimer;
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
        _shelf = new ShelfService();
        _shelf.Load();
        _window = new CardWindow();
        _window.BindShelf(_shelf);
        _window.FanRequested += OnFanRequested;
        InitShare();
        _window.Settled += s =>
        {
            if (s != PanelState.Hidden) return;
            DragOut.Reset();
            _trimmer.OnHidden();
        };

        var (layout, mode, scale, primary) = Measure();
        _layout = layout;
        _mode = mode;
        _scale = scale;
        _primary = primary;
        _window.Initialize(layout, mode, scale);
        _machine = new EdgeStateMachine(layout, mode);
        _trimmer.OnHidden();
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
        });
        _hook = new MouseHook(OnMouse, OnPrimaryDown);
        string exe = Environment.ProcessPath!;
        var autostart = new Autostart();
        try { autostart.RepairPath(exe); } catch { /* политика/доступ к реестру: запуск не должен падать из-за автозапуска */ }
        _messages = new MessageWindow(OnDisplaysChanged);
        _tray = new TrayIcon(_messages, autostart, exe, () => _ui?.TryEnqueue(ExitApp));

        // к первой обрезке окно ещё достраивается: повторяем через 2 с, если карточка всё ещё скрыта
        _startupTrim = new Timer(_ => _ui?.TryEnqueue(() => { if (_machine?.State == PanelState.Hidden) _trimmer.OnHidden(); }), null, 2000, Timeout.Infinite);
    }

    void InitShare()
    {
        _share = new ShareTileModel();
        _nearby = new NearbyService(_ui!);
        _window!.BindShare(_share);
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
        _shareTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _shareTimer.Tick += (_, _) => _share.Tick(DateTimeOffset.UtcNow);
    }

    bool IsOverShareTile(int x, int y) => _window?.QsTileScreenRect() is { } r && r.Contains(x, y);

    /// <summary>Drop файлов (или переносимых элементов стопки) на плитку Quick Share: UI-поток.</summary>
    void OnShareDrop(IReadOnlyList<string> paths)
    {
        if (_share is null || _nearby is null) return;
        _share.BeginShare(paths, DateTimeOffset.UtcNow);
        if (_share.State == ShareTileState.Searching) _nearby.StartShare(paths);
    }

    void OnShareChanged()
    {
        bool keep = _share!.KeepsCardOpen;
        _shareActive = keep;
        lock (_gate) { if (_machine is not null) _machine.HoldOpen = _share.HoldsCardOpen; }
        if (_share.State == ShareTileState.Idle) _shareTimer?.Stop();
        else _shareTimer?.Start();
        // поиск и соединение больше не нужны ни при итоге, ни при отмене; Cancel идемпотентна
        if (!keep) _nearby?.Cancel();
        Log.Write($"share tile {_share.State}");
    }

    // порядок важен: сначала снять хук, затем убрать иконку
    void ExitApp()
    {
        try
        {
            try { _nearby?.Dispose(); } catch { }
            try { _hook?.Dispose(); } catch { }
            try { _dropTarget?.Dispose(); } catch { }
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
        _share?.Cancel();
        _window.Relayout(layout, mode, scale);
        _dropTarget?.Refresh();   // WinUI может пересоздать дочерние HWND при смене DPI/дисплея
        _trimmer.OnHidden();   // Relayout не поднимает Settled
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
        if (machine is null) return;

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
