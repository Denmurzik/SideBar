using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SideBar.Core.Shelf;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace SideBar.Shelf;

/// <summary>IsIcon — значок оболочки (прозрачный, без рамки); иначе картинка (скругление и рамка). Aspect = ширина/высота.</summary>
sealed record Thumb(ImageSource Source, bool IsIcon, double Aspect);

/// <summary>
/// Общий кэш превью элементов стопки (<c>GetThumbnailAsync(SingleItem, 96)</c>) для стопки и веера, только UI-поток.
/// Нет превью (ошибка) — в кэше null, показывается глиф Document.
/// </summary>
sealed class ThumbnailCache
{
    public const int LoadSize = 96;
    const string DocumentGlyph = "\uE8A5";

    public static ThumbnailCache Shared { get; } = new();

    readonly Dictionary<Guid, Thumb?> _thumbs = [];
    readonly HashSet<Guid> _loading = [];

    /// <summary>Превью загружено (успешно): подписчики перестраивают то, что показывают.</summary>
    public event Action? Loaded;

    public Thumb? Get(Guid id) => _thumbs.GetValueOrDefault(id);

    /// <summary>Забывает превью элементов, которых больше нет в стопке.</summary>
    public void Prune(IEnumerable<ShelfItem> alive)
    {
        var ids = alive.Select(i => i.Id).ToHashSet();
        foreach (var id in _thumbs.Keys.Where(id => !ids.Contains(id)).ToList()) _thumbs.Remove(id);
    }

    /// <summary>Запускает загрузку, если превью ещё нет и оно не грузится.</summary>
    public void Request(ShelfItem item, ShelfModel model)
    {
        if (!_thumbs.ContainsKey(item.Id) && _loading.Add(item.Id)) _ = Load(item, model);
    }

    async Task Load(ShelfItem item, ShelfModel model)
    {
        Thumb? result = null;
        try
        {
            IStorageItemProperties? props = Directory.Exists(item.Path)
                ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                : await StorageFile.GetFileFromPathAsync(item.Path);
            using var thumb = await props.GetThumbnailAsync(ThumbnailMode.SingleItem, LoadSize);
            if (thumb is { Size: > 0 })
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                result = new Thumb(bmp, thumb.Type == ThumbnailType.Icon, bmp.PixelHeight > 0 ? (double)bmp.PixelWidth / bmp.PixelHeight : 1);
            }
        }
        catch (Exception ex) { Log.Write("thumbnail failed: " + Log.Describe(ex)); }
        _loading.Remove(item.Id);
        if (!model.Items.Any(i => i.Id == item.Id)) return;   // пока грузилось, элемент ушёл
        _thumbs[item.Id] = result;
        if (result != null) Loaded?.Invoke();
    }

    /// <summary>Визуал превью, вписанный в квадрат <paramref name="box"/> epx; <paramref name="glyphSize"/> — размер глифа, когда превью нет.</summary>
    public static FrameworkElement Visual(Thumb? thumb, double box, double glyphSize, double rasterScale, bool dark)
    {
        FrameworkElement visual;
        if (thumb is { IsIcon: false } pic)
        {
            double w = pic.Aspect >= 1 ? box : box * pic.Aspect, h = pic.Aspect >= 1 ? box / pic.Aspect : box;
            visual = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1 / rasterScale),
                BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x70, 0x8C, 0x8C, 0x8C) : Windows.UI.Color.FromArgb(0x50, 0x40, 0x40, 0x40)),
                Child = new Image { Source = pic.Source, Stretch = Stretch.UniformToFill },
            };
        }
        else if (thumb != null)
            visual = new Image { Source = thumb.Source, Stretch = Stretch.Uniform };
        else
            visual = new FontIcon { Glyph = DocumentGlyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = glyphSize, Opacity = 0.6 };
        visual.HorizontalAlignment = HorizontalAlignment.Center;
        visual.VerticalAlignment = VerticalAlignment.Center;
        return visual;
    }
}
