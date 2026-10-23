using SideBar.Core.Settings;

namespace SideBar.Core.Shelf;

/// <summary>Автоудаление из буфера: какие элементы старше выбранного срока. Сами удаляет ShelfService тем же путём, что корзина.</summary>
public static class ShelfRetentionPolicy
{
    public static TimeSpan? MaxAge(ShelfRetention retention) => retention switch
    {
        ShelfRetention.Day1 => TimeSpan.FromDays(1),
        ShelfRetention.Days7 => TimeSpan.FromDays(7),
        _ => null,
    };

    public static List<Guid> ExpiredIds(IEnumerable<ShelfItem> items, ShelfRetention retention, DateTimeOffset now)
    {
        if (MaxAge(retention) is not { } max) return [];
        return items.Where(i => now - i.AddedAt >= max).Select(i => i.Id).ToList();
    }
}
