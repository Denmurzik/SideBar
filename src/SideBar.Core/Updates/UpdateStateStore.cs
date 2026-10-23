using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideBar.Core.Updates;

/// <summary>Сохранённое состояние проверки: время последней проверки и найденный релиз (тег + адрес; при чтении проходит ту же проверку, что и ответ сети).</summary>
public sealed record UpdateState(DateTimeOffset? LastCheck, string? Tag, string? HtmlUrl);

/// <summary>
/// update-state.json. Любой сбой чтения/записи молчаливый: нет файла или мусор — «ещё не проверяли».
/// TODO(merge M5): перенести в AppState (LastUpdateCheck), когда AppState окажется в main.
/// </summary>
public sealed class UpdateStateStore(string path)
{
    public UpdateState Load()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4096) return new UpdateState(null, null, null);
            var dto = JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdateStateJsonContext.Default.UpdateStateDto);
            return dto is null ? new UpdateState(null, null, null) : new UpdateState(dto.LastCheck, dto.Tag, dto.HtmlUrl);
        }
        catch (Exception) { return new UpdateState(null, null, null); }
    }

    public DateTimeOffset? LastCheck => Load().LastCheck;

    public void Save(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(
                new UpdateStateDto { LastCheck = state.LastCheck, Tag = state.Tag, HtmlUrl = state.HtmlUrl }, UpdateStateJsonContext.Default.UpdateStateDto));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception) { }
    }
}

internal sealed class UpdateStateDto
{
    [JsonPropertyName("lastCheck")] public DateTimeOffset? LastCheck { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("htmlUrl")] public string? HtmlUrl { get; set; }
}

[JsonSerializable(typeof(UpdateStateDto))]
internal sealed partial class UpdateStateJsonContext : JsonSerializerContext;
