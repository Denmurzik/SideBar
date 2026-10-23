using System.Globalization;
using System.Text;

namespace SideBar.Core.Nearby;

/// <summary>Pure helpers for showing peer-controlled text (device names, shared text, links) safely.</summary>
public static class DisplayText
{
    public const int MaxNameLength = 40;
    public const int MaxPreviewLength = 100;

    static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".js", ".lnk", ".url", ".hta", ".cpl", ".reg", ".jar",
    };

    /// <summary>One line without control/format characters (bidi overrides included), whitespace collapsed, capped at <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    static string Clean(string? value, int max, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder();
        var space = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { space = sb.Length > 0; continue; }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse)
                continue;
            var needed = rune.Utf16SequenceLength + (space ? 1 : 0);
            if (sb.Length + needed > max) { truncated = true; break; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    public static string SanitizeName(string? name, string fallback)
    {
        var clean = Clean(name, MaxNameLength, out _);
        return clean.Length == 0 ? fallback : clean;
    }

    /// <summary>First 100 characters on a single line; "…" is appended when the text was longer.</summary>
    public static string Preview(string? value)
    {
        var clean = Clean(value, MaxPreviewLength, out var truncated);
        return truncated ? clean + "…" : clean;
    }

    /// <summary>Only absolute http/https links with a host may be opened from a notification.</summary>
    public static bool TryHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Length > 0)
        {
            uri = u;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>Extensions that run code or follow a shortcut when opened: such files are shown in the folder, not launched.</summary>
    public static bool IsExecutableLike(string path) => ExecutableExtensions.Contains(Path.GetExtension(path));
}
