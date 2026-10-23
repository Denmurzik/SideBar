using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideBar.Core.Updates;

public sealed record ReleaseInfo(Version Version, string HtmlUrl)
{
    public const int MaxJsonBytes = 1024 * 1024;
    private const string UrlPrefix = "https://github.com/";

    /// <summary>Разбор ответа releases/latest. Никогда не бросает: мусор, 404-тело, нет тега, чужой url — false.</summary>
    public static bool TryParse(ReadOnlySpan<byte> json, out ReleaseInfo info)
    {
        info = null!;
        if (json.IsEmpty || json.Length > MaxJsonBytes) return false;
        try
        {
            var dto = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.ReleaseDto);
            if (dto is null || !AppVersion.TryParse(dto.TagName, out var v)) return false;
            var url = dto.HtmlUrl;
            if (url is null || url.Length <= UrlPrefix.Length || url.Length > 512
                || !url.StartsWith(UrlPrefix, StringComparison.Ordinal)) return false;
            info = new ReleaseInfo(v, url);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal sealed class ReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

[JsonSerializable(typeof(ReleaseDto))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
