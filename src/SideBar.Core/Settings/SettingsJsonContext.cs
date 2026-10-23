using System.Text.Json.Serialization;

namespace SideBar.Core.Settings;

/// <summary>Формат settings.json. Перечисления хранятся строками и разбираются терпимо: неизвестное значение = по умолчанию.</summary>
internal sealed class SettingsFile
{
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("checkForUpdates")] public bool? CheckForUpdates { get; set; }
    [JsonPropertyName("visibility")] public string? Visibility { get; set; }
    [JsonPropertyName("pcName")] public string? PcName { get; set; }
    [JsonPropertyName("receiveFolder")] public string? ReceiveFolder { get; set; }
    [JsonPropertyName("officialExePath")] public string? OfficialExePath { get; set; }
    [JsonPropertyName("edgeOpenDelay")] public string? EdgeOpenDelay { get; set; }
    [JsonPropertyName("bounceOnIncoming")] public bool? BounceOnIncoming { get; set; }
    [JsonPropertyName("showInFullscreen")] public bool? ShowInFullscreen { get; set; }
    [JsonPropertyName("shelfRetention")] public string? ShelfRetention { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
