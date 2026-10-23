using SideBar.Core.Official;

namespace SideBar.Core.Tests;

public sealed class OfficialLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SideBar.Tests", Guid.NewGuid().ToString("N"));
    private readonly LogTailScanner _scanner;

    public OfficialLogTests()
    {
        Directory.CreateDirectory(_dir);
        _scanner = new LogTailScanner(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Log(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Classify_marker_only()
    {
        var r = LogTailScanner.Classify("I1 main.cc:1] x PublishDevice succeeded. contact_removed: false\n");
        Assert.True(r.MarkerSeen);
        Assert.False(r.UserActivity);
    }

    [Theory]
    [InlineData("I1 ] Received WM_COPYDATA from another instance")]
    [InlineData("I1 main.cc:414] Another start, command line: C:\\x.png")]
    public void Classify_user_activity(string line) => Assert.True(LogTailScanner.Classify(line).UserActivity);

    [Fact]
    public void Own_startup_line_is_not_user_activity()
    {
        var r = LogTailScanner.Classify("I1022 14:25:30 main.cc:414] We are the only instance, command line: \n");
        Assert.False(r.UserActivity);
    }

    [Fact]
    public void Only_text_after_snapshot_counts()
    {
        File.WriteAllText(Log("nearby_share_a.log"), "PublishDevice succeeded\nReceived WM_COPYDATA\n");
        var snap = _scanner.Snapshot();
        Assert.Equal(new LogScan(false, false), _scanner.Scan(snap));
        File.AppendAllText(Log("nearby_share_a.log"), "later PublishDevice succeeded\n");
        Assert.Equal(new LogScan(true, false), _scanner.Scan(snap));
    }

    [Fact]
    public void Log_rotation_new_file_is_read_from_start()
    {
        File.WriteAllText(Log("nearby_share_a.log"), "old\n");
        var snap = _scanner.Snapshot();
        File.AppendAllText(Log("nearby_share_a.log"), "tail\n");
        File.WriteAllText(Log("nearby_share_b.log"), "noise\nPublishDevice succeeded\n");
        Assert.True(_scanner.Scan(snap).MarkerSeen);
    }

    [Fact]
    public void Other_files_are_ignored_and_missing_dir_is_empty()
    {
        var snap = _scanner.Snapshot();
        File.WriteAllText(Log("other.log"), "PublishDevice succeeded");
        Assert.Equal(new LogScan(false, false), _scanner.Scan(snap));
        var gone = new LogTailScanner(Path.Combine(_dir, "nope"));
        Assert.Empty(gone.Snapshot());
        Assert.Equal(new LogScan(false, false), gone.Scan(new Dictionary<string, long>()));
    }

    [Fact]
    public void Open_for_write_by_another_process_is_readable()
    {
        var snap = _scanner.Snapshot();
        using var w = new FileStream(Log("nearby_share_c.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
        w.Write("PublishDevice succeeded\n"u8);
        w.Flush();
        Assert.True(_scanner.Scan(snap).MarkerSeen);
    }

    [Fact]
    public void Truncated_file_does_not_throw()
    {
        File.WriteAllText(Log("nearby_share_a.log"), "long long long line\n");
        var snap = _scanner.Snapshot();
        File.WriteAllText(Log("nearby_share_a.log"), "x");
        Assert.Equal(new LogScan(false, false), _scanner.Scan(snap));
    }

    [Fact]
    public void Retry_schedule_is_now_then_2min_twice_then_stop()
    {
        Assert.Equal(TimeSpan.Zero, CloseRetryPolicy.DelayBefore(1));
        Assert.Equal(TimeSpan.FromMinutes(2), CloseRetryPolicy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromMinutes(2), CloseRetryPolicy.DelayBefore(3));
        Assert.Null(CloseRetryPolicy.DelayBefore(4));
        Assert.Equal(TimeSpan.FromSeconds(15), CloseRetryPolicy.ExitWait);
    }

    [Fact]
    public void State_clamp_and_first_seen_roundtrip()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var s = new AppState { LastSuccess = now.AddHours(5), LastWarned = now.AddHours(6), FirstSeen = now.AddHours(7) };
        var c = s.ClampedTo(now);
        Assert.Equal(now, c.LastSuccess);
        Assert.Equal(now, c.LastWarned);
        Assert.Equal(now, c.FirstSeen);
        var past = new AppState { LastSuccess = now.AddHours(-1) };
        Assert.Equal(past, past.ClampedTo(now));

        var store = new AppStateStore(Log("state.json"));
        store.Save(new AppState { FirstSeen = now });
        Assert.Equal(now, store.Load().FirstSeen);
    }
}
