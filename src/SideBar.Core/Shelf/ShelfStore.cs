using System.Text.Json;

namespace SideBar.Core.Shelf;

/// <summary>Хранение стопки в JSON. Запись атомарна: <c>&lt;file&gt;.tmp</c> + <c>File.Move(overwrite)</c>; остаточный .tmp при загрузке игнорируется.</summary>
public sealed class ShelfStore(string filePath)
{
    /// <summary>Как <see cref="TryLoad"/>, но сбой чтения неотличим от пустой стопки. Не бросает.</summary>
    public IReadOnlyList<ShelfItem> Load()
    {
        TryLoad(out var items);
        return items;
    }

    /// <summary>
    /// false — файл есть, но не прочитан/не разобран (битый JSON, <c>null</c>, блокировка антивирусом): <paramref name="items"/> пуст.
    /// true — файл прочитан или его просто нет (тогда пусто). Не бросает.
    /// </summary>
    public bool TryLoad(out IReadOnlyList<ShelfItem> items)
    {
        items = [];
        try
        {
            if (!File.Exists(filePath)) return true;
            using var stream = File.OpenRead(filePath);
            var parsed = JsonSerializer.Deserialize(stream, ShelfJsonContext.Default.ListShelfItem);
            if (parsed is null) return false;
            items = ShelfModel.Sanitize(parsed);
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Бросает IOException/UnauthorizedAccessException. Не безопасен при параллельных вызовах (только UI-поток).</summary>
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
