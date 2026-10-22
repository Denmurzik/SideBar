using SideBar.Core.Shelf;

namespace SideBar.Core.Tests;

public class FileNamingTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0);

    [Fact]
    public void Generic_name_without_http_url_becomes_Izobrazhenie() =>
        Assert.Equal("Изображение 2026-10-23 12-00-00.jpg",
            FileNaming.ForVirtual("Без имени.jpg", "data:image/jpeg;base64,AAAA", 1, default, Now));

    [Fact]
    public void Generic_name_without_extension_uses_magic() =>
        Assert.Equal("Изображение 2026-10-23 12-00-00.png",
            FileNaming.ForVirtual("Untitled", null, 1, [0x89, 0x50, 0x4E, 0x47, 0], Now));

    [Fact]
    public void Http_url_last_segment_wins_for_single_virtual() =>
        Assert.Equal("cat photo.jpg",
            FileNaming.ForVirtual("Без имени.jpg", "https://x.com/a/cat%20photo.jpg?x=1", 1, default, Now));

    [Fact]
    public void Url_is_ignored_for_multiple_virtual() =>
        Assert.Equal("b.png", FileNaming.ForVirtual("b.png", "https://x.com/a/cat.jpg", 2, default, Now));

    [Fact]
    public void Missing_extension_is_added_from_magic() =>
        Assert.Equal("pic.gif", FileNaming.ForVirtual("pic", null, 1, "GIF89a"u8, Now));

    [Theory]
    [InlineData("Без имени.jpg")]
    [InlineData("untitled")]
    [InlineData("Sin título.png")]
    [InlineData("UNBENANNT.gif")]
    public void Generic_names_detected(string n) => Assert.True(FileNaming.IsGenericName(n));

    [Fact]
    public void Ordinary_name_is_not_generic() => Assert.False(FileNaming.IsGenericName("report.pdf"));

    [Fact]
    public void Image_name_for_telegram() =>
        Assert.Equal("Telegram 2026-10-23 12-00-00.png", FileNaming.ForImage("png", Now));

    [Fact]
    public void Extension_from_magic()
    {
        Assert.Equal(".jpg", FileNaming.ExtensionFromMagic([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(".png", FileNaming.ExtensionFromMagic([0x89, 0x50, 0x4E, 0x47, 0x0D]));
        Assert.Equal(".gif", FileNaming.ExtensionFromMagic("GIF8"u8));
        Assert.Equal(".webp", FileNaming.ExtensionFromMagic("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(FileNaming.ExtensionFromMagic("abc"u8));
        Assert.Null(FileNaming.ExtensionFromMagic([]));
    }

    [Fact]
    public void Sanitize_rejects_reserved_bidi_and_dots()
    {
        Assert.Equal("_CON.txt", FileNaming.Sanitize("CON.txt"));
        Assert.Equal("_nul", FileNaming.Sanitize("nul"));
        Assert.Equal("_COM1.log", FileNaming.Sanitize("COM1.log"));
        Assert.Equal("фотоgpj.exe", FileNaming.Sanitize("фото‮gpj.exe"));
        Assert.Equal("a", FileNaming.Sanitize("a. . "));
    }

    [Fact]
    public void Sanitize_removes_invalid_chars_and_controls()
    {
        Assert.Equal("abcd.txt", FileNaming.Sanitize("a\\b/c:d*?\"<>|.txt"));
        Assert.Equal("ab", FileNaming.Sanitize("a\u0001b‏"));
    }

    [Fact]
    public void Sanitize_empty_becomes_file()
    {
        Assert.Equal("file", FileNaming.Sanitize(""));
        Assert.Equal("file", FileNaming.Sanitize(" . "));
        Assert.Equal("file", FileNaming.Sanitize("***"));
    }

    [Fact]
    public void Sanitize_truncates_keeping_extension()
    {
        var s = FileNaming.Sanitize(new string('a', 300) + ".png");
        Assert.Equal(200, s.Length);
        Assert.EndsWith(".png", s);
    }
}
