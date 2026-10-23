using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SideBar.Core;
using SideBar.Core.Motion;
using SideBar.Core.Shelf;
using SideBar.Native;
using Windows.Graphics;
using WinRT.Interop;

namespace SideBar.Shelf;

/// <summary>
/// Веер: отдельное окно того же стиля, что карточка, слева от неё (зазор 8 epx, по центру карточки по вертикали).
/// Колонка строк 48 epx (превью 40 + имя), до 8 видимых, дальше прокрутка. Появляется пружиной по X из-за карточки.
/// Каждая строка выносится отдельно (только Copy, через <see cref="DragOut"/>).
/// Закрывает веер владелец (клик вне веера и карточки, скрытие карточки); сам он закрывается по Esc, если получил фокус,
/// и когда стопка опустела.
/// </summary>
public sealed partial class FanWindow : Window
{
    const double RowH = 48, PreviewBox = 40, PadV = 4, WidthEpx = 216, GapEpx = 8;
    const int MaxVisibleRows = 8;

    readonly Spring _pos = new();
    nint _hwnd;
    bool _initialized, _open, _ticking, _shown, _raiseOnTick;
    ShelfService? _shelf;
    ShelfModel? _model;
    PxRect _card;
    double _scale = 1;
    int _left, _top, _widthPx, _heightPx;
    AlwaysActiveAcrylicBackdrop? _backdrop;
    readonly Dictionary<Guid, Border> _previews = [];

    /// <summary>Зона веера (физические px) вместе с зазором до карточки — её автомат считает «над карточкой». null — веер закрыт.</summary>
    internal event Action<PxRect?>? ZoneChanged;

