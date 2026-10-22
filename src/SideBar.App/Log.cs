using System.Diagnostics;

namespace SideBar;

/// <summary>Минимальный лог в %TEMP%\SideBar\sidebar.log (Debug и Release). Не бросает исключений.</summary>
static class Log
{
    static readonly object Gate = new();
    static readonly string PathName = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SideBar", "sidebar.log");
    const long MaxBytes = 1 << 20;

    public static string FilePath => PathName;

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
