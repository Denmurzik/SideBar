using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Visibility = Microsoft.UI.Xaml.Visibility;   // SideBar.Core.Nearby.Visibility (Hidden/Everyone) конфликтует
using Microsoft.UI.Xaml.Media;
using SideBar.Core;
using SideBar.Core.Edge;
using SideBar.Core.Layout;
using SideBar.Core.Motion;
using SideBar.Core.Nearby;
using SideBar.Native;
using SideBar.Shelf;
using Windows.Graphics;
using WinRT.Interop;

namespace SideBar;

/// <summary>Окно = карточка; в Slide двигается само пружиной, в InPlace стоит на месте и проявляется по Opacity.</summary>
public sealed partial class CardWindow : Window
{
    const double FadeScale = 100;   // opacity 0..1 -> 0..100, чтобы пороги успокоения Spring (px) работали

    readonly Spring _pos = new(), _fade = new();
    nint _hwnd;
    CardLayout _layout = null!;
    double _scale = 1;
    EdgeMode _mode;
    PanelState _state = PanelState.Hidden;
    bool _ticking, _raiseOnTick;
    bool _bouncing;
    double _bounceStart;
    int _bouncePeekPx, _bounceLeft;
    bool IsShown { get => _shown; set { _shown = value; ZOrder.Visible = value; } }
    bool _shown;
    AlwaysActiveAcrylicBackdrop? _backdrop;

    public event Action<PanelState>? Settled;

    /// <summary>Клик по непустой стопке: App показывает веер.</summary>
    public event Action? FanRequested;

    /// <summary>Клик по иконке найденного устройства в плитке Quick Share.</summary>
    public event Action<string>? ShareDeviceChosen;

    /// <summary>Свой drag стопки/веера брошен на плитку Quick Share: пути переносимых элементов (они остаются в стопке).</summary>
    public event Action<IReadOnlyList<string>>? OwnItemsDropped;

    ShareTileModel? _share;

    public CardWindow()
    {
        InitializeComponent();
        Title = Strings.AppName;
        // ActualTheme известен только после загрузки дерева и меняется вместе с темой системы
        Root.Loaded += (_, _) => { ApplyStrokes(); _backdrop?.SetTheme(Root.ActualTheme); };
        Root.ActualThemeChanged += (_, _) => { ApplyStrokes(); _backdrop?.SetTheme(Root.ActualTheme); };
    }

    internal void BindShelf(ShelfService shelf)
    {
        Stack.HasItemsChanged += has => ShelfEmptyIcon.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Stack.FanRequested += () => FanRequested?.Invoke();
        Stack.Bind(shelf);
    }

    // Свой drag (XAML DragStarting) в OLE-цель карточки не приходит: WinUI ведёт его сама, поэтому приём - событиями плитки.
    void QsTile_DragOver(object sender, DragEventArgs e)
    {
        if (!DragOut.IsActive || _share?.State == ShareTileState.Sending) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Strings.SendToDevice;
        e.DragUIOverride.IsGlyphVisible = false;
        e.Handled = true;
    }

    void QsTile_Drop(object sender, DragEventArgs e)
    {
        if (!DragOut.IsActive || _share?.State == ShareTileState.Sending) return;
        var paths = DragOut.CarriedPaths;
        if (paths.Count == 0) return;
        DragOut.KeepItemsAfterDrop();   // отправка не уносит элементы из стопки
        e.Handled = true;
        OwnItemsDropped?.Invoke(paths);
    }

    internal void BindShare(ShareTileModel share, ReceiveTileModel recv, Action<int, bool> decide)
    {
        _share = share;
        Share.DeviceChosen += id => ShareDeviceChosen?.Invoke(id);
        Share.BindReceive(recv, decide);
        share.Changed += RenderShare;
        recv.Changed += RenderShare;
        RenderShare();
    }

    void RenderShare()
    {
        if (_share is null) return;
        try
        {
            bool busy = Share.Render(_share);
            QsLogo.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception e) { Log.Write("share render failed: " + Log.Describe(e)); }   // не должен пропустить обновление HoldOpen у остальных подписчиков
    }

