using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class DisplayTextTests
{
    [Fact]
    public void Name_newlines_and_controls_become_single_spaces_or_vanish()
    {
        Assert.Equal("Pixel PIN 0000", DisplayText.SanitizeName("Pixel\n\nPIN\t0000", "Устройство"));
        Assert.Equal("AB", DisplayText.SanitizeName("A\u0000B", "x"));
    }

    [Fact]
    public void Name_strips_bidi_override_and_format_chars()
    {
        Assert.Equal("evil", DisplayText.SanitizeName("\u202Eevil\u202C\u200B", "x"));
        Assert.Equal("ab", DisplayText.SanitizeName("a\u2028b", "x") is "a b" ? "ab" : "bad");
    }

    [Fact]
    public void Name_is_capped_at_40_chars()
    {
        var result = DisplayText.SanitizeName(new string('x', 300), "x");
        Assert.Equal(40, result.Length);
    }

    [Fact]
    public void Empty_or_blank_name_uses_fallback()
    {
        Assert.Equal("Устройство", DisplayText.SanitizeName("", "Устройство"));
        Assert.Equal("Устройство", DisplayText.SanitizeName(null, "Устройство"));
        Assert.Equal("Устройство", DisplayText.SanitizeName("\u202E \n\u200B", "Устройство"));
    }

    [Fact]
    public void Cut_never_splits_a_surrogate_pair()
    {
        var name = new string('a', 39) + "😀" + "tail";   // pair starts at index 39, would straddle the cap
        var result = DisplayText.SanitizeName(name, "x");
        Assert.Equal(new string('a', 39), result);
        Assert.All(result, c => Assert.False(char.IsSurrogate(c)));
    }

    [Fact]
    public void Preview_is_one_line_100_chars_with_ellipsis()
    {
        Assert.Equal("a b c", DisplayText.Preview("a\r\nb\t c"));
        var p = DisplayText.Preview(new string('x', 150));
        Assert.Equal(101, p.Length);
        Assert.EndsWith("…", p);
        Assert.Equal(new string('y', 100), DisplayText.Preview(new string('y', 100)));
    }

    [Fact]
    public void Preview_strips_bidi_and_does_not_split_surrogates()
    {
        Assert.Equal("abc", DisplayText.Preview("\u202Eabc"));
        var p = DisplayText.Preview(new string('a', 99) + "😀😀");
        Assert.All(p, c => Assert.False(char.IsSurrogate(c)));
        Assert.EndsWith("…", p);
    }

    [Theory]
    [InlineData("http://example.com/a", true)]
    [InlineData("https://example.com", true)]
    [InlineData(" https://example.com ", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/system32/calc.exe", false)]
    [InlineData("ms-settings:network", false)]
    [InlineData("http://", false)]
    [InlineData("", false)]
    [InlineData("example.com", false)]
    public void TryHttpUri_accepts_only_http_and_https_with_host(string value, bool ok)
    {
        Assert.Equal(ok, DisplayText.TryHttpUri(value, out _));
    }

    [Theory]
    [InlineData(@"C:\d\a.exe", true)]
    [InlineData(@"C:\d\A.MSI", true)]
    [InlineData(@"C:\d\a.lnk", true)]
    [InlineData(@"C:\d\a.url", true)]
    [InlineData(@"C:\d\a.ps1", true)]
    [InlineData(@"C:\d\a.jar", true)]
    [InlineData(@"C:\d\a.txt", false)]
    [InlineData(@"C:\d\a.pdf", false)]
    [InlineData(@"C:\d\noext", false)]
    public void Executable_like_extensions_are_flagged(string path, bool risky)
    {
        Assert.Equal(risky, DisplayText.IsExecutableLike(path));
    }
}
