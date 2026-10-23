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

    [Fact]
    public void TryLoad_retries_while_the_file_is_locked_and_then_succeeds()
    {
        var file = Path.Combine(_dir, "shelf.json");
        var store = new ShelfStore(file);
        ShelfItem[] saved = [Item("a.txt")];
        store.Save(saved);

        // Антивирус/индексатор держит файл ~300 мс: первая попытка и несколько повторов упрутся в IOException.
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(300); held.Dispose(); });
        release.Start();
        var ok = store.TryLoad(out var loaded);
        release.Join();
        Assert.True(ok);
        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void TryLoad_gives_up_when_the_file_stays_locked()
    {
        var file = Path.Combine(_dir, "shelf.json");
        var store = new ShelfStore(file);
        store.Save([Item("a.txt")]);
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(store.TryLoad(out var items));
        Assert.Empty(items);
        Assert.InRange(sw.ElapsedMilliseconds, 700, 3000);   // 4 повтора по 200 мс
    }

    [Fact]
    public void Quarantine_moves_the_file_under_a_timestamped_name_and_never_overwrites()
    {
        var file = Path.Combine(_dir, "shelf.json");
        var store = new ShelfStore(file);
        File.WriteAllText(file, "bad one");
        store.Quarantine();
        File.WriteAllText(file, "bad two");
        store.Quarantine();   // в ту же секунду: имя занято, первая копия должна уцелеть

        var bads = Directory.GetFiles(_dir, "shelf.bad-*.json");
        Assert.False(File.Exists(file));
        Assert.Equal(2, bads.Length);
        Assert.All(bads, b => Assert.Matches(@"^shelf\.bad-\d{8}-\d{6}(-\d+)?\.json$", Path.GetFileName(b)));
        Assert.Equal(["bad one", "bad two"], bads.Select(File.ReadAllText).Order());
    }

    [Fact]
    public void Save_is_refused_until_a_locked_bad_file_has_a_backup()
    {
        var file = Path.Combine(_dir, "shelf.json");
        var store = new ShelfStore(file);
        File.WriteAllText(file, "{ not json [[[");
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            store.Quarantine();   // ни перенести, ни скопировать
            Assert.Empty(Directory.GetFiles(_dir, "shelf.bad-*.json"));
            Assert.Throws<IOException>(() => store.Save([Item("a.txt")]));
        }
        finally { held.Dispose(); }

        store.Save([Item("a.txt")]);   // блокировка снята: перед записью делается резервная копия
        var bads = Directory.GetFiles(_dir, "shelf.bad-*.json");
        Assert.Single(bads);
        Assert.Equal("{ not json [[[", File.ReadAllText(bads[0]));
        Assert.Single(store.Load());
    }

    [Fact]
    public void Save_retries_while_the_temp_file_is_briefly_locked()
    {
        var file = Path.Combine(_dir, "shelf.json");
        var store = new ShelfStore(file);
        var held = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(150); held.Dispose(); });
        release.Start();
        store.Save([Item("a.txt")]);   // первая попытка упирается в блокировку, повтор через 100 мс проходит
        release.Join();
        Assert.Single(store.Load());
    }

    [Fact]
    public void Store_round_trips_pending_deletes()
    {
        var store = new ShelfStore(Path.Combine(_dir, "shelf.json"));
        ShelfItem[] items = [Item("a.txt")];
        PendingDelete[] pending = [new(Guid.NewGuid(), new DateTimeOffset(2026, 10, 23, 12, 10, 0, TimeSpan.Zero))];
        store.Save(items, pending);
        Assert.True(store.TryLoad(out var loaded, out var loadedPending));
        Assert.Equal(items, loaded);
        Assert.Equal(pending, loadedPending);
        var json = File.ReadAllText(Path.Combine(_dir, "shelf.json"));
        Assert.Contains("\"pendingDeletes\"", json);
        Assert.Contains("\"deleteAfterUtc\"", json);
    }

    [Fact]
    public void Old_array_format_loads_with_empty_pending_deletes()
    {
        var file = Path.Combine(_dir, "shelf.json");
        const string id = "11111111-1111-1111-1111-111111111111";
        File.WriteAllText(file, $$"""[{"Id":"{{id}}","DisplayName":"a.txt","Path":"C:/a.txt","Kind":0,"AddedAt":"2026-10-23T12:00:00+03:00"}]""");
        Assert.True(new ShelfStore(file).TryLoad(out var items, out var pending));
        Assert.Equal([Guid.Parse(id)], items.Select(i => i.Id));
        Assert.Empty(pending);
    }

    [Fact]
    public void Object_format_without_pending_deletes_loads_and_garbage_pending_entries_are_dropped()
    {
        var file = Path.Combine(_dir, "shelf.json");
        File.WriteAllText(file, """{"items":[]}""");
        Assert.True(new ShelfStore(file).TryLoad(out var items, out var pending));
        Assert.Empty(items);
        Assert.Empty(pending);

        const string id = "33333333-3333-3333-3333-333333333333";
        File.WriteAllText(file, $$"""{"items":[],"pendingDeletes":[null,{},{"guid":"{{id}}","deleteAfterUtc":"2026-10-23T12:00:00Z"},{"guid":"{{id}}","deleteAfterUtc":"2026-10-23T13:00:00Z"}]}""");
        Assert.True(new ShelfStore(file).TryLoad(out _, out pending));
        Assert.Equal([Guid.Parse(id)], pending.Select(p => p.Guid));
    }

    [Fact]
    public void Corrupt_object_file_is_reported_as_unreadable()
    {
        var file = Path.Combine(_dir, "shelf.json");
        File.WriteAllText(file, """{"items": [""");
        Assert.False(new ShelfStore(file).TryLoad(out var items, out var pending));
        Assert.Empty(items);
        Assert.Empty(pending);
    }
}
