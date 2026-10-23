using System.ComponentModel;
using System.Diagnostics;
using SideBar.Core.Official;

namespace SideBar.Official;

internal enum QuitResult
{
    /// <summary>WM_QUIT отправлен потоку окна нашего процесса.</summary>
    Posted,
    /// <summary>У процесса есть видимое окно — им пользуются, не трогаем.</summary>
    WindowsVisible,
    /// <summary>Окна <c>Google.Windows.NearbyShare</c> ещё/уже нет — слать некому.</summary>
    NoWindow,
}

/// <summary>Запущенный нами процесс: дескриптор из CreateProcess исключает путаницу с переиспользованным PID.</summary>
internal sealed class LaunchedProcess(nint handle, int pid) : IDisposable
{
    public int Pid { get; } = pid;
    public bool HasExited => Native.HasExited(handle);
    /// <summary>Хоть раз видели видимое окно процесса: им уже пользуются (защёлка, скрытие обратно не отменяет).</summary>
    public bool SawVisible { get; set; }
    public void Dispose() => Native.CloseHandle(handle);
}

/// <summary>
/// Управление официальным Quick Share (<c>nearby_share.exe</c>): наличие, запуск скрытым, мягкое закрытие нашего экземпляра.
/// Путь к exe и каталог данных переопределяются <c>SIDEBAR_OFFICIAL_EXE</c> / <c>SIDEBAR_SHARING_DIR</c> (для симулятора).
/// Процесс не убивается никогда; чужие экземпляры не трогаются.
/// </summary>
internal sealed class OfficialAppController(string exePath, string sharingDir, Action<string> log)
{
    public const string WindowClass = "Google.Windows.NearbyShare";

    public static OfficialAppController FromEnvironment(Action<string> log, string? exePathOverride = null) => new(
        Env("SIDEBAR_OFFICIAL_EXE") ?? (exePathOverride is { Length: > 0 } ? exePathOverride : DefaultExe),
        Env("SIDEBAR_SHARING_DIR") ?? DefaultSharingDir,
        log);

    /// <summary>Для приложения: путь из настроек (null — по умолчанию); переопределения окружением — только в Debug.</summary>
    public static OfficialAppController ForApp(string? exePathOverride, Action<string> log)
    {
#if DEBUG
        return FromEnvironment(log, exePathOverride);
#else
        return new(exePathOverride is { Length: > 0 } ? exePathOverride : DefaultExe, DefaultSharingDir, log);
#endif
    }

    static string DefaultExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "NearbyShare", "nearby_share.exe");
    static string DefaultSharingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Nearby", "Sharing");

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public string LogDir => Path.Combine(sharingDir, "Logs");
    public string ProcessName => Path.GetFileNameWithoutExtension(exePath);

    public bool IsInstalled => File.Exists(exePath);

    /// <summary>Любой экземпляр (в том числе пользовательский) этого приложения.</summary>
    public bool IsRunning()
    {
        var all = Process.GetProcessesByName(ProcessName);
        try { return all.Length > 0; }
        finally { foreach (var p in all) p.Dispose(); }
    }

    /// <summary>Запускает exe скрыто (<c>STARTF_USESHOWWINDOW</c> + <c>SW_HIDE</c>) без аргументов. null — не удалось (причина в лог: только код).</summary>
    public unsafe LaunchedProcess? Launch()
    {
        var si = new Native.STARTUPINFOW
        {
            cb = sizeof(Native.STARTUPINFOW),
            dwFlags = Native.STARTF_USESHOWWINDOW,
            wShowWindow = Native.SW_HIDE,
        };
        var cmd = ("\"" + exePath + "\"\0").ToCharArray();   // CreateProcessW может менять буфер командной строки
        var cwd = (Path.GetDirectoryName(exePath) ?? ".") + "\0";
        Native.PROCESS_INFORMATION pi;
        int ok;
        fixed (char* c = cmd)
        fixed (char* d = cwd)
            ok = Native.CreateProcessW(null, c, 0, 0, 0, 0, 0, d, ref si, out pi);
        if (ok == 0)
        {
            log($"official: launch failed, win32 {new Win32Exception().NativeErrorCode}");
            return null;
        }
        Native.CloseHandle(pi.hThread);
        return new LaunchedProcess(pi.hProcess, pi.dwProcessId);
    }

    /// <summary>Обновляет защёлку <see cref="LaunchedProcess.SawVisible"/>.</summary>
    public void Observe(LaunchedProcess proc)
    {
        if (proc.SawVisible || proc.HasExited) return;
        if (Native.WindowsOf(proc.Pid).FirstOrDefault(w => w.Visible) is { Visible: true } vis) Latch(proc, vis.ClassName);
    }

    // имя класса окна не секрет; по нему видно, что именно сработало (окно приложения или, например, диалог)
    void Latch(LaunchedProcess proc, string className)
    {
        proc.SawVisible = true;
        log($"official: visible window latched, class {className}");
    }

    /// <summary>
    /// Просит наш процесс завершить цикл сообщений: <c>WM_QUIT</c> только потоку окна <see cref="WindowClass"/> этого PID
    /// и только если все его окна невидимы. (WM_CLOSE у настоящего приложения лишь прячет в трей, поэтому не годится.)
    /// </summary>
    public QuitResult RequestQuit(LaunchedProcess proc)
    {
        if (proc.SawVisible) return QuitResult.WindowsVisible;
        var windows = Native.WindowsOf(proc.Pid);
        if (windows.FirstOrDefault(w => w.Visible) is { Visible: true } vis) { Latch(proc, vis.ClassName); return QuitResult.WindowsVisible; }
        var threads = windows.Where(w => w.ClassName == WindowClass).Select(w => w.ThreadId).Distinct().ToList();
        if (threads.Count == 0) return QuitResult.NoWindow;
        var posted = false;
        foreach (var t in threads) posted |= Native.PostQuitToThread(t, proc.Pid);
        return posted ? QuitResult.Posted : QuitResult.NoWindow;
    }
}
