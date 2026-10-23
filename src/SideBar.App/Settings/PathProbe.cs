namespace SideBar.Settings;

/// <summary>Проверка существования пути с таймаутом: недоступный сетевой диск не должен вешать вызывающего поток.</summary>
static class PathProbe
{
    const int TimeoutMs = 2000;

    public static bool DirectoryExists(string path) => Probe(() => Directory.Exists(path));

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
