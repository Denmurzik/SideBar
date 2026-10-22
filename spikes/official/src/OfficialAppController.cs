using System.Diagnostics;
using System.Text;

namespace SideBar.Official;

public enum ShowMode { Normal, MinimizedNoActivate, Minimized, Hidden }

public sealed record WindowInfo(string ClassName, bool Visible, bool Iconic, bool HasTitle);

// Controls the official Quick Share (nearby_share.exe): detect, launch, wait for certificate publish, close softly.
public sealed class OfficialAppController
{
    public const string ProcessName = "nearby_share";
    public const string PublishMarker = "PublishDevice succeeded";
    public const string ExitMarker = "Exiting Nearby Share for Windows";

    public string ExePath { get; init; } = @"C:\Program Files\Google\NearbyShare\nearby_share.exe";
    public string LogDir { get; init; } = Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "Google", "Nearby", "Sharing", "Logs");

    public static Process[] FindProcesses() => Process.GetProcessesByName(ProcessName);
    public static bool IsRunning() => FindProcesses().Length > 0;

    // Starts the exe with the given initial show state; extra args (e.g. file paths) go to the command line.
    public int Launch(ShowMode mode, string? args = null)
    {
        var si = new Native.STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.STARTUPINFO>() };
        if (mode != ShowMode.Normal)
        {
            si.dwFlags = Native.STARTF_USESHOWWINDOW;
            si.wShowWindow = mode switch
            {
                ShowMode.MinimizedNoActivate => Native.SW_SHOWMINNOACTIVE,
                ShowMode.Minimized => Native.SW_SHOWMINIMIZED,
                _ => Native.SW_HIDE,
            };
        }
        var cmd = new StringBuilder($"\"{ExePath}\"" + (args is null ? "" : " " + args));
        if (!Native.CreateProcessW(null, cmd, 0, 0, false, 0, 0, Path.GetDirectoryName(ExePath), ref si, out var pi))
            throw new System.ComponentModel.Win32Exception();
        Native.CloseHandle(pi.hThread);
        Native.CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }

    // Snapshot of log files so that a new log created by the launch can be recognized.
    public Dictionary<string, long> SnapshotLogs() =>
        Directory.GetFiles(LogDir, "nearby_share*.log").ToDictionary(f => f, f => new FileInfo(f).Length);

    // Waits until a line containing `marker` appears in a log written after the snapshot.
    // The app starts a new timestamped log per launch; nearby_share.log is a hard link to it.
    public async Task<bool> WaitForLogAsync(string marker, Dictionary<string, long> before, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            foreach (var f in Directory.GetFiles(LogDir, "nearby_share*.log"))
            {
                long from = before.GetValueOrDefault(f);
                if (ContainsFrom(f, from, marker)) return true;
            }
            await Task.Delay(250, ct);
        }
        return false;
    }

    static bool ContainsFrom(string path, long offset, string marker)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= offset) return false;
            fs.Position = offset;
            var buf = new byte[fs.Length - offset];
            int n = fs.Read(buf, 0, buf.Length);
            return Encoding.Latin1.GetString(buf, 0, n).Contains(marker, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    public static List<(IntPtr Hwnd, WindowInfo Info)> WindowsOf(int pid)
    {
        var list = new List<(IntPtr, WindowInfo)>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint p);
            if (p == pid)
            {
                var sb = new StringBuilder(256);
                Native.GetClassNameW(h, sb, sb.Capacity);
                list.Add((h, new WindowInfo(sb.ToString(), Native.IsWindowVisible(h), Native.IsIconic(h), Native.GetWindowTextLengthW(h) > 0)));
            }
            return true;
        }, 0);
        return list;
    }

    // Ends the UI thread message loop (WM_QUIT to the thread owning the main window). WM_CLOSE only hides to tray.
    public static async Task<bool> QuitMessageLoopAsync(TimeSpan timeout)
    {
        foreach (var p in FindProcesses())
        {
            var tids = new HashSet<uint>();
            Native.EnumWindows((h, _) =>
            {
                uint tid = Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == p.Id) tids.Add(tid);
                return true;
            }, 0);
            foreach (var tid in tids) Native.PostThreadMessageW(tid, Native.WM_QUIT, 0, 0);
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsRunning()) return true;
            await Task.Delay(200);
        }
        return !IsRunning();
    }

    // Posts WM_CLOSE to all top-level windows of the process and waits for exit. Returns true if the process ended.
    // Never kills; the caller decides what to do on false.
    public static async Task<bool> CloseSoftlyAsync(TimeSpan timeout)
    {
        var procs = FindProcesses();
        foreach (var p in procs)
            foreach (var (h, _) in WindowsOf(p.Id))
                Native.PostMessageW(h, Native.WM_CLOSE, 0, 0);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!IsRunning()) return true;
            await Task.Delay(200);
        }
        return !IsRunning();
    }
}
