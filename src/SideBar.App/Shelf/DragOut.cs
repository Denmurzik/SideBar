using Microsoft.UI.Xaml;
using SideBar.Core.Shelf;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace SideBar.Shelf;

/// <summary>
/// Вынос элементов стопки из карточки. Только Copy: Move дал бы Проводнику перенести оригиналы пользователя.
/// Элементы снимаются со стопки лишь после успешного чужого drop; на нашу же карточку вынос отклоняется (<see cref="IsActive"/>).
/// Один drag за раз, только UI-поток.
/// </summary>
static class DragOut
{
    static IReadOnlyList<ShelfItem> s_items = [];
    static bool s_keep;

    /// <summary>true между DragStarting и DropCompleted: <c>OleDropTarget</c> отвечает DROPEFFECT_NONE на свой же вынос.</summary>
    public static bool IsActive { get; private set; }

    /// <summary>Пути переносимых сейчас элементов (для drop на плитку Quick Share).</summary>
    public static IReadOnlyList<string> CarriedPaths => [.. s_items.Select(i => i.Path)];

    /// <summary>Текущий drop — отправка: элементы остаются в стопке, хотя drop принят.</summary>
    public static void KeepItemsAfterDrop() => s_keep = true;

    /// <summary>Заполняет DataPackage (StorageFile для файлов, StorageFolder для каталогов); недоступные элементы пропускает. Нечего нести — Cancel.</summary>
    public static async void Start(DragStartingEventArgs e, IReadOnlyList<ShelfItem> items)
    {
        Reset();   // потерянный DropCompleted не должен оставить все внешние drop'ы отклонёнными
        var deferral = e.GetDeferral();
        try
        {
            var storage = new List<IStorageItem>();
            var carried = new List<ShelfItem>();
            foreach (var item in items)
            {
                var si = await Resolve(item);
                if (si is null) continue;
                storage.Add(si);
                carried.Add(item);
            }
            if (storage.Count == 0)
            {
                e.Cancel = true;
                Log.Write("drag-out cancelled: no item is available");
                return;
            }
            e.Data.SetStorageItems(storage);
            e.AllowedOperations = DataPackageOperation.Copy;
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            s_items = carried;
            IsActive = true;
            Log.Write($"drag-out starting: {carried.Count} of {items.Count} item(s)");
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            Log.Write("drag-out start failed: " + Log.Describe(ex));
        }
        finally { deferral.Complete(); }
    }

    public static void Completed(DropCompletedEventArgs e, ShelfService shelf)
    {
        var carried = s_items;
        bool keep = s_keep;
        s_items = [];
        s_keep = false;
        IsActive = false;
        Log.Write($"drag-out completed: DropResult={e.DropResult}{(keep ? ", items kept (share)" : "")}");
        if (keep) return;
        if (e.DropResult == DataPackageOperation.None) return;
        shelf.RemoveAfterDragOut(carried.Select(i => i.Id).ToList());
    }

    /// <summary>Сбрасывает состояние выноса (начало нового drag, скрытие карточки).</summary>
    public static void Reset()
    {
        s_items = [];
        s_keep = false;
        IsActive = false;
    }

    static async Task<IStorageItem?> Resolve(ShelfItem item)
    {
        try
        {
            if (Directory.Exists(item.Path)) return await StorageFolder.GetFolderFromPathAsync(item.Path);
            if (File.Exists(item.Path)) return await StorageFile.GetFileFromPathAsync(item.Path);
        }
        catch (Exception ex) { Log.Write("drag-out item skipped: " + Log.Describe(ex)); }
        return null;
    }
}
