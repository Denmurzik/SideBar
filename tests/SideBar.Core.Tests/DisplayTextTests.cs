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
        Assert.Equal("a b", DisplayText.SanitizeName("a\u2028b", "x"));
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
    [InlineData(@"C:\d\a.jpg", true)]
    [InlineData(@"C:\d\a.HEIC", true)]
    [InlineData(@"C:\d\a.mp4", true)]
    [InlineData(@"C:\d\a.flac", true)]
    [InlineData(@"C:\d\a.pdf", true)]
    [InlineData(@"C:\d\a.docx", true)]
    [InlineData(@"C:\d\a.rtf", false)]
    [InlineData(@"C:\d\a.exe", false)]
    [InlineData(@"C:\d\a.msi", false)]
    [InlineData(@"C:\d\a.lnk", false)]
    [InlineData(@"C:\d\a.url", false)]
    [InlineData(@"C:\d\a.ps1", false)]
    [InlineData(@"C:\d\a.jar", false)]
    [InlineData(@"C:\d\a.svg", false)]
    [InlineData(@"C:\d\a.html", false)]
    [InlineData(@"C:\d\a.zip", false)]
    [InlineData(@"C:\d\a.unknownext", false)]
    [InlineData(@"C:\d\noext", false)]
    public void Only_allow_listed_extensions_are_safe_to_open(string path, bool safe)
    {
        Assert.Equal(safe, DisplayText.IsSafeToOpen(path));
    }
}
