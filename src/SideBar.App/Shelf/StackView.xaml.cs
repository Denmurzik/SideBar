using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideBar.Core.Shelf;

namespace SideBar.Shelf;

/// <summary>
/// Стопка превью: до 3 верхних элементов 96×96 epx со смещением 6 epx вниз-вправо и поворотом −4°/0°/+4°, сверху последний добавленный;
/// бейдж с числом при &gt; 1. Клик — <see cref="FanRequested"/>; перетаскивание — вынос всей стопки.
/// </summary>
public sealed partial class StackView : UserControl
{
    const int MaxLayers = 3, PreviewSize = ThumbnailCache.LoadSize;
    const double Step = 6;
    const string DocumentGlyph = "\uE8A5";
    // вращение по глубине от верхнего: сверху 0°, под ним −4°, глубже +4°
    static readonly double[] AngleByDepth = [0, -4, 4];

    ShelfService? _shelf;
    ShelfModel? _model;

    public event Action? FanRequested;

    /// <summary>Видна ли хотя бы одна карточка стопки (для скрытия иконки пустой полки).</summary>
    public event Action<bool>? HasItemsChanged;

    public StackView()
    {
        InitializeComponent();
        Tapped += (_, _) => { if (_model?.Items.Count > 0) FanRequested?.Invoke(); };
        DragStarting += (_, e) =>
        {
            if (_model is { Items.Count: > 0 } m) DragOut.Start(e, m.Items.ToList());
            else e.Cancel = true;
        };
        DropCompleted += (_, e) => { if (_shelf != null) DragOut.Completed(e, _shelf); };
    }

    internal void Bind(ShelfService shelf)
    {
        if (_model != null) _model.Changed -= Rebuild;
        ThumbnailCache.Shared.Loaded -= Rebuild;
        _shelf = shelf;
        _model = shelf.Model;
        _model.Changed += Rebuild;
        ThumbnailCache.Shared.Loaded += Rebuild;
        Rebuild();
    }

    void Rebuild()
    {
        var items = _model!.Items;
        ThumbnailCache.Shared.Prune(items);

        Layers.Children.Clear();
        int n = Math.Min(MaxLayers, items.Count);
        for (int k = 0; k < n; k++)
        {
            var item = items[items.Count - n + k];   // k = 0 — самый нижний из показанных
            Layers.Children.Add(MakeLayer(item, k, n));
            ThumbnailCache.Shared.Request(item, _model);
        }
        Badge.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = items.Count.ToString();
        HasItemsChanged?.Invoke(items.Count > 0);
    }

    FrameworkElement MakeLayer(ShelfItem item, int k, int n)
    {
        int depth = n - 1 - k;
        double center = (n - 1) * Step / 2;
        var holder = new Grid
        {
            Width = PreviewSize,
            Height = PreviewSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new TransformGroup
            {
                Children =
                {
                    new RotateTransform { Angle = AngleByDepth[depth] },
                    new TranslateTransform { X = k * Step - center, Y = k * Step - center },
                },
            },
        };
        // подложки нет: под акрилом непрозрачный квадрат читался бы чёрной плиткой
        holder.Opacity = depth switch { 0 => 1.0, 1 => 0.85, _ => 0.7 };
        var visual = ThumbnailCache.Visual(ThumbnailCache.Shared.Get(item.Id), PreviewSize, 40, XamlRoot?.RasterizationScale ?? 1, ActualTheme != ElementTheme.Light);
        holder.Children.Add(visual);
        return holder;
    }
}
