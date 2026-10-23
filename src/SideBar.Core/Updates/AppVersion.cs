using System.Globalization;

namespace SideBar.Core.Updates;

public static class AppVersion
{
    /// <summary>
    /// Разбирает «0.1.0», «v0.1.0», «0.1.0+abc123», «0.1.0-beta». Префикс v и суффикс +…/-… отбрасываются:
    /// пред-релизный суффикс при сравнении считается той же версией.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(text) || text.Length > 64) return false;

        var s = text.AsSpan();
        if (s[0] == 'v') s = s[1..];
        var cut = s.IndexOfAny('+', '-');
        if (cut >= 0) s = s[..cut];

        Span<Range> parts = stackalloc Range[4];
        if (s.Split(parts, '.') != 3) return false;

        Span<int> n = stackalloc int[3];
        for (var i = 0; i < 3; i++)
        {
            var p = s[parts[i]];
            if (p.Length == 0 || p.Length > 9) return false;
            foreach (var c in p) if (c is < '0' or > '9') return false;
            n[i] = int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new Version(n[0], n[1], n[2]);
        return true;
    }

    public static bool IsNewer(Version current, Version latest) => latest > current;
}
