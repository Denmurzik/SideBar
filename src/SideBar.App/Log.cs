using System.Diagnostics;

namespace SideBar;

/// <summary>Минимальный лог в %TEMP%\SideBar\sidebar.log (Debug и Release). Не бросает исключений.</summary>
static class Log
{
    static readonly object Gate = new();
    // SIDEBAR_DATA_DIR (автотест) уводит и лог в свой каталог, чтобы не трогать лог настоящего экземпляра
    static readonly string PathName = Environment.GetEnvironmentVariable("SIDEBAR_DATA_DIR") is { Length: > 0 } dataDir
        ? System.IO.Path.Combine(dataDir, "log", "sidebar.log")
        : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SideBar", "sidebar.log");
    const long MaxBytes = 1 << 20;

    public static string FilePath => PathName;

    /// <summary>Тип и HResult без Message/ToString: в них бывают пути файлов.</summary>
    public static string Describe(Exception e) => $"{e.GetType().Name} 0x{e.HResult:X8}";

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
                var info = new FileInfo(PathName);
                if (info.Exists && info.Length > MaxBytes) File.Delete(PathName);
                File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) { Debug.WriteLine("Log failed: " + e.Message); }
    }
}
