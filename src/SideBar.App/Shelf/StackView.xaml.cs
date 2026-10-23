using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideBar.Core.Shelf;

namespace SideBar.Shelf;

/// <summary>
/// Стопка превью: до 10 верхних элементов 96×96 epx лежащих друг под другом вокруг общего центра с небольшим разбросом сдвига (±5 epx) и поворота (±6°) по Id и затемнением задних
/// (раскладка — <see cref="StackPile"/>), сверху последний добавленный;
/// бейдж с числом при &gt; 1. Клик — <see cref="FanRequested"/>; перетаскивание — вынос всей стопки.
/// </summary>
public sealed partial class StackView : UserControl
{
    const int PreviewSize = ThumbnailCache.LoadSize;

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
        int n = StackPile.VisibleLayers(items.Count);
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
        var layout = StackPile.Layer(k, n, item.Id);
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
                    new RotateTransform { Angle = layout.Angle },
                    new TranslateTransform { X = layout.X, Y = layout.Y },
                },
            },
        };
        // подложки нет: под акрилом непрозрачный квадрат читался бы чёрной плиткой
        holder.Opacity = layout.Opacity;
        var visual = ThumbnailCache.Visual(ThumbnailCache.Shared.Get(item.Id), PreviewSize, 40, XamlRoot?.RasterizationScale ?? 1, ActualTheme != ElementTheme.Light);
        holder.Children.Add(visual);
        return holder;
    }
}
