using System.Text.Json.Serialization;

namespace SideBar.Core.Shelf;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ShelfItem>))]
internal sealed partial class ShelfJsonContext : JsonSerializerContext;
