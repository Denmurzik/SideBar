namespace SideBar.Core.Shelf;

/// <summary>Папка Shelf\&lt;guid&gt; снятого с полки Owned-элемента, которую нельзя удалять раньше <paramref name="DeleteAfterUtc"/>.</summary>
public sealed record PendingDelete(Guid Guid, DateTimeOffset DeleteAfterUtc);
