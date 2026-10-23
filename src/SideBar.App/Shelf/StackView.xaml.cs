using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SideBar.Core.Shelf;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace SideBar.Shelf;

/// <summary>
/// Стопка превью: до 3 верхних элементов 96×96 epx со смещением 6 epx вниз-вправо и поворотом −4°/0°/+4°, сверху последний добавленный;
/// бейдж с числом при &gt; 1. Клик — <see cref="FanRequested"/>; перетаскивание — вынос всей стопки.
/// </summary>
public sealed partial class StackView : UserControl
{
    const int MaxLayers = 3, PreviewSize = 96;
    const double Step = 6;
    const string DocumentGlyph = "\uE8A5";
    // вращение по глубине от верхнего: сверху 0°, под ним −4°, глубже +4°
    static readonly double[] AngleByDepth = [0, -4, 4];

    ShelfService? _shelf;
    ShelfModel? _model;
    readonly Dictionary<Guid, Thumb?> _thumbs = [];   // null — миниатюры нет, показывается глиф

    /// <summary>IsIcon — значок оболочки (прозрачный, без рамки); иначе картинка (скругление и рамка). Aspect = ширина/высота.</summary>
    sealed record Thumb(ImageSource Source, bool IsIcon, double Aspect);
    readonly HashSet<Guid> _loading = [];

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
        _shelf = shelf;
        _model = shelf.Model;
        _model.Changed += Rebuild;
        Rebuild();
    }

    void Rebuild()
    {
        var items = _model!.Items;
        var alive = items.Select(i => i.Id).ToHashSet();
        foreach (var id in _thumbs.Keys.Where(id => !alive.Contains(id)).ToList()) _thumbs.Remove(id);

        Layers.Children.Clear();
        int n = Math.Min(MaxLayers, items.Count);
        for (int k = 0; k < n; k++)
        {
            var item = items[items.Count - n + k];   // k = 0 — самый нижний из показанных
            Layers.Children.Add(MakeLayer(item, k, n));
            if (!_thumbs.ContainsKey(item.Id) && _loading.Add(item.Id)) _ = LoadThumbnail(item);
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
        _thumbs.TryGetValue(item.Id, out var thumb);
        FrameworkElement visual;
        if (thumb is { IsIcon: false } pic)
        {
            double w = pic.Aspect >= 1 ? PreviewSize : PreviewSize * pic.Aspect, h = pic.Aspect >= 1 ? PreviewSize / pic.Aspect : PreviewSize;
            double scale = XamlRoot?.RasterizationScale ?? 1;
            bool dark = ActualTheme != ElementTheme.Light;
            visual = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1 / scale),
                BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x70, 0x8C, 0x8C, 0x8C) : Windows.UI.Color.FromArgb(0x50, 0x40, 0x40, 0x40)),
                Child = new Image { Source = pic.Source, Stretch = Stretch.UniformToFill },
            };
        }
        else if (thumb != null)
            visual = new Image { Source = thumb.Source, Stretch = Stretch.Uniform };
        else
            visual = new FontIcon { Glyph = DocumentGlyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 40, Opacity = 0.6 };
        visual.HorizontalAlignment = HorizontalAlignment.Center;
        visual.VerticalAlignment = VerticalAlignment.Center;
        var frame = visual;
        holder.Children.Add(frame);
        return holder;
    }

    async Task LoadThumbnail(ShelfItem item)
    {
        Thumb? result = null;
        try
        {
            IStorageItemProperties? props = Directory.Exists(item.Path)
                ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                : await StorageFile.GetFileFromPathAsync(item.Path);
            using var thumb = await props.GetThumbnailAsync(ThumbnailMode.SingleItem, PreviewSize);
            if (thumb is { Size: > 0 })
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                result = new Thumb(bmp, thumb.Type == ThumbnailType.Icon, bmp.PixelHeight > 0 ? (double)bmp.PixelWidth / bmp.PixelHeight : 1);
            }
        }
        catch (Exception ex) { Log.Write("thumbnail failed: " + Log.Describe(ex)); }
        _loading.Remove(item.Id);
        if (_model is null || !_model.Items.Any(i => i.Id == item.Id)) return;   // пока грузилось, элемент ушёл
        _thumbs[item.Id] = result;
        if (result != null) Rebuild();
    }
}
