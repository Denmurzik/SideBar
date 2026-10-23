using System.Runtime.InteropServices;
using SideBar.Core.Shelf;

namespace SideBar.Shelf;

/// <summary>
/// Стопка: загрузка/сохранение, превращение drop в элементы. Только UI-поток (IDropTarget приходит на поток окна).
/// Данные лежат в %LocalAppData%\SideBar (или в каталоге из SIDEBAR_DATA_DIR — для автотеста).
/// </summary>
sealed partial class ShelfService
{
    public const int DropEffectNone = 0, DropEffectCopy = 1;
    // приёмники читают файл позже (диалог замены в Проводнике, мессенджеры, <input type=file>, очереди копирования)
    const int DefaultOwnedDeleteDelaySec = 600, BusyRetrySec = 120, BusyGiveUpSec = 3600;
    readonly int _ownedDeleteDelayMs = ReadDeleteDelayMs();

    static int ReadDeleteDelayMs()
    {
        // SIDEBAR_OWNED_DELETE_DELAY_SEC — для автотеста
        var raw = Environment.GetEnvironmentVariable("SIDEBAR_OWNED_DELETE_DELAY_SEC");
        var sec = int.TryParse(raw, out var v) && v >= 0 ? v : DefaultOwnedDeleteDelaySec;
        return (int)Math.Min(sec * 1000L, int.MaxValue);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathName(string path, char* buffer, uint length);

    readonly ShelfStore _store;
    readonly object _pendingGate = new();
    readonly HashSet<Timer> _pending = [];
    // снятые с полки Owned-папки, которые нельзя удалять раньше срока; переживают перезапуск (shelf.json)
    readonly Dictionary<Guid, DateTimeOffset> _pendingDeletes = [];
    bool _batch;

    public ShelfService(string? dataDir = null)
    {
        dataDir ??= Environment.GetEnvironmentVariable("SIDEBAR_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBar");
        ShelfRoot = Path.Combine(dataDir, "Shelf");
        StatePath = Path.Combine(dataDir, "shelf.json");
        _store = new ShelfStore(StatePath);
    }

    public string ShelfRoot { get; }

    string StatePath { get; }

    /// <summary>Заменяется в <see cref="Load"/>: подписываться на Changed — после Load.</summary>
    public ShelfModel Model { get; private set; } = new();

    public void Load()
    {
        ShelfModel model;
        int pruned = 0, recovered = 0;
        try
        {
            bool readable = _store.TryLoad(out var items, out var pendingDeletes);
            if (readable)
            {
                model = new ShelfModel(items);
                pruned = model.PruneMissing(Exists);
                var due = AdoptPendingDeletes(model, pendingDeletes, out bool pendingChanged);
                SweepOrphans(model);
                if (pendingChanged) pruned++;   // тот же признак «надо пересохранить»
                RearmPendingDeletes(due);
            }
            else
            {
                // пустой список ссылок удалил бы все Shelf\<guid>, а они часто единственные копии: не чистим,
                // файл откладываем, а Owned-элементы восстанавливаем из самих папок
                Log.Write("shelf.json unreadable: moving the file aside, recovering owned items from Shelf folders");
                _store.Quarantine();
                model = new ShelfModel(RecoverOwned());
                recovered = model.Items.Count;
            }
        }
        catch (Exception e)
        {
            Log.Write("shelf load failed: " + Log.Describe(e));
            model = new ShelfModel();
        }
        Model = model;
        if (pruned > 0 || recovered > 0) Persist();
        model.Changed += OnChanged;
        Log.Write($"shelf loaded: {model.Items.Count} item(s), pruned {pruned}");
    }

    /// <summary>
    /// Принимает отложенные удаления из shelf.json: записи без папки (уже удалена) и записи папок, на которые ссылается элемент, отбрасываются.
    /// Возвращает принятые (guid, срок). <paramref name="changed"/> — список отличается от сохранённого.
    /// </summary>
    List<KeyValuePair<Guid, DateTimeOffset>> AdoptPendingDeletes(ShelfModel model, IReadOnlyList<PendingDelete> saved, out bool changed)
    {
        var referenced = ReferencedDirs(model);
        lock (_pendingGate)
        {
            _pendingDeletes.Clear();
            foreach (var p in saved)
            {
                var dir = GuidDir(p.Guid);
                if (!Directory.Exists(dir) || referenced.Contains(NormalizeDir(dir))) continue;
                _pendingDeletes[p.Guid] = p.DeleteAfterUtc;
            }
            changed = _pendingDeletes.Count != saved.Count;
            return [.. _pendingDeletes];
        }
    }

    /// <summary>Для каждой записи заводит таймер на срок; если срок уже прошёл — проверка «не открыт ли файл» выполняется сразу (не слепое удаление).</summary>
    void RearmPendingDeletes(List<KeyValuePair<Guid, DateTimeOffset>> due)
    {
        foreach (var (guid, when) in due)
        {
            var ms = (when - DateTimeOffset.UtcNow).TotalMilliseconds;
            ScheduleDelete(guid, ms <= 0 ? 0 : (int)Math.Min(ms, int.MaxValue));
        }
    }

    /// <summary>Папка элемента: Materializer называет её <c>Guid.ToString("N")</c>.</summary>
    string GuidDir(Guid guid) => Path.Combine(ShelfRoot, guid.ToString("N"));

    static string NormalizeDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    static HashSet<string> ReferencedDirs(ShelfModel model)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in model.Items)
        {
            var parent = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(parent)) referenced.Add(NormalizeDir(parent));
        }
        return referenced;
    }

    /// <summary>
    /// Owned-элементы по папкам Shelf\<guid> с ровно одним обычным файлом (*.part не считаются): Id = guid папки, AddedAt = время создания.
    /// Остальные папки не трогаем. Старейшие сверх лимита отбрасываются из списка (папки остаются).
    /// </summary>
    List<ShelfItem> RecoverOwned()
    {
        var result = new List<ShelfItem>();
        try
        {
            if (!Directory.Exists(ShelfRoot)) return result;
            foreach (var dir in Directory.EnumerateDirectories(ShelfRoot))
            {
                if (!Guid.TryParse(Path.GetFileName(dir), out var id) || id == Guid.Empty) continue;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                var entries = Directory.EnumerateFileSystemEntries(dir)
                    .Where(e => !e.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).ToList();
                if (entries.Count != 1 || Directory.Exists(entries[0])) continue;
                if ((File.GetAttributes(entries[0]) & FileAttributes.ReparsePoint) != 0) continue;
                result.Add(new ShelfItem(id, Path.GetFileName(entries[0]), entries[0], ShelfItemKind.Owned,
                    new DateTimeOffset(Directory.GetCreationTime(dir))));
            }
        }
        catch (Exception e) { Log.Write("owned recovery failed: " + Log.Describe(e)); }
        result.Sort((a, b) => a.AddedAt.CompareTo(b.AddedAt));
        return result.Count > ShelfModel.MaxItems ? result[^ShelfModel.MaxItems..] : result;
    }

    /// <summary>Файл или каталог: каталоги кладутся в стопку ссылками.</summary>
    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Вызывается из IDropTarget.Drop. Возвращает DROPEFFECT_COPY, если добавлен хотя бы один элемент.</summary>
    public int AcceptDrop(nint dataObject)
    {
        try
        {
            var payload = ReadPayload(dataObject, out var descriptors);
            var decision = DropPolicy.Decide(payload, Exists, TempRoots());
            int needed = decision.Action switch
            {
                DropAction.References => decision.ReferencePaths.Count + decision.TempPathsToCopy.Count,
                DropAction.CopyTempPaths => decision.TempPathsToCopy.Count,
                DropAction.MaterializeVirtual => descriptors.Count,
                DropAction.MaterializeImage => 1,
                _ => 0,
            };
            if (needed == 0)
            {
                Log.Write(payload.OnlyTdForward ? "drop rejected: only application/x-td-forward" : "drop rejected: nothing to accept");
                return DropEffectNone;
            }
            if (Model.Items.Count + needed > ShelfModel.MaxItems)
            {
                Log.Write($"drop rejected: shelf limit {ShelfModel.MaxItems} (have {Model.Items.Count}, need {needed})");
                return DropEffectNone;
            }
            if (HasOversizedFile(decision, descriptors))
            {
                Log.Write("drop rejected: a file is over the per-file limit");
                return DropEffectNone;
            }

            var added = new List<ShelfItem>();
            _batch = true;
            try
            {
                switch (decision.Action)
                {
                    case DropAction.References:
                        foreach (var path in decision.ReferencePaths)
                            Track(Model.Add(path, ShelfItemKind.Reference), added);
                        CopyTemps(decision.TempPathsToCopy, added);
                        break;
                    case DropAction.CopyTempPaths:
                        CopyTemps(decision.TempPathsToCopy, added);
                        break;
                    case DropAction.MaterializeVirtual:
                        MaterializeVirtual(dataObject, descriptors, payload.Url, added);
                        break;
                    case DropAction.MaterializeImage:
                        MaterializeImage(dataObject, payload.ImageFormat!, added);
                        break;
                }
            }
            catch (TooLargeException)
            {
                // поток оказался больше заявленного: отменяем весь drop целиком
                Rollback(added);
                added.Clear();
                Log.Write("drop rejected: a stream is over the per-file limit");
            }
            finally
            {
                _batch = false;
                if (added.Count > 0) Persist();
            }
            Log.Write($"drop {decision.Action}: added {added.Count} of {needed}");
            return added.Count > 0 ? DropEffectCopy : DropEffectNone;
        }
        catch (Exception e)
        {
            Log.Write("drop failed: " + Log.Describe(e));
            return DropEffectNone;
        }
    }

    /// <summary>Снимает элемент после успешного выноса; Owned-файл удаляется не раньше чем через 10 мин и только если не открыт, только внутри Shelf\.</summary>
    public void RemoveAfterDragOut(IReadOnlyCollection<Guid> ids)
    {
        var items = Model.Items.Where(i => ids.Contains(i.Id)).ToList();
        if (items.Count == 0) return;
        _batch = true;
        try { Model.RemoveRange(items.Select(i => i.Id)); }
        finally { _batch = false; }
        var toDelete = new List<Guid>();
        var deleteAfter = DateTimeOffset.UtcNow.AddMilliseconds(_ownedDeleteDelayMs);
        foreach (var item in items)
        {
            if (item.Kind != ShelfItemKind.Owned || !IsStrictlyInsideShelf(item.Path)) continue;
            var dir = Path.GetDirectoryName(item.Path);
            if (dir == null || !IsStrictlyInsideShelf(dir) || !Guid.TryParse(Path.GetFileName(dir), out var guid)) continue;
            lock (_pendingGate) _pendingDeletes[guid] = deleteAfter;
            toDelete.Add(guid);
        }
        Persist();   // срок записывается до таймера: перезапуск внутри окна не должен его потерять
        foreach (var guid in toDelete) ScheduleDelete(guid, _ownedDeleteDelayMs);
    }

    DropPayload ReadPayload(nint dobj, out List<VirtualDescriptor> descriptors)
    {
        var paths = Safe(() => OleData.ReadHDrop(dobj), []).Select(NormalizePath).ToList();
        descriptors = Safe(() => OleData.ReadDescriptors(dobj), []);
        var image = Safe(() => OleData.ImageFormat(dobj), null);
        var url = descriptors.Count > 0 ? Safe(() => OleData.ReadUrl(dobj), null) : null;
        bool tdOnly = paths.Count == 0 && descriptors.Count == 0 && image == null && OleData.HasFormat(dobj, "application/x-td-forward");
        return new DropPayload(paths, descriptors.Select(d => new VirtualFileInfo(d.Name, d.Size)).ToList(), url, image, tdOnly);
    }

    static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception e) { Log.Write("drop data read failed: " + Log.Describe(e)); return fallback; }
    }

    /// <summary>Один файл больше лимита отклоняет весь drop (до того, как что-то записано).</summary>
    static bool HasOversizedFile(DropDecision decision, List<VirtualDescriptor> descriptors)
    {
        if (decision.Action == DropAction.MaterializeVirtual) return descriptors.Any(d => d.Size > Materializer.MaxFileBytes);
        foreach (var path in decision.TempPathsToCopy)
        {
            try { if (File.Exists(path) && new FileInfo(path).Length > Materializer.MaxFileBytes) return true; }
            catch (Exception e) { Log.Write("size check failed: " + Log.Describe(e)); }
        }
        return false;
    }

    static void Track(ShelfItem? item, List<ShelfItem> added)
    {
        if (item != null) added.Add(item);
    }

    /// <summary>Добавляет Owned-файл; если стопка вдруг полна — файл не оставляем.</summary>
    void TrackOwned(string path, List<ShelfItem> added)
    {
        var item = Model.Add(path, ShelfItemKind.Owned);
        if (item != null) added.Add(item);
        else DeleteOwnedDir(path);
    }

    void Rollback(List<ShelfItem> added)
    {
        foreach (var item in added)
        {
            Model.Remove(item.Id);
            if (item.Kind == ShelfItemKind.Owned && IsStrictlyInsideShelf(item.Path))
                DeleteOwnedDir(item.Path);
        }
    }

    void CopyTemps(IReadOnlyList<string> temps, List<ShelfItem> added)
    {
        foreach (var src in temps)
        {
            if (Directory.Exists(src)) { Log.Write("temp directory skipped"); continue; }
            try { TrackOwned(Materializer.CopyFile(ShelfRoot, src), added); }
            catch (TooLargeException) { throw; }
            catch (Exception e) { Log.Write("temp copy failed: " + Log.Describe(e)); }
        }
    }

    void MaterializeVirtual(nint dobj, List<VirtualDescriptor> descriptors, string? url, List<ShelfItem> added)
    {
        foreach (var d in descriptors)
        {
            try
            {
                var path = Materializer.Materialize(ShelfRoot,
                    fs => OleData.CopyContents(dobj, d.Index, fs, Materializer.MaxFileBytes, d.Size),
                    head => FileNaming.ForVirtual(d.Name, url, descriptors.Count, head, DateTime.Now));
                TrackOwned(path, added);
            }
            catch (TooLargeException) { throw; }
            catch (Exception e) { Log.Write("virtual file failed: " + Log.Describe(e)); }
        }
    }

    void MaterializeImage(nint dobj, string format, List<ShelfItem> added)
    {
        try
        {
            string ext = OleData.ExtensionOf(format);
            Action<Stream> write;
            if (format == "dib")
            {
                var png = Materializer.BmpToPng(OleData.ReadDibAsBmp(dobj));
                ext = ".png";
                write = fs => fs.Write(png);
            }
            else write = fs => OleData.CopyImage(dobj, format, fs, Materializer.MaxFileBytes);
            TrackOwned(Materializer.Materialize(ShelfRoot, write, _ => FileNaming.ForImage(ext, DateTime.Now)), added);
        }
        catch (TooLargeException) { throw; }
        catch (Exception e)   // WinRT-кодеки бросают COMException/ArgumentException/InvalidOperationException
        {
            Log.Write("image failed: " + Log.Describe(e));
        }
    }

    void OnChanged()
    {
        if (!_batch) Persist();
    }

    void Persist()
    {
        try
        {
            PendingDelete[] pending;
            lock (_pendingGate) pending = [.. _pendingDeletes.Select(p => new PendingDelete(p.Key, p.Value))];
            _store.Save(Model.Items, pending);
        }
        catch (Exception e) { Log.Write("shelf save failed: " + Log.Describe(e)); }
    }

    /// <summary>Удаляет каталоги Shelf\&lt;guid&gt;, на которые не ссылается ни один элемент (остатки сбоя, неснятых выносов).</summary>
    void SweepOrphans(ShelfModel model)
    {
        try
        {
            if (!Directory.Exists(ShelfRoot)) return;
            var referenced = ReferencedDirs(model);
            HashSet<Guid> pending;
            lock (_pendingGate) pending = [.. _pendingDeletes.Keys];
            foreach (var dir in Directory.EnumerateDirectories(ShelfRoot))
            {
                if (!Guid.TryParse(Path.GetFileName(dir), out var guid)) continue;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                if (pending.Contains(guid)) continue;   // ждёт своего срока (RearmPendingDeletes), не раньше
                if (referenced.Contains(NormalizeDir(dir))) continue;
                Materializer.TryDeleteDir(dir);
            }
        }
        catch (Exception e)
        {
            Log.Write("orphan sweep failed: " + Log.Describe(e));
        }
    }

    bool IsStrictlyInsideShelf(string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ShelfRoot)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Удаляет каталог Shelf\&lt;guid&gt; файла, только если он Guid-папка строго внутри Shelf\ (как в ScheduleDelete).</summary>
    void DeleteOwnedDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && IsStrictlyInsideShelf(dir) && Guid.TryParse(Path.GetFileName(dir), out _))
            Materializer.TryDeleteDir(dir);
    }

    /// <summary>Файл не открыт никем (в т.ч. приёмником): открывается с FileShare.None.</summary>
    static bool IsFree(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Все файлы папки не открыты никем (пустая/отсутствующая папка — свободна).</summary>
    static bool IsDirFree(string dir)
    {
        try { return !Directory.Exists(dir) || Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).All(IsFree); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Через задержку (≥ 10 мин по умолчанию) удаляет папку Shelf\&lt;guid&gt;, если в ней нет открытых файлов; занята — повтор раз в 2 мин до часа,
    /// дальше срок остаётся в shelf.json, и при следующем старте действует то же правило. Срок хранится в <c>_pendingDeletes</c> и в shelf.json.
    /// </summary>
    void ScheduleDelete(Guid guid, int delayMs, int waitedSec = 0)
    {
        Timer? timer = null;
        var retrying = false;
        timer = new Timer(state =>
        {
            try
            {
                var dir = GuidDir(guid);
                if (!IsStrictlyInsideShelf(dir) || !Directory.Exists(dir)) return;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
                if (!IsDirFree(dir))
                {
                    if (waitedSec + BusyRetrySec <= BusyGiveUpSec)
                    {
                        retrying = true;
                        ScheduleDelete(guid, BusyRetrySec * 1000, waitedSec + BusyRetrySec);
                    }
                    else Log.Write("owned file still busy, left to the next startup");
                }
                else Directory.Delete(dir, recursive: true);
            }
            catch (Exception e)
            {
                Log.Write("owned delete failed: " + Log.Describe(e));
            }
            finally
            {
                // запись снимается, только когда папки больше нет; shelf.json дозапишется при ближайшем Persist (устаревшая запись при загрузке отбрасывается)
                lock (_pendingGate)
                {
                    _pending.Remove(timer!);
                    if (!retrying && !Directory.Exists(GuidDir(guid))) _pendingDeletes.Remove(guid);
                }
                timer!.Dispose();
            }
        }, null, delayMs, Timeout.Infinite);
        lock (_pendingGate) _pending.Add(timer);
    }

    static IReadOnlyList<string> TempRoots() => DropPolicy.DefaultTempRoots().Select(NormalizePath).ToList();

    /// <summary>Снимает префикс \\?\ и разворачивает 8.3-имена (GetLongPathNameW); при ошибке возвращает путь как есть.</summary>
    internal static unsafe string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        try
        {
            uint size = 512;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = new char[size];
                fixed (char* p = buf)
                {
                    uint n = GetLongPathName(path, p, size);
                    if (n == 0) return path;
                    if (n < size) return new string(p, 0, (int)n);
                    size = n + 1;
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return path;
    }
}
