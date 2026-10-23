using SideBar.Core.Shelf;
using SideBar.Shelf;

namespace SideBar.Sharing;

/// <summary>
/// Файлы с временных путей (распаковщики 7-Zip/WinRAR, почтовые клиенты удаляют их сразу после drop). В момент drop
/// (синхронно, быстро) файлы открываются с FileShare.Delete и держатся открытыми; копирование в
/// <c>&lt;данные&gt;\ShareStaging\&lt;guid&gt;\</c> идёт в фоне, отправка дожидается его. Копии удаляются после конца отправки.
/// </summary>
static class ShareStaging
{
    static readonly string RootPath = Path.Combine(DataDir(), "ShareStaging");
    static readonly object Gate = new();
    static readonly List<string> Dirs = [];
    static readonly List<Task> Jobs = [];
    static CancellationTokenSource _cts = new();
    static Task<IReadOnlyList<string>>? _pending;   // UI-поток

    static string DataDir()
    {
        var dir = Environment.GetEnvironmentVariable("SIDEBAR_DATA_DIR");
        return string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBar")
            : dir;
    }

    /// <summary>Забирает задачу копирования, созданную последним <see cref="Stage"/> (null, если временных файлов не было).</summary>
    public static Task<IReadOnlyList<string>>? TakePending()
    {
        var t = _pending;
        _pending = null;
        return t;
    }

    /// <summary>
    /// Возвращает исходные пути сразу. Для временных файлов открывает дескрипторы (источник уже может их удалять) и запускает
    /// фоновое копирование; его результат — пути с подставленными копиями — отдаёт <see cref="TakePending"/>.
    /// </summary>
    public static IReadOnlyList<string> Stage(IReadOnlyList<string> paths) =>
        Stage(paths, DropPolicy.DefaultTempRoots().Select(ShelfService.NormalizePath).ToList());

    static IReadOnlyList<string> Stage(IReadOnlyList<string> paths, IReadOnlyList<string> tempRoots)
    {
        _pending = null;
        var handles = new Dictionary<int, FileStream>();
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (!DropPolicy.IsTempPath(ShelfService.NormalizePath(path), tempRoots) || !File.Exists(path)) continue;
            try { handles[i] = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
            catch (Exception e) { Log.Write("share staging open failed: " + Log.Describe(e)); }
        }
        if (handles.Count == 0) return paths;

        var token = _cts.Token;
        var job = Task.Run<IReadOnlyList<string>>(() =>
        {
            var result = paths.ToList();
            try
            {
                foreach (var (index, handle) in handles)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var copy = Materializer.Materialize(RootPath, dest => CopyTo(handle, dest, token),
                            _ => FileNaming.Sanitize(Path.GetFileName(paths[index])));
                        lock (Gate) Dirs.Add(Path.GetDirectoryName(copy)!);
                        result[index] = copy;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { Log.Write("share staging failed: " + Log.Describe(e)); }   // остаётся исходный путь
                }
            }
            finally { foreach (var h in handles.Values) h.Dispose(); }
            Log.Write($"share staged {handles.Count}");
            return result;
        }, token);
        lock (Gate) Jobs.Add(job);
        _pending = job;
        return paths;
    }

    static void CopyTo(FileStream src, Stream dest, CancellationToken ct)
    {
        if (src.Length > Materializer.MaxFileBytes) throw new TooLargeException("file");
        var buf = new byte[1 << 20];
        long total = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += n;
            if (total > Materializer.MaxFileBytes) throw new TooLargeException("file");
            dest.Write(buf, 0, n);
        }
    }

    /// <summary>Отправка закончилась: незавершённое копирование отменяется, копии удаляются (файл ещё может быть открыт сессией — повторы).</summary>
    public static void Cleanup()
    {
        string[] dirs;
        Task[] jobs;
        CancellationTokenSource old;
        lock (Gate)
        {
            if (Dirs.Count == 0 && Jobs.Count == 0) return;
            dirs = [.. Dirs];
            jobs = [.. Jobs];
            Dirs.Clear();
            Jobs.Clear();
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        _pending = null;
        try { old.Cancel(); } catch (Exception) { }
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(jobs).ConfigureAwait(false); } catch (Exception) { }
            foreach (var dir in dirs)
                for (int attempt = 0; attempt < 6 && Directory.Exists(dir); attempt++)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception e) { if (attempt == 5) Log.Write("share staging cleanup failed: " + Log.Describe(e)); }
                    if (Directory.Exists(dir)) await Task.Delay(1000);
                }
            old.Dispose();
        });
    }

    /// <summary>При запуске: остатки аварийно завершённого процесса.</summary>
    public static void SweepStale()
    {
        try { if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true); }
        catch (Exception e) { Log.Write("share staging sweep failed: " + Log.Describe(e)); }
    }
}
