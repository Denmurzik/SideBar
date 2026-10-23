using System.Text.Json;

namespace SideBar.Core.Shelf;

/// <summary>Хранение стопки в JSON. Запись атомарна: <c>&lt;file&gt;.tmp</c> + <c>File.Move(overwrite)</c>; остаточный .tmp при загрузке игнорируется.</summary>
public sealed class ShelfStore(string filePath)
{
    const int LoadRetries = 4, LoadRetryDelayMs = 200;

    // файл, который не прочитан и не убран в сторону: перезаписать его Save'ом значит потерять единственную копию
    bool _unprotectedBadFile;

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
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(filePath)) return true;
                using var stream = File.OpenRead(filePath);
                var parsed = JsonSerializer.Deserialize(stream, ShelfJsonContext.Default.ListShelfItem);
                if (parsed is null) return false;
                items = ShelfModel.Sanitize(parsed);
                return true;
            }
            catch (IOException) when (attempt < LoadRetries)
            {
                Thread.Sleep(LoadRetryDelayMs);   // антивирус/индексатор держит файл коротко: не принимаем его за битый
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Откладывает нечитаемый файл в <c>shelf.bad-yyyyMMdd-HHmmss.json</c> (существующий не перезаписывается). Если перенос не удался,
    /// Save не пишет, пока не появится резервная копия (пробуется перенос, затем копия перед каждым Save). Не бросает.
    /// </summary>
    public void Quarantine()
    {
        if (!File.Exists(filePath)) return;
        _unprotectedBadFile = true;
        TryProtectBadFile();
    }

    bool TryProtectBadFile()
    {
        if (!_unprotectedBadFile) return true;
        try
        {
            if (!File.Exists(filePath)) { _unprotectedBadFile = false; return true; }
            var dir = Path.GetDirectoryName(filePath) ?? ".";
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var bad = Path.Combine(dir, $"shelf.bad-{stamp}.json");
            for (var n = 2; File.Exists(bad); n++) bad = Path.Combine(dir, $"shelf.bad-{stamp}-{n}.json");
            try { File.Move(filePath, bad, overwrite: false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                File.Copy(filePath, bad, overwrite: false);   // не двигается (занят) — хотя бы копия; Save затем заменит оригинал
            }
            _unprotectedBadFile = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Бросает IOException/UnauthorizedAccessException. Не безопасен при параллельных вызовах (только UI-поток).</summary>
    public void Save(IReadOnlyList<ShelfItem> items)
    {
        if (!TryProtectBadFile()) throw new IOException("unreadable shelf.json has no backup copy yet; not overwriting it");
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
