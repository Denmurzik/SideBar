using SideBar.Core.Shelf;

namespace SideBar.Core.Tests;

public sealed class ShelfStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SideBar.Tests", Guid.NewGuid().ToString("N"));

    public ShelfStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ShelfItem Item(string name, ShelfItemKind kind = ShelfItemKind.Reference) =>
        new(Guid.NewGuid(), name, @"C:\x\" + name, kind, new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(3)));

    [Fact]
    public void Store_round_trips_items()
    {
        var store = new ShelfStore(Path.Combine(_dir, "shelf.json"));
        ShelfItem[] items = [Item("a.txt"), Item("Файл б.png", ShelfItemKind.Owned)];
        store.Save(items);
        Assert.Equal(items, store.Load());
        Assert.False(File.Exists(Path.Combine(_dir, "shelf.json.tmp")));
    }

    [Fact]
    public void Store_load_of_missing_file_returns_empty()
    {
        Assert.Empty(new ShelfStore(Path.Combine(_dir, "none.json")).Load());
    }

    [Fact]
    public void Store_load_of_corrupt_file_returns_empty()
    {
        var file = Path.Combine(_dir, "shelf.json");
        File.WriteAllText(file, "{ not json [[[");
        Assert.Empty(new ShelfStore(file).Load());
    }

    [Fact]
    public void TryLoad_reports_failure_for_garbage_and_success_for_missing()
    {
        var missing = new ShelfStore(Path.Combine(_dir, "none.json"));
        Assert.True(missing.TryLoad(out var none));
        Assert.Empty(none);

        var garbage = Path.Combine(_dir, "garbage.json");
        File.WriteAllText(garbage, "{ not json [[[");
        Assert.False(new ShelfStore(garbage).TryLoad(out var items));
        Assert.Empty(items);

        var nullJson = Path.Combine(_dir, "null.json");
        File.WriteAllText(nullJson, "null");
        Assert.False(new ShelfStore(nullJson).TryLoad(out _));

        var ok = new ShelfStore(Path.Combine(_dir, "ok.json"));
        ShelfItem[] saved = [Item("a.txt")];
        ok.Save(saved);
        Assert.True(ok.TryLoad(out var loaded));
        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void Store_save_is_atomic()
    {
        var file = Path.Combine(_dir, "shelf.json");
        var store = new ShelfStore(file);
        ShelfItem[] first = [Item("a.txt")];
        store.Save(first);

        // Крах посреди записи: остался обрезанный .tmp, основной файл цел.
        File.WriteAllText(file + ".tmp", "[{\"Id\":\"trunc");
        Assert.Equal(first, store.Load());

        // Следующее сохранение перезаписывает мусорный .tmp и заменяет файл целиком.
        ShelfItem[] second = [Item("b.txt")];
        store.Save(second);
        Assert.Equal(second, store.Load());
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void Load_sanitizes_corrupt_entries()
    {
        var file = Path.Combine(_dir, "shelf.json");
        const string id = "11111111-1111-1111-1111-111111111111";
        var valid = $$"""{"Id":"{{id}}","DisplayName":"a.txt","Path":"C:/a.txt","Kind":0,"AddedAt":"2026-10-23T12:00:00+03:00"}""";
        var bad = valid.Replace("\"Kind\":0", "\"Kind\":7").Replace(id, "22222222-2222-2222-2222-222222222222");
        File.WriteAllText(file, $"[null,{{}},{valid},{valid},{bad}]");
        var items = new ShelfStore(file).Load();
        Assert.Equal([Guid.Parse(id)], items.Select(i => i.Id));
    }
}
