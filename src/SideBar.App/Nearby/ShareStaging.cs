using SideBar.Core.Shelf;
using SideBar.Shelf;

namespace SideBar.Sharing;

/// <summary>
/// Файлы с временных путей (распаковщики 7-Zip/WinRAR, почтовые клиенты удаляют их сразу после drop) копируются в
/// <c>&lt;данные&gt;\ShareStaging\&lt;guid&gt;\</c> в момент drop и удаляются после конца отправки. UI-поток.
/// </summary>
static class ShareStaging
{
    static readonly string RootPath = Path.Combine(DataDir(), "ShareStaging");
    static readonly List<string> Dirs = [];   // UI-поток

    static string DataDir()
    {
        var dir = Environment.GetEnvironmentVariable("SIDEBAR_DATA_DIR");
        return string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBar")
            : dir;
    }

    /// <summary>Пути для отправки: временные файлы заменены копиями; если копия не удалась, остаётся исходный путь.</summary>
    public static IReadOnlyList<string> Stage(IReadOnlyList<string> paths) => Stage(paths, DropPolicy.DefaultTempRoots().Select(ShelfService.NormalizePath).ToList());

    static IReadOnlyList<string> Stage(IReadOnlyList<string> paths, IReadOnlyList<string> tempRoots)
    {
        var result = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (!DropPolicy.IsTempPath(ShelfService.NormalizePath(path), tempRoots) || !File.Exists(path)) { result.Add(path); continue; }
            try
            {
                var copy = Materializer.CopyFile(RootPath, path);
                Dirs.Add(Path.GetDirectoryName(copy)!);
                result.Add(copy);
            }
            catch (Exception e)
            {
                Log.Write("share staging failed: " + Log.Describe(e));
                result.Add(path);
            }
        }
        if (Dirs.Count > 0) Log.Write($"share staged {Dirs.Count}");
        return result;
    }

    /// <summary>Отправка закончилась: копии удаляются. Файл ещё может быть открыт сессией — несколько повторов.</summary>
    public static void Cleanup()
    {
        if (Dirs.Count == 0) return;
        var dirs = Dirs.ToArray();
        Dirs.Clear();
        _ = Task.Run(async () =>
        {
            foreach (var dir in dirs)
                for (int attempt = 0; attempt < 6 && Directory.Exists(dir); attempt++)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception e) { if (attempt == 5) Log.Write("share staging cleanup failed: " + Log.Describe(e)); }
                    if (Directory.Exists(dir)) await Task.Delay(1000);
                }
        });
    }

    /// <summary>При запуске: остатки аварийно завершённого процесса.</summary>
    public static void SweepStale()
    {
        try { if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true); }
        catch (Exception e) { Log.Write("share staging sweep failed: " + Log.Describe(e)); }
    }
}
