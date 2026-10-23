using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideBar.Core;
using SideBar.Native;
using Windows.Graphics;
using WinRT.Interop;

namespace SideBar.Sharing;

/// <summary>Содержимое запасного уведомления: строки, кнопки, живёт до клика, отмены или автоскрытия.</summary>
sealed record PromptSpec(string[] Lines, (string Label, Action Act)[] Buttons, TimeSpan? AutoHide);

/// <summary>
/// Запасной путь, когда уведомления Windows недоступны: панель того же стиля, что карточка, слева от неё.
/// Окно не активируется (WS_EX_NOACTIVATE). Показывает по одному запросу, остальные ждут в очереди.
/// Все методы — только с UI-потока.
/// </summary>
public sealed partial class PromptWindow : Window
{
    const double WidthEpx = 300, GapEpx = 8;

    sealed class Entry(PromptSpec spec, CancellationToken ct)
    {
        public PromptSpec Spec { get; } = spec;
        public CancellationToken Ct { get; } = ct;
        public CancellationTokenRegistration Registration;
        public DispatcherTimer? Timer;
    }

    readonly Queue<Entry> _queue = new();
    Entry? _current;
    nint _hwnd;
    bool _initialized, _shown;
    AlwaysActiveAcrylicBackdrop? _backdrop;
    Func<(PxRect Card, double Scale)>? _anchor;

    public PromptWindow()
    {
        InitializeComponent();
        Title = Strings.AppName;
        Root.ActualThemeChanged += (_, _) => OnThemeChanged();
    }

    /// <summary>Где стоит открытая карточка (физические px) и масштаб раскладки.</summary>
    internal void SetAnchor(Func<(PxRect Card, double Scale)> anchor) => _anchor = anchor;

    internal void Present(PromptSpec spec, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        var entry = new Entry(spec, ct);
        entry.Registration = ct.Register(() => DispatcherQueue.TryEnqueue(() => Dismiss(entry)));
        _queue.Enqueue(entry);
        if (_current is null) ShowNext();
    }

    void ShowNext()
    {
        while (_queue.Count > 0)
        {
            var entry = _queue.Dequeue();
            if (entry.Ct.IsCancellationRequested) { entry.Registration.Dispose(); continue; }
            _current = entry;
            Build(entry);
            if (entry.Spec.AutoHide is { } hide)
            {
                var timer = new DispatcherTimer { Interval = hide };
                timer.Tick += (_, _) => Dismiss(entry);
                entry.Timer = timer;
                timer.Start();
            }
            return;
        }
        HideWindow();
    }

    void Dismiss(Entry entry)
    {
        entry.Timer?.Stop();
        entry.Registration.Dispose();
        if (_current != entry)
        {
            // ещё в очереди: пересобираем без него
            var rest = _queue.Where(e => e != entry).ToList();
            _queue.Clear();
            foreach (var e in rest) _queue.Enqueue(e);
            return;
        }
        _current = null;
        ShowNext();
    }

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
        OnThemeChanged();
    }

    void OnThemeChanged()
    {
        _backdrop?.SetTheme(Root.ActualTheme);
        bool dark = Root.ActualTheme != ElementTheme.Light;
        Panel.BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x70, 0x8C, 0x8C, 0x8C) : Windows.UI.Color.FromArgb(0x50, 0x40, 0x40, 0x40));
    }

    void Build(Entry entry)
    {
        EnsureInitialized();
        Body.Children.Clear();
        for (int i = 0; i < entry.Spec.Lines.Length; i++)
        {
            Body.Children.Add(new TextBlock
            {
                Text = entry.Spec.Lines[i],
                FontSize = 13,
                FontWeight = i == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = i == 0 ? 3 : 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        if (entry.Spec.Buttons.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (label, act) in entry.Spec.Buttons)
            {
                var button = new Button { Content = label };
                button.Click += (_, _) =>
                {
                    try { act(); }
                    catch (Exception e) { Log.Write("prompt action failed: " + Log.Describe(e)); }
                    Dismiss(entry);
                };
                row.Children.Add(button);
            }
            Body.Children.Add(row);
        }
        Place();
        // шаблоны кнопок применяются только после первого показа: размер по содержимому уточняем в следующем такте
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (_current == entry) Place(); });
    }

    void Place()
    {
        Root.UpdateLayout();
        var (card, scale) = _anchor?.Invoke() ?? (default, 1.0);
        Root.Measure(new Windows.Foundation.Size(WidthEpx, double.PositiveInfinity));
        double height = Root.DesiredSize.Height;
        int w = (int)Math.Round(WidthEpx * scale), h = (int)Math.Round(height * scale);
        int left = card.Left - (int)Math.Round(GapEpx * scale) - w;
        int top = (card.Top + card.Bottom) / 2 - h / 2;
        AppWindow.MoveAndResize(new RectInt32(left, top, w, h));
        if (!_shown)
        {
            AppWindow.Show(false);
            _shown = true;
            WindowFrame.Strip(_hwnd);
        }
    }

    void HideWindow()
    {
        if (!_shown) return;
        AppWindow.Hide();
        _shown = false;
    }
}
