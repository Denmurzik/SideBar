using System.Globalization;
using System.Text;

namespace SideBar.Core.Shelf;

public static class FileNaming
{
    private const int MaxLength = 200;
    private const string Stamp = "yyyy-MM-dd HH-mm-ss";

    private static readonly string[] GenericStems =
        ["Без имени", "Безымянный", "Untitled", "Unnamed", "Sin título", "Sans titre", "Unbenannt", "Senza titolo"];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    public static string ForVirtual(string sourceName, string? url, int virtualCount, ReadOnlySpan<byte> head, DateTime now)
    {
        var name = Sanitize(sourceName);
        var magicExt = ExtensionFromMagic(head);

        if (virtualCount == 1 && UrlLastSegment(url) is { } seg)
            name = Sanitize(seg);
        else if (IsGenericName(name))
        {
            var ext = Path.GetExtension(name);
            if (ext.Length == 0) ext = magicExt ?? "";
            name = Sanitize($"Изображение {now.ToString(Stamp, CultureInfo.InvariantCulture)}{ext}");
        }

        if (Path.GetExtension(name).Length == 0 && magicExt != null)
            name = Sanitize(name + magicExt);
        return name;
    }

    public static string ForImage(string ext, DateTime now) =>
        Sanitize($"Telegram {now.ToString(Stamp, CultureInfo.InvariantCulture)}.{ext.TrimStart('.')}");

    public static bool IsGenericName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name).Trim();
        foreach (var g in GenericStems)
            if (stem.Equals(g, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string? ExtensionFromMagic(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (head.Length >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ".png";
        if (head.Length >= 4 && head[..4].SequenceEqual("GIF8"u8)) return ".gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|') continue;
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (c is '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')) continue;
            sb.Append(c);
        }

        var s = TrimName(sb.ToString());
        if (s.Length == 0) return "file";

        if (s.Length > MaxLength)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length >= MaxLength - 4) ext = "";
            var stemLen = MaxLength - ext.Length;
            if (stemLen > 0 && char.IsHighSurrogate(s[stemLen - 1])) stemLen--;
            var stem = TrimName(s[..stemLen]);
            s = (stem.Length == 0 ? "file" : stem) + ext;
        }

        if (IsReserved(s))
        {
            // "_" не должен выталкивать имя за лимит: укорачиваем основу на один символ
            if (s.Length >= MaxLength)
            {
                var ext = Path.GetExtension(s);
                var stem = TrimName(s[..(MaxLength - 1 - ext.Length)]);
                s = (stem.Length == 0 ? "file" : stem) + ext;
            }
            s = "_" + s;
        }
        return s;
    }

    private static bool IsReserved(string s)
    {
        var dot = s.IndexOf('.');
        return Reserved.Contains((dot < 0 ? s : s[..dot]).TrimEnd());
    }

    private static string TrimName(string s) => s.Trim('.', ' ');

    private static string? UrlLastSegment(string? url)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        var seg = Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
        return seg.Length > 0 ? seg : null;
    }
}
