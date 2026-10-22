using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SideBar.Core;
using SideBar.Core.Edge;
using SideBar.Core.Layout;
using SideBar.Core.Motion;
using SideBar.Native;
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
    bool _shown, _ticking;

    public event Action<PanelState>? Settled;

    public CardWindow()
    {
        InitializeComponent();
        Title = Strings.AppName;
        // ActualTheme известен только после загрузки дерева и меняется вместе с темой системы
        Root.Loaded += (_, _) => ApplyStrokes();
        Root.ActualThemeChanged += (_, _) => ApplyStrokes();
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
        Dwm.RoundCornersNoBorder(_hwnd);

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Fill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x20, 0x20, 0x20));

        ApplyStrokes();
        Root.Opacity = mode == EdgeMode.InPlace ? 0 : 1;
        int hidden = layout.LeftFor(PanelState.Hidden);
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
            if (_shown) return;
            AppWindow.Hide();
            WorkingSet.Trim();
        });
    }

    public void Relayout(CardLayout layout, EdgeMode mode, double scale)
    {
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
        int hidden = layout.LeftFor(PanelState.Hidden);
        _pos.Reset(hidden);
        _fade.Reset(0);
        Root.Opacity = mode == EdgeMode.InPlace ? 0 : 1;
        PlaceAt(hidden);
        if (_shown)
        {
            AppWindow.Hide();
            _shown = false;
            WorkingSet.Trim();
        }
    }

    public void SetState(PanelState s)
    {
        var prev = _state;
        if (s == prev) return;
        _state = s;
        double now = Now;

        if (_mode == EdgeMode.Slide)
        {
            if (!_shown && s != PanelState.Hidden) Show();
            _pos.Retarget(_layout.LeftFor(s), now);
        }
        else
        {
            bool open = EffectiveOpen(s);
            if (open == EffectiveOpen(prev)) return;
            if (open && !_shown)
            {
                PlaceAt(_layout.LeftFor(PanelState.Open));
                Show();
            }
            _fade.Retarget(open ? FadeScale : 0, now);
        }
        StartTicking();
    }

    static bool EffectiveOpen(PanelState s) => s == PanelState.Open;

    void Show()
    {
        AppWindow.Show(false);
        _shown = true;
    }

    void PlaceAt(int left) =>
        AppWindow.MoveAndResize(new RectInt32(left, _layout.Top, _layout.CardWidthPx, _layout.CardHeightPx));

    void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnTick;
    }

    void OnTick(object? sender, object e)
    {
        bool slide = _mode == EdgeMode.Slide;
        var spring = slide ? _pos : _fade;
        spring.Step(Now);
        bool settled = spring.IsSettled;
        double value = settled ? spring.Target : spring.Position;
        if (slide) WindowFrame.MoveTo(_hwnd, (int)Math.Round(value), _layout.Top);
        // Фейдится только содержимое: DesktopAcrylicBackdrop появляется сразу (слоистая альфа окна ломает акрил, S10).
        else Root.Opacity = Math.Clamp(value / FadeScale, 0, 1);
        if (!settled) return;

        CompositionTarget.Rendering -= OnTick;
        _ticking = false;
        if (slide ? _state == PanelState.Hidden : !EffectiveOpen(_state))
        {
            AppWindow.Hide();
            _shown = false;
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
