using System.Text.Json;

namespace SideBar.Core.Shelf;

/// <summary>Хранение стопки в JSON. Запись атомарна: <c>&lt;file&gt;.tmp</c> + <c>File.Move(overwrite)</c>; остаточный .tmp при загрузке игнорируется.</summary>
public sealed class ShelfStore(string filePath)
{
    const int LoadRetries = 4, LoadRetryDelayMs = 200, SaveRetries = 3, SaveRetryDelayMs = 100;

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
    public bool TryLoad(out IReadOnlyList<ShelfItem> items) => TryLoad(out items, out _);

    /// <summary>
    /// Как <see cref="TryLoad(out IReadOnlyList{ShelfItem})"/>, плюс отложенные удаления. Понимает оба формата:
    /// объект <c>{ "items": [...], "pendingDeletes": [...] }</c> и старый голый массив элементов (тогда pending пуст).
    /// </summary>
    public bool TryLoad(out IReadOnlyList<ShelfItem> items, out IReadOnlyList<PendingDelete> pendingDeletes)
    {
        items = [];
        pendingDeletes = [];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(filePath)) return true;
                var bytes = File.ReadAllBytes(filePath);
                if (bytes is [0xEF, 0xBB, 0xBF, ..]) bytes = bytes[3..];   // BOM
                var first = bytes.FirstOrDefault(b => !char.IsWhiteSpace((char)b));
                if (first == (byte)'[')
                {
                    var parsed = JsonSerializer.Deserialize(bytes, ShelfJsonContext.Default.ListShelfItem);
                    if (parsed is null) return false;
                    items = ShelfModel.Sanitize(parsed);
                    return true;
                }
                var file = JsonSerializer.Deserialize(bytes, ShelfJsonContext.Default.ShelfFile);
                if (file is null) return false;
                items = ShelfModel.Sanitize(file.Items ?? []);
                pendingDeletes = SanitizePending(file.PendingDeletes);
                return true;
            }
            catch (IOException e) when (attempt < LoadRetries && e is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(LoadRetryDelayMs);   // антивирус/индексатор держит файл коротко: не принимаем его за битый
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    static List<PendingDelete> SanitizePending(List<PendingDeleteDto>? raw)
    {
        var result = new List<PendingDelete>();
        if (raw is null) return result;
        var seen = new HashSet<Guid>();
        foreach (var p in raw)
            if (p is not null && p.Guid != Guid.Empty && seen.Add(p.Guid))
                result.Add(new PendingDelete(p.Guid, p.DeleteAfterUtc));
        return result;
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
    public void Save(IReadOnlyList<ShelfItem> items) => Save(items, []);

    /// <summary>Бросает IOException/UnauthorizedAccessException. Не безопасен при параллельных вызовах (только UI-поток).</summary>
    public void Save(IReadOnlyList<ShelfItem> items, IReadOnlyList<PendingDelete> pendingDeletes)
    {
        if (!TryProtectBadFile()) throw new IOException("unreadable shelf.json has no backup copy yet; not overwriting it");
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = filePath + ".tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new ShelfFile
                    {
                        Items = items.ToList(),
                        PendingDeletes = pendingDeletes.Select(p => new PendingDeleteDto { Guid = p.Guid, DeleteAfterUtc = p.DeleteAfterUtc }).ToList(),
                    }, ShelfJsonContext.Default.ShelfFile);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tmp, filePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < SaveRetries)
            {
                Thread.Sleep(SaveRetryDelayMs);   // антивирус/индексатор держит файл коротко
            }
        }
    }
}
