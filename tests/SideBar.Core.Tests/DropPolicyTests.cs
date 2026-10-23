using SideBar.Core.Shelf;

namespace SideBar.Core.Tests;

public class DropPolicyTests
{
    private static readonly string[] Roots = [@"C:\Users\u\AppData\Local\Temp", @"C:\Users\u\AppData\Local\Microsoft\Windows\INetCache"];
    private const string Doc = @"C:\Users\u\Docs\a.txt";
    private const string Tmp = @"C:\Users\u\AppData\Local\Temp\chrome\x.png";
    private static readonly VirtualFileInfo Virt = new("x.png", 10);

    private static DropPayload P(string[]? paths = null, VirtualFileInfo[]? virt = null, string? url = null, string? img = null, bool td = false) =>
        new(paths ?? [], virt ?? [], url, img, td);

    private static DropDecision Decide(DropPayload p, params string[] existing) =>
        DropPolicy.Decide(p, existing.Contains, Roots);

    [Fact]
    public void Explorer_paths_win_over_virtual()
    {
        var d = Decide(P([Doc], [Virt]), Doc);
        Assert.Equal(DropAction.References, d.Action);
        Assert.Equal([Doc], d.ReferencePaths);
        Assert.Empty(d.TempPathsToCopy);
    }

    [Fact]
    public void Chrome_temp_hdrop_plus_virtual_materializes_virtual()
    {
        var d = Decide(P([Tmp], [Virt]), Tmp);
        Assert.Equal(DropAction.MaterializeVirtual, d.Action);
        Assert.Empty(d.ReferencePaths);
        Assert.Empty(d.TempPathsToCopy);
    }

    [Fact]
    public void Only_temp_hdrop_copies()
    {
        var d = Decide(P([Tmp]), Tmp);
        Assert.Equal(DropAction.CopyTempPaths, d.Action);
        Assert.Equal([Tmp], d.TempPathsToCopy);
    }

    [Fact]
    public void Mixed_hdrop_splits_reference_and_temp()
    {
        var d = Decide(P([Doc, Tmp, @"C:\gone.txt"], [Virt]), Doc, Tmp);
        Assert.Equal(DropAction.References, d.Action);
        Assert.Equal([Doc], d.ReferencePaths);
        Assert.Equal([Tmp], d.TempPathsToCopy);
    }

    [Fact]
    public void Temp_comparison_is_case_insensitive_and_normalized()
    {
        var p = @"c:\users\U\appdata\local\temp\..\Temp\x.png";
        var d = Decide(P([p]), p);
        Assert.Equal(DropAction.CopyTempPaths, d.Action);
    }

    [Fact]
    public void Sibling_directory_with_temp_prefix_is_not_temp()
    {
        var p = @"C:\Users\u\AppData\Local\Temporary\x.png";
        Assert.Equal(DropAction.References, Decide(P([p]), p).Action);
    }

    [Fact]
    public void Telegram_png_without_files_materializes_image() =>
        Assert.Equal(DropAction.MaterializeImage, Decide(P(img: "png")).Action);

    [Fact]
    public void Dib_only_materializes_image() =>
        Assert.Equal(DropAction.MaterializeImage, Decide(P(img: "dib")).Action);

    [Fact]
    public void Td_forward_only_rejects() =>
        Assert.Equal(DropAction.Reject, Decide(P(td: true)).Action);

    [Fact]
    public void Nothing_rejects() =>
        Assert.Equal(DropAction.Reject, Decide(P()).Action);

    [Fact]
    public void Missing_paths_only_rejects() =>
        Assert.Equal(DropAction.Reject, Decide(P([Doc])).Action);

    [Fact]
    public void Decide_never_throws()
    {
        var d = DropPolicy.Decide(P([Doc]), _ => throw new IOException(), Roots);
        Assert.Equal(DropAction.Reject, d.Action);
        Assert.Equal(DropAction.Reject, DropPolicy.Decide(P(), Doc.Equals, null!).Action);
    }

    [Fact]
    public void Default_temp_roots_include_system_temp() =>
        Assert.Contains(Path.GetTempPath(), DropPolicy.DefaultTempRoots());

    [Fact]
    public void Temp_only_hdrop_wins_over_image() =>
        Assert.Equal(DropAction.CopyTempPaths, Decide(P([Tmp], img: "png"), Tmp).Action);

    [Fact]
    public void Existing_path_wins_over_td_forward() =>
        Assert.Equal(DropAction.References, Decide(P([Doc], td: true), Doc).Action);

    [Fact]
    public void Throwing_exists_treats_only_that_path_as_missing()
    {
        var d = DropPolicy.Decide(P([@"C:ad.txt", Doc]), p => p == Doc ? true : throw new IOException(), Roots);
        Assert.Equal(DropAction.References, d.Action);
        Assert.Equal([Doc], d.ReferencePaths);
    }
}