    public FanWindow()
    {
        InitializeComponent();
        Title = Strings.AppName;
        Root.Loaded += (_, _) => OnThemeChanged();
        Root.ActualThemeChanged += (_, _) => OnThemeChanged();
    }

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    static int Px(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    internal bool IsOpen => _open;

    internal void Bind(ShelfService shelf)
    {
        if (_model != null) _model.Changed -= OnModelChanged;
        ThumbnailCache.Shared.Loaded -= OnThumbnailLoaded;
        _shelf = shelf;
        _model = shelf.Model;
        _model.Changed += OnModelChanged;
        ThumbnailCache.Shared.Loaded += OnThumbnailLoaded;
    }

    /// <summary>Показывает веер слева от карточки <paramref name="cardRect"/> (физические px); <paramref name="scale"/> — масштаб раскладки карточки.</summary>
    internal void ShowNextTo(PxRect cardRect, double scale)
    {
        if (_model is not { Items.Count: > 0 }) return;
        _card = cardRect;
        _scale = scale;
        bool wasOpen = _open;
        _open = true;
        EnsureInitialized();
        RebuildRows();
        Layout();
        if (wasOpen)
        {
            ApplyGeometry();
        }
        else
        {
            int start = _card.Left;   // правый край веера = правый край карточки: окно целиком за ней
            _pos.Reset(start);
            PlaceAt(start);
            AppWindow.Show(false);
            _shown = true;
            WindowFrame.Strip(_hwnd);
            ZOrder.Follow(_hwnd);
            _pos.Retarget(_left, Now);
            StartTicking();
        }
        ZoneChanged?.Invoke(Zone);
    }

    /// <summary>Скрывает веер (окно остаётся для повторного показа).</summary>
    internal new void Close()
    {
        if (!_open) return;
        _open = false;
        StopTicking();
        ZOrder.Follow(0);
        if (_shown)
        {
            AppWindow.Hide();
            _shown = false;
            WorkingSet.Trim();
        }
        ZoneChanged?.Invoke(null);
    }

    PxRect Zone => new(_left, _top, _card.Left, _top + _heightPx);

    void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        _hwnd = WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        WindowFrame.MakeTool(_hwnd);
        AppWindow.Closing += (_, e) => e.Cancel = true;
        Dwm.RoundCornersNoBorder(_hwnd);
        _backdrop = new AlwaysActiveAcrylicBackdrop();
        SystemBackdrop = _backdrop;
        Fill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x20, 0x20, 0x20));
    }

    // Размер и целевая позиция по числу строк и геометрии карточки.
    void Layout()
    {
        int rows = Math.Min(MaxVisibleRows, _model!.Items.Count);
        _widthPx = Px(WidthEpx * _scale);
        _heightPx = Px((rows * RowH + 2 * PadV) * _scale);
        _left = _card.Left - Px(GapEpx * _scale) - _widthPx;
        _top = (_card.Top + _card.Bottom) / 2 - _heightPx / 2;
    }

    int CurrentLeft => _ticking ? (int)Math.Round(_pos.Position) : _left;

    void ApplyGeometry() => AppWindow.MoveAndResize(new RectInt32(CurrentLeft, _top, _widthPx, _heightPx));

    void PlaceAt(int left) => AppWindow.MoveAndResize(new RectInt32(left, _top, _widthPx, _heightPx));

    void OnModelChanged()
    {
        if (!_open) return;
        if (_model!.Items.Count == 0) { Close(); return; }
        double offset = Scroller.VerticalOffset;
        RebuildRows();
        Layout();
        ApplyGeometry();
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, offset, null, true);
        ZoneChanged?.Invoke(Zone);
    }

    // Подставляет загруженные превью на месте, без перестройки строк (иначе прокрутка сбрасывалась бы).
    void OnThumbnailLoaded()
    {
        if (!_open) return;
        foreach (var (id, host) in _previews)
            if (ThumbnailCache.Shared.Get(id) is { } thumb) host.Child = PreviewVisual(thumb);
    }

    void OnThemeChanged()
    {
        _backdrop?.SetTheme(Root.ActualTheme);
        ApplyStrokes();
        if (_open) RebuildRows();
    }

    void ApplyStrokes()
    {
        bool dark = Root.ActualTheme != ElementTheme.Light;
        Panel.BorderThickness = new Thickness(1.0 / _scale);
        Panel.BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x70, 0x8C, 0x8C, 0x8C) : Windows.UI.Color.FromArgb(0x50, 0x40, 0x40, 0x40));
    }

    void RebuildRows()
    {
        ApplyStrokes();
        ThumbnailCache.Shared.Prune(_model!.Items);
        Rows.Children.Clear();
        _previews.Clear();
        // сверху последний добавленный, как в стопке
        foreach (var item in _model.Items.Reverse())
        {
            Rows.Children.Add(MakeRow(item));
            ThumbnailCache.Shared.Request(item, _model);
        }
    }

    FrameworkElement PreviewVisual(Thumb? thumb) =>
        ThumbnailCache.Visual(thumb, PreviewBox, 24, _scale, Root.ActualTheme != ElementTheme.Light);

    FrameworkElement MakeRow(ShelfItem item)
    {
        var host = new Border { Width = PreviewBox, Height = PreviewBox, Child = PreviewVisual(ThumbnailCache.Shared.Get(item.Id)) };
        _previews[item.Id] = host;
        var name = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid { Height = RowH, Padding = new Thickness(8, 0, 8, 0), ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(host);
        grid.Children.Add(name);
        Grid.SetColumn(name, 1);
        var none = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));   // прозрачный, но попадает под курсор
        var row = new Border { CanDrag = true, Background = none, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0), Child = grid };
        var hover = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = none;
        row.DragStarting += (_, e) => DragOut.Start(e, [item]);
        row.DropCompleted += (_, e) => { if (_shelf != null) DragOut.Completed(e, _shelf); };
        return row;
    }

    void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _raiseOnTick = true;
        CompositionTarget.Rendering += OnTick;
    }

    void StopTicking()
    {
        if (!_ticking) return;
        CompositionTarget.Rendering -= OnTick;
        _ticking = false;
    }

    void OnTick(object? sender, object e)
    {
        _pos.Step(Now);
        bool settled = _pos.IsSettled;
        double value = settled ? _pos.Target : _pos.Position;
        WindowFrame.MoveTo(_hwnd, (int)Math.Round(value), _top);
        // Show() сразу после себя не всегда держит порядок: на первом тике и при успокоении снова ставим под карточку
        if (_raiseOnTick || settled) ZOrder.Follow(_hwnd);
        _raiseOnTick = false;
        if (settled) StopTicking();
    }
}
