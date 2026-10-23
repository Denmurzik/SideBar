using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideBar.Core;
using SideBar.Native;
using Windows.Graphics;
using WinRT.Interop;

namespace SideBar.Sharing;

/// <summary>
/// Содержимое запасного уведомления: строки, отдельная строка PIN (её имя отправителя вытеснить не может), кнопки.
/// <paramref name="AutoHide"/> null — запрос согласия, живёт до решения или отмены.
/// </summary>
sealed record PromptSpec(string[] Lines, string? Pin, (string Label, Action Act)[] Buttons, TimeSpan? AutoHide);

/// <summary>Где стоит открытая карточка (физические px), масштаб, рабочая область основного экрана и зона веера, которую панель обходит.</summary>
readonly record struct PromptAnchor(PxRect Card, double Scale, PxRect Work, PxRect? Avoid);

/// <summary>
/// Запасной путь, когда уведомления Windows недоступны: панель того же стиля, что карточка, слева от неё.
/// Показывает все живые записи стопкой: запросы согласия сверху (у каждого свои PIN и кнопки), ниже — итоговые заметки;
/// не больше <see cref="MaxEntries"/>, лишними становятся самые старые заметки. Окно не активируется. Только UI-поток.
/// </summary>
public sealed partial class PromptWindow : Window
{
    const double WidthEpx = 300, GapEpx = 8;
    const int MaxEntries = 4;

    sealed class Entry(PromptSpec spec, CancellationToken ct)
    {
        public PromptSpec Spec { get; } = spec;
        public CancellationToken Ct { get; } = ct;
        public CancellationTokenRegistration Registration;
        public DispatcherTimer? Timer;
        public bool Sticky => Spec.AutoHide is null;
    }

    readonly List<Entry> _entries = [];   // порядок прихода
    readonly Dictionary<Entry, Border> _views = [];   // элементы живут, пока жива запись: пересборка не пересоздаёт кнопки под курсором
    nint _hwnd;
    bool _initialized, _shown;
    AlwaysActiveAcrylicBackdrop? _backdrop;
    Func<PromptAnchor>? _anchor;

    /// <summary>Прямоугольник окна (физические px) или null, когда оно скрыто.</summary>
    internal event Action<PxRect?>? ZoneChanged;

    public PromptWindow()
    {
        InitializeComponent();
        Title = Strings.AppName;
        Root.ActualThemeChanged += (_, _) => OnThemeChanged();
    }

    internal void SetAnchor(Func<PromptAnchor> anchor) => _anchor = anchor;

    internal void Present(PromptSpec spec, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        var entry = new Entry(spec, ct);
        if (_entries.Count >= MaxEntries)
        {
            var oldestNote = _entries.FirstOrDefault(e => !e.Sticky);
            if (oldestNote is not null) Remove(oldestNote);
            else if (!entry.Sticky) return;   // стопка занята одними запросами: заметка не нужна
        }
        entry.Registration = ct.Register(() => DispatcherQueue.TryEnqueue(() => Dismiss(entry)));
        if (spec.AutoHide is { } hide)
        {
            var timer = new DispatcherTimer { Interval = hide };
            timer.Tick += (_, _) => Dismiss(entry);
            entry.Timer = timer;
            timer.Start();
        }
        _entries.Add(entry);
        _views[entry] = Build(entry);
        Sync();
    }

    void Dismiss(Entry entry)
    {
        if (!_entries.Contains(entry)) return;
        Remove(entry);
        Sync();
    }

    void Remove(Entry entry)
    {
        entry.Timer?.Stop();
        entry.Registration.Dispose();
        _entries.Remove(entry);
        _views.Remove(entry);
    }

    /// <summary>Положение пересчитывается при смене зоны веера.</summary>
    internal void Reposition()
    {
        if (_shown) Place();
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

    /// <summary>Приводит стопку к порядку «запросы, затем заметки (новые выше)»: меняются только затронутые элементы.</summary>
    void Sync()
    {
        if (_entries.Count == 0)
        {
            Body.Children.Clear();
            HideWindow();
            return;
        }
        EnsureInitialized();
        var ordered = _entries.Where(e => e.Sticky).Concat(_entries.Where(e => !e.Sticky).Reverse()).Select(e => _views[e]).ToList();
        foreach (var stale in Body.Children.Where(c => !ordered.Contains(c)).ToList()) Body.Children.Remove(stale);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i < Body.Children.Count && ReferenceEquals(Body.Children[i], ordered[i])) continue;
            Body.Children.Remove(ordered[i]);
            Body.Children.Insert(i, ordered[i]);
        }
        var line = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].BorderBrush = line;
            ordered[i].BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
            ordered[i].Padding = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
        }
        Place();
        // шаблоны кнопок применяются только после первого показа: размер по содержимому уточняем в следующем такте
        var snapshot = _entries.Count;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (_shown && _entries.Count == snapshot) Place(); });
    }

    Border Build(Entry entry)
    {
        var panel = new StackPanel { Spacing = 6 };
        for (int i = 0; i < entry.Spec.Lines.Length; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = entry.Spec.Lines[i],
                FontSize = 13,
                FontWeight = i == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        if (entry.Spec.Pin is { } pin)
        {
            // отдельная строка без усечения: PIN нельзя вытеснить из видимой области текстом отправителя
            panel.Children.Add(new TextBlock { Text = Strings.Pin(pin), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
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
            panel.Children.Add(row);
        }
        return new Border { Child = panel };
    }

    void Place()
    {
        var a = _anchor?.Invoke() ?? new PromptAnchor(default, 1.0, new PxRect(0, 0, 1920, 1080), null);
        Root.UpdateLayout();
        Root.Measure(new Windows.Foundation.Size(WidthEpx, double.PositiveInfinity));
        int w = (int)Math.Round(WidthEpx * a.Scale), h = (int)Math.Round(Root.DesiredSize.Height * a.Scale);
        int gap = (int)Math.Round(GapEpx * a.Scale);
        int left = a.Card.Left - gap - w;
        int top = (a.Card.Top + a.Card.Bottom) / 2 - h / 2;
        if (a.Avoid is { } fan && left < fan.Right && left + w > fan.Left && top < fan.Bottom && top + h > fan.Top)
        {
            // веер занимает тот же столбец: панель выше него, а если места нет — ниже
            top = fan.Top - gap - h;
            if (top < a.Work.Top) top = fan.Bottom + gap;
        }
        // окно не выходит за рабочую область (4 записи, размещение под веером)
        top = Math.Max(a.Work.Top + gap, Math.Min(top, a.Work.Bottom - gap - h));
        left = Math.Max(a.Work.Left, left);
        AppWindow.MoveAndResize(new RectInt32(left, top, w, h));
        if (!_shown)
        {
            AppWindow.Show(false);
            _shown = true;
            WindowFrame.Strip(_hwnd);
        }
        ZoneChanged?.Invoke(new PxRect(left, top, left + w, top + h));
    }

    void HideWindow()
    {
        if (!_shown) return;
        AppWindow.Hide();
        _shown = false;
        ZoneChanged?.Invoke(null);
    }
}
