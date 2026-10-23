using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideBar.Core.Settings;

/// <summary>Чтение settings.json: каждое поле разбирается отдельно, значение не того типа сбрасывает только это поле.</summary>
internal sealed class SettingsRaw
{
    [JsonPropertyName("language")] public JsonElement? Language { get; set; }
    [JsonPropertyName("checkForUpdates")] public JsonElement? CheckForUpdates { get; set; }
    [JsonPropertyName("visibility")] public JsonElement? Visibility { get; set; }
    [JsonPropertyName("pcName")] public JsonElement? PcName { get; set; }
    [JsonPropertyName("receiveFolder")] public JsonElement? ReceiveFolder { get; set; }
    [JsonPropertyName("officialExePath")] public JsonElement? OfficialExePath { get; set; }
    [JsonPropertyName("edgeOpenDelay")] public JsonElement? EdgeOpenDelay { get; set; }
    [JsonPropertyName("bounceOnIncoming")] public JsonElement? BounceOnIncoming { get; set; }
    [JsonPropertyName("showInFullscreen")] public JsonElement? ShowInFullscreen { get; set; }
    [JsonPropertyName("shelfRetention")] public JsonElement? ShelfRetention { get; set; }
}

/// <summary>Запись settings.json. Перечисления хранятся строками.</summary>
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
[JsonSerializable(typeof(SettingsRaw))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
