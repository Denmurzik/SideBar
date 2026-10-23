using SideBar.Nearby.Session;

namespace SideBar.Nearby.Tests;

public sealed class ReceivedFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidebar-writer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Write(string rawName, string content = "x")
    {
        var writer = new ReceivedFileWriter(_dir);
        using (var s = writer.Begin(rawName, content.Length, out _))
            s.Write(System.Text.Encoding.UTF8.GetBytes(content));
        return writer.Commit();
    }

    [Theory]
    [InlineData("..\\..\\a.txt")]
    [InlineData("C:\\x\\a.txt")]
    [InlineData("a/b.txt")]
    [InlineData("../../../Windows/evil.exe")]
    public void Traversal_names_stay_in_directory(string raw)
    {
        var path = Write(raw);
        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("NUL", "_NUL")]
    [InlineData("a\u202Etxt.exe", "atxt.exe")]
    [InlineData("name...", "name")]
    public void Reserved_and_bidi_names_are_sanitized(string raw, string expected) =>
        Assert.Equal(expected, Path.GetFileName(Write(raw)));

    [Fact]
    public void Commit_is_atomic_and_unique()
    {
        var first = new ReceivedFileWriter(_dir);
        var second = new ReceivedFileWriter(_dir);
        using var s1 = first.Begin("a.txt", 1, out var p1);
        using var s2 = second.Begin("a.txt", 1, out var p2);
        Assert.Equal(Path.Combine(_dir, "a.txt"), p1);
        Assert.Equal(Path.Combine(_dir, "a (2).txt"), p2);
        s1.WriteByte(1);
        s2.WriteByte(2);

        Assert.False(File.Exists(p1));
        Assert.True(File.Exists(p1 + ".part"));
        Assert.Equal(p2, second.Commit());
        Assert.Equal(p1, first.Commit());
        Assert.Equal([1], File.ReadAllBytes(p1));
        Assert.Equal([2], File.ReadAllBytes(p2));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public void Existing_file_is_never_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "old");
        Assert.Equal(Path.Combine(_dir, "a (2).txt"), Write("a.txt", "new"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public void Abort_removes_part()
    {
        var writer = new ReceivedFileWriter(_dir);
        writer.Begin("a.txt", 1, out _).WriteByte(1);
        writer.Abort();
        Assert.Empty(Directory.GetFiles(_dir));
        writer.Abort(); // idempotent
    }

    [Fact]
    public void Zone_identifier_written()
    {
        var path = Write("a.txt");
        var zone = File.ReadAllText(path + ":Zone.Identifier");
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n", zone);
    }

    [Fact]
    public void Free_space_check_rejects_too_large()
    {
        const long free = 1000L * 1024 * 1024;
        Assert.True(ReceivedFileWriter.HasSpace(_dir, free - ReceivedFileWriter.FreeSpaceMargin, _ => free));
        Assert.False(ReceivedFileWriter.HasSpace(_dir, free - ReceivedFileWriter.FreeSpaceMargin + 1, _ => free));
        Assert.False(ReceivedFileWriter.HasSpace(_dir, long.MaxValue, _ => free));
    }

    [Fact]
    public void Downloads_folder_honours_override()
    {
        var old = Environment.GetEnvironmentVariable(ReceivedFileWriter.DownloadsOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(ReceivedFileWriter.DownloadsOverrideVariable, _dir);
            Assert.Equal(_dir, ReceivedFileWriter.DownloadsFolder());
            foreach (var bad in new[] { @"relative\dir", "C:dir", @"\dir" })
            {
                Environment.SetEnvironmentVariable(ReceivedFileWriter.DownloadsOverrideVariable, bad);
                Assert.NotEqual(bad, ReceivedFileWriter.DownloadsFolder());
                Assert.True(Path.IsPathFullyQualified(ReceivedFileWriter.DownloadsFolder()));
            }
            Environment.SetEnvironmentVariable(ReceivedFileWriter.DownloadsOverrideVariable, null);
            Assert.True(Path.IsPathRooted(ReceivedFileWriter.DownloadsFolder()));
        }
        finally { Environment.SetEnvironmentVariable(ReceivedFileWriter.DownloadsOverrideVariable, old); }
    }
}
