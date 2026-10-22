using SideBar.Core.Shelf;

namespace SideBar.Core.Tests;

public class ShelfModelTests
{
    [Fact]
    public void Add_assigns_unique_display_names()
    {
        var m = new ShelfModel();
        var a = m.Add(@"C:\x\a.txt", ShelfItemKind.Reference);
        var b = m.Add(@"C:\y\a.txt", ShelfItemKind.Reference);
        Assert.Equal("a.txt", a!.DisplayName);
        Assert.Equal("a (2).txt", b!.DisplayName);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal([a, b], m.Items);
    }

    [Fact]
    public void Add_uses_preferred_name_and_case_insensitive_uniqueness()
    {
        var m = new ShelfModel();
        m.Add(@"C:\x\a.txt", ShelfItemKind.Reference);
        var b = m.Add(@"C:\x\q.bin", ShelfItemKind.Owned, "A.txt");
        Assert.Equal("A (2).txt", b!.DisplayName);
    }

    [Fact]
    public void Remove_by_id_only_removes_that_item()
    {
        var m = new ShelfModel();
        var a = m.Add(@"C:\a.txt", ShelfItemKind.Reference)!;
        var b = m.Add(@"C:\b.txt", ShelfItemKind.Reference)!;
        var changed = 0;
        m.Changed += () => changed++;
        Assert.True(m.Remove(a.Id));
        Assert.Equal([b], m.Items);
        Assert.Equal(1, changed);
        Assert.False(m.Remove(a.Id));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Add_beyond_100_returns_null()
    {
        var m = new ShelfModel();
        for (var i = 0; i < ShelfModel.MaxItems; i++)
            Assert.NotNull(m.Add($@"C:\f{i}.txt", ShelfItemKind.Reference));
        var changed = 0;
        m.Changed += () => changed++;
        Assert.Null(m.Add(@"C:\extra.txt", ShelfItemKind.Reference));
        Assert.Equal(ShelfModel.MaxItems, m.Items.Count);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void PruneMissing_drops_items_whose_file_is_gone()
    {
        var m = new ShelfModel();
        m.Add(@"C:\keep.txt", ShelfItemKind.Reference);
        m.Add(@"C:\gone.txt", ShelfItemKind.Reference);
        var changed = 0;
        m.Changed += () => changed++;
        var n = m.PruneMissing(p => p == @"C:\keep.txt");
        Assert.Equal(1, n);
        Assert.Equal([@"C:\keep.txt"], m.Items.Select(i => i.Path));
        Assert.Equal(1, changed);
        Assert.Equal(0, m.PruneMissing(_ => true));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void UniqueName_handles_no_extension_and_dotfiles()
    {
        Assert.Equal("README (2)", ShelfModel.UniqueName("README", ["README"]));
        Assert.Equal(".gitignore (2)", ShelfModel.UniqueName(".gitignore", [".gitignore"]));
        Assert.Equal("a.txt", ShelfModel.UniqueName("a.txt", []));
        Assert.Equal("a (3).txt", ShelfModel.UniqueName("a.txt", ["a.txt", "a (2).txt"]));
    }

    [Fact]
    public void Restore_keeps_newest_100_in_order()
    {
        var src = Enumerable.Range(0, 130)
            .Select(i => new ShelfItem(Guid.NewGuid(), $"f{i}", $@"C:\f{i}", ShelfItemKind.Reference, DateTimeOffset.Now)).ToList();
        var m = new ShelfModel(src);
        Assert.Equal(src.Skip(30), m.Items);
    }

    [Fact]
    public void Restore_deduplicates_display_names()
    {
        ShelfItem Mk(string n) => new(Guid.NewGuid(), n, @"C:\" + n, ShelfItemKind.Reference, DateTimeOffset.Now);
        var m = new ShelfModel([Mk("a.txt"), Mk("A.txt")]);
        Assert.Equal(["a.txt", "A (2).txt"], m.Items.Select(i => i.DisplayName));
    }

    [Fact]
    public void Add_root_or_trailing_slash_gets_fallback_name()
    {
        var m = new ShelfModel();
        Assert.Equal("file", m.Add(@"C:\", ShelfItemKind.Reference)!.DisplayName);
        Assert.Equal("dir", m.Add(@"C:\x\dir\", ShelfItemKind.Reference)!.DisplayName);
    }
}
