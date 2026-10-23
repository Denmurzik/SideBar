namespace SideBar.Settings;

/// <summary>Проверка существования пути с таймаутом: недоступный сетевой диск не должен вешать вызывающего поток.</summary>
static class PathProbe
{
    const int TimeoutMs = 2000;

    public static bool DirectoryExists(string path) => Probe(() => Directory.Exists(path));

    /// <summary>Папка существует и в неё можно писать: создаётся и тут же удаляется временный файл .sidebar-probe-&lt;guid&gt;.</summary>
    public static bool DirectoryWritable(string path) => Probe(() =>
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            var probe = Path.Combine(path, ".sidebar-probe-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    });

    public static bool FileExists(string path) => Probe(() => File.Exists(path));

    static bool Probe(Func<bool> check)
    {
        try
        {
            var task = Task.Run(check);
            return task.Wait(TimeoutMs) && task.Result;   // таймаут = недоступно; висящая проверка доживает на пуле
        }
        catch (AggregateException) { return false; }
    }
}
