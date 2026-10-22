using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SideBar.Core;
using SideBar.Core.Edge;
using SideBar.Core.Layout;
using SideBar.Edge;
using SideBar.Tray;

namespace SideBar;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

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

    public App()
    {
        // второй экземпляр (автозапуск + ручной) завершается до создания любого окна
        if (!SingleInstance.TryAcquire()) Environment.Exit(0);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _window = new CardWindow();
        _window.Settled += s => { if (s == PanelState.Hidden) _trimmer.OnHidden(); };

        var (layout, mode, scale, primary) = Measure();
        _layout = layout;
        _mode = mode;
        _scale = scale;
        _primary = primary;
        _window.Initialize(layout, mode, scale);
        _machine = new EdgeStateMachine(layout, mode);
        _trimmer.OnHidden();
        _hook = new MouseHook(OnMouse);
        string exe = Environment.ProcessPath!;
        var autostart = new Autostart();
        try { autostart.RepairPath(exe); } catch { /* политика/доступ к реестру: запуск не должен падать из-за автозапуска */ }
        _messages = new MessageWindow(OnDisplaysChanged);
        _tray = new TrayIcon(_messages, autostart, exe, () => _ui?.TryEnqueue(ExitApp));

        // к первой обрезке окно ещё достраивается: повторяем через 2 с, если карточка всё ещё скрыта
        _startupTrim = new Timer(_ => _ui?.TryEnqueue(() => { if (_machine?.State == PanelState.Hidden) _trimmer.OnHidden(); }), null, 2000, Timeout.Infinite);
    }

    // порядок важен: сначала снять хук, затем убрать иконку
    void ExitApp()
    {
        try
        {
            try { _hook?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _messages?.Dispose(); } catch { }
        }
        finally { Environment.Exit(0); }
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
        _window.Relayout(layout, mode, scale);
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

        bool button = (GetAsyncKeyState(0x01) & 0x8000) != 0;
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
                if (s != PanelState.Hidden) _trimmer.OnShown();
                _window?.SetState(s);
            });
        }
    }
}
