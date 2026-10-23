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

    /// <summary>true между DragStarting и DropCompleted: <c>OleDropTarget</c> отвечает DROPEFFECT_NONE на свой же вынос.</summary>
    public static bool IsActive { get; private set; }

    /// <summary>Заполняет DataPackage (StorageFile для файлов, StorageFolder для каталогов); недоступные элементы пропускает. Нечего нести — Cancel.</summary>
    public static async void Start(DragStartingEventArgs e, IReadOnlyList<ShelfItem> items)
    {
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
        s_items = [];
        IsActive = false;
        Log.Write($"drag-out completed: DropResult={e.DropResult}");
        if (e.DropResult == DataPackageOperation.None) return;
        foreach (var item in carried) shelf.RemoveAfterDragOut(item.Id);
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
