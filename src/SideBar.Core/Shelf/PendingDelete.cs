namespace SideBar.Core.Shelf;

/// <summary>Папка Shelf\&lt;guid&gt; снятого с полки Owned-элемента, которую нельзя удалять раньше <paramref name="DeleteAfterUtc"/>.</summary>
public sealed record PendingDelete(Guid Guid, DateTimeOffset DeleteAfterUtc);

/// <summary>Правила срока отложенного удаления.</summary>
public static class PendingDeletePolicy
{
    public static readonly TimeSpan MinCap = TimeSpan.FromHours(1);

    /// <summary>
    /// Срок из файла не доверяется безгранично (сбитые часы): не позже now + max(1 ч, задержка). Прошедший срок остаётся как есть (пора проверять сейчас).
    /// </summary>
    public static DateTimeOffset Cap(DateTimeOffset saved, DateTimeOffset nowUtc, TimeSpan ownedDelay)
    {
        var limit = nowUtc + (ownedDelay > MinCap ? ownedDelay : MinCap);
        return saved < limit ? saved : limit;
    }
}