    /// <summary>Плитка Quick Share в физических пикселях экрана (для drop на плитку); null, пока карточка не построена. UI-поток.</summary>
    internal PxRect? QsTileScreenRect()
    {
        try
        {
            if (Root.XamlRoot is not { } xr || QsTile.ActualWidth <= 0) return null;
            double scale = xr.RasterizationScale;
            var origin = QsTile.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
            var pos = AppWindow.Position;
            int left = pos.X + (int)Math.Round(origin.X * scale), top = pos.Y + (int)Math.Round(origin.Y * scale);
            return new PxRect(left, top, left + (int)Math.Round(QsTile.ActualWidth * scale), top + (int)Math.Round(QsTile.ActualHeight * scale));
        }
        catch (Exception e)
        {
            Log.Write("tile rect failed: " + Log.Describe(e));
            return null;
        }
    }

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Initialize(CardLayout layout, EdgeMode mode, double scale)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        _layout = layout;
        _scale = scale;
        _mode = mode;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        WindowFrame.MakeTool(_hwnd);
        ZOrder.Watch(_hwnd);
        Closed += (_, _) => ZOrder.Unwatch();
        // клик активирует карточку, Alt+F4 не должен её закрывать; выход — только из трея
        AppWindow.Closing += (_, e) => e.Cancel = true;
        Dwm.RoundCornersNoBorder(_hwnd);

        _backdrop = new AlwaysActiveAcrylicBackdrop();
        SystemBackdrop = _backdrop;
        Fill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x20, 0x20, 0x20));

        ApplyStrokes();
        Root.Opacity = mode == EdgeMode.InPlace ? 0 : 1;
        int hidden = ParkLeft(layout, mode);
        PlaceAt(hidden);
        _pos.Reset(hidden);
        _fade.Reset(0);

#if DEBUG
        foreach (var (name, state) in new (string, PanelState?)[] { ("open", PanelState.Open), ("peek", PanelState.Peek), ("hide", PanelState.Hidden), ("exit", null) })
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "SideBar_dbg_" + name);
            var s = state;
            new Thread(() =>
            {
                while (true)
                {
                    ev.WaitOne();
                    DispatcherQueue.TryEnqueue(() => { if (s is { } st) SetState(st); else Environment.Exit(0); });
                }
            }) { IsBackground = true }.Start();
        }
