using System.Text.Json.Serialization;

namespace SideBar.Core.Shelf;

/// <summary>Формат shelf.json: объект с items и pendingDeletes (старый формат — голый массив элементов — читается отдельно).</summary>
internal sealed class ShelfFile
{
    [JsonPropertyName("items")] public List<ShelfItem>? Items { get; set; }
    [JsonPropertyName("pendingDeletes")] public List<PendingDeleteDto>? PendingDeletes { get; set; }
}

internal sealed class PendingDeleteDto
{
    [JsonPropertyName("guid")] public Guid Guid { get; set; }
    [JsonPropertyName("deleteAfterUtc")] public DateTimeOffset DeleteAfterUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ShelfItem>))]
[JsonSerializable(typeof(ShelfFile))]
internal sealed partial class ShelfJsonContext : JsonSerializerContext;
