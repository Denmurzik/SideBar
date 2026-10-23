namespace SideBar.Core.Shelf;

public enum ShelfItemKind { Reference, Owned }

/// <summary>Элемент стопки. <c>Reference</c> — ссылка на чужой файл, <c>Owned</c> — файл, материализованный в Shelf\.</summary>
public sealed record ShelfItem(Guid Id, string DisplayName, string Path, ShelfItemKind Kind, DateTimeOffset AddedAt);
