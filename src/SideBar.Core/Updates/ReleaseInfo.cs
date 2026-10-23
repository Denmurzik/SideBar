using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideBar.Core.Updates;

public sealed record ReleaseInfo(Version Version, string HtmlUrl)
{
    public const int MaxJsonBytes = 1024 * 1024;
    private const string PathPrefix = "/Denmurzik/SideBar/releases/";

    /// <summary>Разбор ответа releases/latest. Никогда не бросает: мусор, 404-тело, нет тега, чужой url — false.</summary>
    public static bool TryParse(ReadOnlySpan<byte> json, [NotNullWhen(true)] out ReleaseInfo? info)
    {
        info = null;
        if (json.IsEmpty || json.Length > MaxJsonBytes) return false;
        try
        {
            var dto = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.ReleaseDto);
            return dto is not null && TryCreate(dto.TagName, dto.HtmlUrl, out info);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Та же проверка тега и адреса для данных не из сети (восстановление из update-state.json).</summary>
    public static bool TryCreate(string? tag, string? htmlUrl, [NotNullWhen(true)] out ReleaseInfo? info)
    {
        info = null;
        if (!AppVersion.TryParse(tag, out var v) || !TryNormalizeUrl(htmlUrl, out var url)) return false;
        info = new ReleaseInfo(v, url);
        return true;
    }

    /// <summary>Тег для сохранения: «v» + версия.</summary>
    public string Tag => "v" + Version;

    private static bool TryNormalizeUrl(string? raw, [NotNullWhen(true)] out string? url)
    {
        url = null;
        if (raw is null || raw.Length > 512) return false;
        foreach (var c in raw)
            if (c <= ' ' || c > '~' || c == '"' || c == (char)92) return false; // 92 = обратный слэш
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return false;
        if (!uri.AbsolutePath.StartsWith(PathPrefix, StringComparison.Ordinal) || uri.AbsolutePath.Length == PathPrefix.Length) return false;
        url = uri.AbsoluteUri;
        return true;
    }
}

internal sealed class ReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

[JsonSerializable(typeof(ReleaseDto))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
