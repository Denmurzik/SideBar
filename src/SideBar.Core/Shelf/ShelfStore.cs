using System.Text.Json;

namespace SideBar.Core.Shelf;

/// <summary>Хранение стопки в JSON. Запись атомарна: <c>&lt;file&gt;.tmp</c> + <c>File.Move(overwrite)</c>; остаточный .tmp при загрузке игнорируется.</summary>
public sealed class ShelfStore(string filePath)
{
    public IReadOnlyList<ShelfItem> Load()
    {
        try
        {
            if (!File.Exists(filePath)) return [];
            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize(stream, ShelfJsonContext.Default.ListShelfItem) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<ShelfItem> items)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = filePath + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, items.ToList(), ShelfJsonContext.Default.ListShelfItem);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, filePath, overwrite: true);
    }
}
