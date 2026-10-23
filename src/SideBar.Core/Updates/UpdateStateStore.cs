using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideBar.Core.Updates;

/// <summary>
/// Время последней проверки обновлений (update-state.json). Любой сбой чтения/записи молчаливый: нет файла или мусор — «ещё не проверяли».
/// TODO(merge M5): перенести в AppState.LastUpdateCheck, когда AppState окажется в main.
/// </summary>
public sealed class UpdateStateStore(string path)
{
    public DateTimeOffset? LastCheck
    {
        get
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 4096) return null;
                return JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdateStateJsonContext.Default.UpdateStateDto)?.LastCheck;
            }
            catch (Exception) { return null; }
        }
    }

    public void SaveLastCheck(DateTimeOffset when)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(new UpdateStateDto { LastCheck = when }, UpdateStateJsonContext.Default.UpdateStateDto));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception) { }
    }
}

internal sealed class UpdateStateDto
{
    [JsonPropertyName("lastCheck")] public DateTimeOffset? LastCheck { get; set; }
}

[JsonSerializable(typeof(UpdateStateDto))]
internal sealed partial class UpdateStateJsonContext : JsonSerializerContext;
