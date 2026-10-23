namespace SideBar.Core.Shelf;

/// <summary>Стопка: порядок добавления, последний элемент — сверху. Не потокобезопасна (только UI-поток).</summary>
public sealed class ShelfModel
{
    public const int MaxItems = 100;

    private readonly List<ShelfItem> _items = [];

    public IReadOnlyList<ShelfItem> Items => _items;

    public event Action? Changed;

    public ShelfModel() { }

    public ShelfModel(IEnumerable<ShelfItem> initial) => _items.AddRange(Sanitize(initial));

    public ShelfItem? Add(string path, ShelfItemKind kind, string? preferredName = null)
    {
        if (_items.Count >= MaxItems) return null;
        var name = UniqueName(string.IsNullOrWhiteSpace(preferredName) ? FallbackName(path) : preferredName,
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

    /// <summary>Снимает элементы по Id одним уведомлением Changed. Возвращает число снятых.</summary>
    public int RemoveRange(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        var n = _items.RemoveAll(i => set.Contains(i.Id));
        if (n > 0) Changed?.Invoke();
        return n;
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

    /// <summary>Имя файла из пути; для корня/хвостового слэша - последний сегмент каталога, иначе "file".</summary>
    private static string FallbackName(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (!string.IsNullOrEmpty(name)) return name;
        name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? "file" : name;
    }

    /// <summary>
    /// Чистит данные с диска: отбрасывает null, пустой Id/Path/DisplayName, неопределённый Kind и повторы Id (первый остаётся),
    /// делает DisplayName уникальными и оставляет НОВЕЙШИЕ <see cref="MaxItems"/> с сохранением порядка.
    /// </summary>
    public static List<ShelfItem> Sanitize(IEnumerable<ShelfItem?> items)
    {
        var ids = new HashSet<Guid>();
        var result = new List<ShelfItem>();
        foreach (var i in items)
        {
            if (i is null || i.Id == Guid.Empty || string.IsNullOrEmpty(i.Path) || string.IsNullOrEmpty(i.DisplayName)
                || !Enum.IsDefined(i.Kind) || !ids.Add(i.Id)) continue;
            var name = UniqueName(i.DisplayName, result.Select(r => r.DisplayName));
            result.Add(name == i.DisplayName ? i : i with { DisplayName = name });
        }
        if (result.Count > MaxItems) result.RemoveRange(0, result.Count - MaxItems);
        return result;
    }
}
