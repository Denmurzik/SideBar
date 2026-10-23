using System.Runtime.InteropServices;
using SideBar.Core.Shelf;

namespace SideBar.Nearby.Session;

/// <summary>
/// Writes one received file safely: into <c>&lt;name&gt;.part</c> next to the target, then an atomic rename to a unique
/// name that never overwrites anything. The sender's name is untrusted, so it is reduced to a leaf and sanitized.
/// </summary>
public sealed partial class ReceivedFileWriter(string directory)
{
    /// <summary>Env override of the Downloads folder (tests, driver).</summary>
    public const string DownloadsOverrideVariable = "SIDEBAR_DOWNLOADS_DIR";

    /// <summary>Free space that must remain after the transfer.</summary>
    public const long FreeSpaceMargin = 64L * 1024 * 1024;

    private const string ZoneStream = ":Zone.Identifier";
    private const string ZoneContent = "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n";

    private FileStream? _stream;
    private string? _partPath;
    private string? _baseName;
    private int _index;
    private bool _finished;

    /// <summary>Opens <c>.part</c> for writing and reserves the final name (<paramref name="finalPathPreview"/>).</summary>
    public Stream Begin(string rawName, long size, out string finalPathPreview)
    {
        if (_stream is not null || _finished) throw new InvalidOperationException("Writer already used.");
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        Directory.CreateDirectory(directory);

        var name = SafeName(rawName);
        for (var i = 1; ; i++)
        {
            var candidate = Candidate(name, i);
            var finalPath = Path.Combine(directory, candidate);
            if (File.Exists(finalPath)) continue;
            var partPath = finalPath + ".part";
            try
            {
                // CreateNew reserves the name against concurrent writers: the other one gets the next number.
                _stream = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
            }
            catch (IOException) when (File.Exists(partPath)) { continue; }
            _partPath = partPath;
            _baseName = name;
            _index = i;
            finalPathPreview = finalPath;
            return _stream;
        }
    }

    /// <summary>Closes the stream, marks the file as downloaded from the internet and renames it into place.</summary>
    public string Commit()
    {
        if (_stream is null || _partPath is null || _baseName is null) throw new InvalidOperationException("Nothing to commit.");
        _stream.Dispose();
        _stream = null;
        try
        {
            WriteMarkOfTheWeb(_partPath);
            // The final name was free when reserved; if someone took it since, move on to the next number.
            for (var i = _index; ; i++)
            {
                var finalPath = Path.Combine(directory, Candidate(_baseName, i));
                try
                {
                    File.Move(_partPath, finalPath, overwrite: false);
                    _finished = true;
                    return finalPath;
                }
                catch (IOException) when (File.Exists(finalPath)) { }
            }
        }
        catch
        {
            Abort();
            throw;
        }
    }

    /// <summary>Drops the incomplete file. Safe to call at any time, including after <see cref="Commit"/>.</summary>
    public void Abort()
    {
        if (_finished) return;
        _finished = true;
        try { _stream?.Dispose(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        _stream = null;
        if (_partPath is null) return;
        try { File.Delete(_partPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>False when the files plus <see cref="FreeSpaceMargin"/> do not fit; unknown free space counts as enough.</summary>
    public static bool HasSpace(string directory, long totalSize, Func<string, long>? freeBytes = null)
    {
        long free;
        try { free = (freeBytes ?? DefaultFreeBytes)(directory); }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return true; }
        return totalSize <= free - FreeSpaceMargin;
    }

    public static string DownloadsFolder()
    {
        // действует и в Release (автотесты, e2e); относительные и «полуотносительные» значения игнорируются
        if (Environment.GetEnvironmentVariable(DownloadsOverrideVariable) is { Length: > 0 } overridden && Path.IsPathFullyQualified(overridden))
            return overridden;
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(in id, 0, 0, out var buffer) != 0)
        {
            Marshal.FreeCoTaskMem(buffer);
            return fallback;
        }
        try { return Marshal.PtrToStringUni(buffer) is { Length: > 0 } path ? path : fallback; }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    internal static string SafeName(string rawName)
    {
        var leaf = rawName.Replace('\\', '/');
        return FileNaming.Sanitize(leaf[(leaf.LastIndexOf('/') + 1)..]);
    }

    private static long DefaultFreeBytes(string directory) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;

    private static string Candidate(string name, int index) =>
        index == 1 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({index}){Path.GetExtension(name)}";

    private static void WriteMarkOfTheWeb(string path)
    {
        try { File.WriteAllText(path + ZoneStream, ZoneContent); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            NearbyLog.Write(new NearbyLogEvent("receive.motw-failed", Value: e.GetType().Name));
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, nint token, out nint path);
}
