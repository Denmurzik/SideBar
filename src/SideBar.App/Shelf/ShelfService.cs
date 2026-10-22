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
    const int OwnedDeleteDelayMs = 30_000;

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathName(string path, char* buffer, uint length);

    readonly ShelfStore _store;
    readonly object _pendingGate = new();
    readonly HashSet<Timer> _pending = [];
    bool _batch;

    public ShelfService(string? dataDir = null)
    {
        dataDir ??= Environment.GetEnvironmentVariable("SIDEBAR_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBar");
        ShelfRoot = Path.Combine(dataDir, "Shelf");
        _store = new ShelfStore(Path.Combine(dataDir, "shelf.json"));
    }

    public string ShelfRoot { get; }

    /// <summary>Заменяется в <see cref="Load"/>: подписываться на Changed — после Load.</summary>
    public ShelfModel Model { get; private set; } = new();

    public void Load()
    {
        var model = new ShelfModel(_store.Load());
        int pruned = model.PruneMissing(File.Exists);
        Model = model;
        SweepOrphans(model);
        if (pruned > 0) Persist();
        model.Changed += OnChanged;
        Log.Write($"shelf loaded: {model.Items.Count} item(s), pruned {pruned}");
    }

    /// <summary>Вызывается из IDropTarget.Drop. Возвращает DROPEFFECT_COPY, если добавлен хотя бы один элемент.</summary>
    public int AcceptDrop(nint dataObject)
    {
        try
        {
            var payload = ReadPayload(dataObject, out var descriptors);
            var decision = DropPolicy.Decide(payload, File.Exists, TempRoots());
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

            int added = 0;
            _batch = true;
            try
            {
                switch (decision.Action)
                {
                    case DropAction.References:
                        foreach (var path in decision.ReferencePaths)
                            if (Model.Add(path, ShelfItemKind.Reference) != null) added++;
                        added += CopyTemps(decision.TempPathsToCopy);
                        break;
                    case DropAction.CopyTempPaths:
                        added += CopyTemps(decision.TempPathsToCopy);
                        break;
                    case DropAction.MaterializeVirtual:
                        added += MaterializeVirtual(dataObject, descriptors, payload.Url);
                        break;
                    case DropAction.MaterializeImage:
                        added += MaterializeImage(dataObject, payload.ImageFormat!);
                        break;
                }
            }
            finally
            {
                _batch = false;
                if (added > 0) Persist();
            }
            Log.Write($"drop {decision.Action}: added {added} of {needed}");
            return added > 0 ? DropEffectCopy : DropEffectNone;
        }
        catch (Exception e)
        {
            Log.Write("drop failed: " + e);
            return DropEffectNone;
        }
    }

    /// <summary>Снимает элемент после успешного выноса; Owned-файл удаляется через 30 с (приёмник может копировать асинхронно), только внутри Shelf\.</summary>
    public void RemoveAfterDragOut(Guid id)
    {
        var item = Model.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || !Model.Remove(id)) return;
        if (item.Kind == ShelfItemKind.Owned && IsStrictlyInsideShelf(item.Path)) ScheduleDelete(item.Path);
    }

    DropPayload ReadPayload(nint dobj, out List<VirtualDescriptor> descriptors)
    {
        var paths = OleData.ReadHDrop(dobj).Select(NormalizePath).ToList();
        descriptors = OleData.ReadDescriptors(dobj);
        var image = OleData.ImageFormat(dobj);
        var url = descriptors.Count > 0 ? OleData.ReadUrl(dobj) : null;
        bool tdOnly = paths.Count == 0 && descriptors.Count == 0 && image == null && OleData.HasFormat(dobj, "application/x-td-forward");
        return new DropPayload(paths, descriptors.Select(d => new VirtualFileInfo(d.Name, d.Size)).ToList(), url, image, tdOnly);
    }

    int CopyTemps(IReadOnlyList<string> temps)
    {
        int added = 0;
        foreach (var src in temps)
        {
            try
            {
                var path = Materializer.CopyFile(ShelfRoot, src);
                if (Model.Add(path, ShelfItemKind.Owned) != null) added++;
                else Materializer.TryDeleteDir(Path.GetDirectoryName(path)!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write("temp copy failed: " + e.GetType().Name);
            }
        }
        return added;
    }

    int MaterializeVirtual(nint dobj, List<VirtualDescriptor> descriptors, string? url)
    {
        int added = 0;
        foreach (var d in descriptors)
        {
            if (d.Size > Materializer.MaxFileBytes)
            {
                Log.Write("virtual file skipped: declared size over the limit");
                continue;
            }
            try
            {
                var path = Materializer.Materialize(ShelfRoot,
                    fs => OleData.CopyContents(dobj, d.Index, fs, Materializer.MaxFileBytes),
                    head => FileNaming.ForVirtual(d.Name, url, descriptors.Count, head, DateTime.Now));
                if (Model.Add(path, ShelfItemKind.Owned) != null) added++;
                else Materializer.TryDeleteDir(Path.GetDirectoryName(path)!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write("virtual file failed: " + e.GetType().Name);
            }
        }
        return added;
    }

    int MaterializeImage(nint dobj, string format)
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
            var path = Materializer.Materialize(ShelfRoot, write, _ => FileNaming.ForImage(ext, DateTime.Now));
            if (Model.Add(path, ShelfItemKind.Owned) != null) return 1;
            Materializer.TryDeleteDir(Path.GetDirectoryName(path)!);
        }
        catch (Exception e)   // WinRT-кодеки бросают COMException/ArgumentException/InvalidOperationException
        {
            Log.Write("image failed: " + e.GetType().Name);
        }
        return 0;
    }

    void OnChanged()
    {
        if (!_batch) Persist();
    }

    void Persist()
    {
        try { _store.Save(Model.Items); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write("shelf save failed: " + e.GetType().Name); }
    }

    /// <summary>Удаляет каталоги Shelf\&lt;guid&gt;, на которые не ссылается ни один элемент (остатки сбоя, неснятых выносов).</summary>
    void SweepOrphans(ShelfModel model)
    {
        try
        {
            if (!Directory.Exists(ShelfRoot)) return;
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in model.Items)
            {
                var parent = Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrEmpty(parent)) referenced.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)));
            }
            foreach (var dir in Directory.EnumerateDirectories(ShelfRoot))
            {
                if (!Guid.TryParse(Path.GetFileName(dir), out _)) continue;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                if (referenced.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)))) continue;
                Materializer.TryDeleteDir(dir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("orphan sweep failed: " + e.GetType().Name);
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

    void ScheduleDelete(string path)
    {
        Timer? timer = null;
        timer = new Timer(state =>
        {
            try
            {
                File.Delete(path);
                var dir = Path.GetDirectoryName(path)!;
                if (IsStrictlyInsideShelf(dir) && Guid.TryParse(Path.GetFileName(dir), out _) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write("owned delete failed: " + e.GetType().Name);
            }
            finally
            {
                lock (_pendingGate) _pending.Remove(timer!);
                timer!.Dispose();
            }
        }, null, OwnedDeleteDelayMs, Timeout.Infinite);
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
