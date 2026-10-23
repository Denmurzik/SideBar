namespace SideBar.Core.Shelf;

public enum DropAction { Reject, References, CopyTempPaths, MaterializeVirtual, MaterializeImage }

public sealed record DropDecision(DropAction Action, IReadOnlyList<string> ReferencePaths, IReadOnlyList<string> TempPathsToCopy);

public static class DropPolicy
{
    private static readonly DropDecision Rejected = new(DropAction.Reject, [], []);

    /// <summary>
    /// Существующие не временные пути CF_HDROP побеждают (виртуальные файлы игнорируются, временные пути копируются);
    /// иначе виртуальные файлы, затем временные пути CF_HDROP, затем картинка (application/x-td-forward — отказ, только если ничего не подошло). Не бросает исключений.
    /// </summary>
    public static DropDecision Decide(DropPayload p, Func<string, bool> exists, IReadOnlyList<string> tempRoots)
    {
        try
        {
            var roots = tempRoots.Select(NormalizeRoot).OfType<string>().ToList();
            var refs = new List<string>();
            var temps = new List<string>();
            foreach (var path in p.Paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Exists(path, exists)) continue;
                (IsInside(path, roots) ? temps : refs).Add(path);
            }

            if (refs.Count > 0) return new DropDecision(DropAction.References, refs, temps);
            if (p.Virtual.Count > 0) return new DropDecision(DropAction.MaterializeVirtual, [], []);
            if (temps.Count > 0) return new DropDecision(DropAction.CopyTempPaths, [], temps);
            if (p.ImageFormat != null) return new DropDecision(DropAction.MaterializeImage, [], []);
            return Rejected;
        }
        catch
        {
            return Rejected;
        }
    }

    public static IReadOnlyList<string> DefaultTempRoots() =>
    [
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
        Environment.GetFolderPath(Environment.SpecialFolder.InternetCache),
    ];

    /// <summary>True when the path lies under one of the temp roots: its owner (7-Zip, WinRAR, a mail client) may delete it at any moment.</summary>
    public static bool IsTempPath(string path, IReadOnlyList<string> tempRoots)
    {
        try { return IsInside(path, tempRoots.Select(NormalizeRoot).OfType<string>().ToList()); }
        catch { return false; }
    }

    private static bool Exists(string path, Func<string, bool> exists)
    {
        try { return exists(path); }
        catch { return false; }
    }

    private static string? NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar; }
        catch { return null; }
    }

    private static bool IsInside(string path, List<string> roots)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        foreach (var r in roots)
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