#endif

        // Рамку можно снять только после первого Show; показываем за краем экрана и прячем,
        // если к этому моменту SetState не успел показать окно по-настоящему.
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show(false);
            WindowFrame.Strip(_hwnd);
            if (IsShown) return;
            AppWindow.Hide();
            WorkingSet.Trim();
        });
    }

    /// <summary>
    /// Прыжок скрытой карточки у края (входящий запрос): три подскока с затуханием, затем снова скрыта. Только Slide и только из Hidden;
    /// состояние автомата не меняется. false — прыжка не будет. Если курсор дошёл до края, <see cref="SetState"/> передаёт окно пружине с текущей позиции.
    /// </summary>
    public bool StartBounce()
    {
        if (_mode != EdgeMode.Slide || _state != PanelState.Hidden || _ticking || _bouncing) return false;
        // не дальше ширины карточки
        _bouncePeekPx = (int)Math.Min(_layout.LeftFor(PanelState.Hidden) - _layout.LeftFor(PanelState.Peek), _layout.CardWidthPx / Bounce.Peaks.Max());
        _bounceLeft = _layout.LeftFor(PanelState.Hidden);
        _bounceStart = Now;
        _bouncing = true;
        PlaceAt(_bounceLeft);
        Show();
        StartTicking();
        return true;
    }

    void EndBounce()
    {
        _bouncing = false;
        CompositionTarget.Rendering -= OnTick;
        _ticking = false;
        AppWindow.Hide();
        IsShown = false;
        WorkingSet.Trim();
        Settled?.Invoke(PanelState.Hidden);
    }

    public void Relayout(CardLayout layout, EdgeMode mode, double scale)
    {
        _bouncing = false;
        if (_ticking)
        {
            CompositionTarget.Rendering -= OnTick;
            _ticking = false;
        }
        _layout = layout;
        _mode = mode;
        _scale = scale;
        _state = PanelState.Hidden;   // как EdgeStateMachine.Relayout
        ApplyStrokes();
        int hidden = ParkLeft(layout, mode);
        _pos.Reset(hidden);
        _fade.Reset(0);
        Root.Opacity = mode == EdgeMode.InPlace ? 0 : 1;
        PlaceAt(hidden);
        if (IsShown)
        {
            AppWindow.Hide();
            IsShown = false;
            WorkingSet.Trim();
        }
    }

    public void SetState(PanelState s)
    {
        var prev = _state;
        if (s == prev) return;
        _state = s;
        double now = Now;
        if (_bouncing)
        {
            // обычный автомат главнее прыжка: пружина продолжает с текущей позиции, без скачка
            _bouncing = false;
            _pos.Reset(_bounceLeft);
        }

        if (_mode == EdgeMode.Slide)
        {
            if (!IsShown && s != PanelState.Hidden) Show();
            _pos.Retarget(_layout.LeftFor(s), now);
        }
        else
        {
            bool open = EffectiveOpen(s);
            if (open == EffectiveOpen(prev)) return;
            if (open && !IsShown)
            {
                PlaceAt(_layout.LeftFor(PanelState.Open));
                Show();
            }
            _fade.Retarget(open ? FadeScale : 0, now);
        }
        if (IsShown) ZOrder.RaiseToTop(_hwnd);
        StartTicking();
    }

    // InPlace: скрытое окно стоит на месте открытой карточки (opacity 0), а не за краем — там соседний монитор
    static int ParkLeft(CardLayout layout, EdgeMode mode) =>
        layout.LeftFor(mode == EdgeMode.InPlace ? PanelState.Open : PanelState.Hidden);

    static bool EffectiveOpen(PanelState s) => s == PanelState.Open;

    void Show()
    {
        AppWindow.Show(false);
        IsShown = true;
        ZOrder.RaiseToTop(_hwnd);
    }

    void PlaceAt(int left) =>
        AppWindow.MoveAndResize(new RectInt32(left, _layout.Top, _layout.CardWidthPx, _layout.CardHeightPx));

    void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _raiseOnTick = true;
        CompositionTarget.Rendering += OnTick;
    }

    void OnTick(object? sender, object e)
    {
        if (_bouncing)
        {
            double t = Now - _bounceStart;
            if (t >= Bounce.Duration.TotalSeconds) { EndBounce(); return; }
            _bounceLeft = _layout.LeftFor(PanelState.Hidden) - (int)Math.Round(Bounce.OffsetFraction(t) * _bouncePeekPx);
            WindowFrame.MoveTo(_hwnd, _bounceLeft, _layout.Top);
            if (IsShown && _raiseOnTick) ZOrder.RaiseToTop(_hwnd);
            _raiseOnTick = false;
            return;
        }
        bool slide = _mode == EdgeMode.Slide;
        var spring = slide ? _pos : _fade;
        spring.Step(Now);
        bool settled = spring.IsSettled;
        double value = settled ? spring.Target : spring.Position;
        if (slide) WindowFrame.MoveTo(_hwnd, (int)Math.Round(value), _layout.Top);
        // Фейдится только содержимое: DesktopAcrylicBackdrop появляется сразу (слоистая альфа окна ломает акрил, S10).
        else Root.Opacity = Math.Clamp(value / FadeScale, 0, 1);
        // Show() сразу после себя не всегда удерживает TOPMOST: поднимаем на первом тике и при успокоении;
        // остальное (позже активированные окна) закрывает foreground-хук в ZOrder.
        if (IsShown && (_raiseOnTick || settled)) ZOrder.RaiseToTop(_hwnd);
        _raiseOnTick = false;
        if (!settled) return;

        CompositionTarget.Rendering -= OnTick;
        _ticking = false;
        if (slide ? _state == PanelState.Hidden : !EffectiveOpen(_state))
        {
            AppWindow.Hide();
            IsShown = false;
            WorkingSet.Trim();
        }
        Settled?.Invoke(_state);
    }

    // Все линии — 1 физ. пиксель (в epx = 1/scale); пунктир втянут на полпикселя, т.к. штрих центрируется на геометрии.
    void ApplyStrokes()
    {
        if (_hwnd == 0) return;
        double scale = _scale;   // тот же масштаб, что у CardLayout
        bool dark = Root.ActualTheme != ElementTheme.Light;
        var brush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x70, 0x8C, 0x8C, 0x8C) : Windows.UI.Color.FromArgb(0x50, 0x40, 0x40, 0x40));
        Panel.BorderThickness = new Thickness(1.0 / scale);
        Panel.BorderBrush = brush;
        QsTile.BorderThickness = new Thickness(1.0 / scale);
        QsTile.BorderBrush = brush;
        ShelfDash.StrokeThickness = 1.0 / scale;
        ShelfDash.Margin = new Thickness(0.5 / scale);
        ShelfDash.Stroke = brush;
    }
}
