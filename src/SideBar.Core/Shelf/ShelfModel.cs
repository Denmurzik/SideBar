namespace SideBar.Core.Shelf;

/// <summary>Стопка: порядок добавления, последний элемент — сверху. Не потокобезопасна (только UI-поток).</summary>
public sealed class ShelfModel
{
    public const int MaxItems = 100;

    private readonly List<ShelfItem> _items = [];

    public IReadOnlyList<ShelfItem> Items => _items;

    public event Action? Changed;

    public ShelfModel() { }

    public ShelfModel(IEnumerable<ShelfItem> initial) => _items.AddRange(initial.Take(MaxItems));

    public ShelfItem? Add(string path, ShelfItemKind kind, string? preferredName = null)
    {
        if (_items.Count >= MaxItems) return null;
        var name = UniqueName(string.IsNullOrWhiteSpace(preferredName) ? System.IO.Path.GetFileName(path) : preferredName,
            _items.Select(i => i.DisplayName));
        var item = new ShelfItem(Guid.NewGuid(), name, path, kind, DateTimeOffset.Now);
        _items.Add(item);
        Changed?.Invoke();
        return item;
    }

    public bool Remove(Guid id)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return false;
        _items.RemoveAt(i);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Убирает элементы, чей файл исчез. Возвращает число убранных.</summary>
    public int PruneMissing(Func<string, bool> exists)
    {
        var n = _items.RemoveAll(i => !exists(i.Path));
        if (n > 0) Changed?.Invoke();
        return n;
    }

    /// <summary>«a.txt» → «a (2).txt» → «a (3).txt»; сравнение без учёта регистра; точка в начале имени — не расширение.</summary>
    public static string UniqueName(string name, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name)) return name;
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!set.Contains(candidate)) return candidate;
        }
    }
}
