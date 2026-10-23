using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class OfficialAppTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tile_shows_official_app_running_hint()
    {
        var m = new ShareTileModel();
        m.ShowOfficialAppRunning(T0);
        Assert.Equal(ShareTileState.OfficialAppRunning, m.State);
        Assert.Equal(ShareTileModel.OfficialAppHint, m.FailReason);
        Assert.True(m.HoldsCardOpen);
        Assert.False(m.KeepsCardOpen);
        m.Tick(T0 + ShareTileModel.ResultDuration - TimeSpan.FromMilliseconds(1));
        Assert.Equal(ShareTileState.OfficialAppRunning, m.State);
        m.Tick(T0 + ShareTileModel.ResultDuration);
        Assert.Equal(ShareTileState.Idle, m.State);
        Assert.Null(m.FailReason);
    }

    [Fact]
    public void Tile_hint_does_not_interrupt_sending()
    {
        var m = new ShareTileModel();
        m.BeginShare(["a.txt"], T0);
        m.DeviceFound(new DeviceView("id1", "D", ShareDeviceKind.Phone));
        m.Choose("id1");
        m.ShowOfficialAppRunning(T0);
        Assert.Equal(ShareTileState.Sending, m.State);
    }

    [Fact]
    public void Stack_pause_follows_official_app()
    {
        var p = new StackPause();
        int changes = 0;
        p.Changed += () => changes++;
        Assert.False(p.IsPaused);
        p.SetOfficialRunning(true);
        p.SetOfficialRunning(true);
        Assert.True(p.IsPaused);
        Assert.Equal(1, changes);
        p.SetOfficialRunning(false);
        Assert.False(p.IsPaused);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Resume_never_overrides_running_official_app()
    {
        var p = new StackPause();
        p.Pause();
        p.SetOfficialRunning(true);
        p.Resume();
        Assert.True(p.IsPaused);
        p.SetOfficialRunning(false);
        Assert.False(p.IsPaused);
    }

    [Fact]
    public void Manual_pause_holds_until_resume()
    {
        var p = new StackPause();
        int changes = 0;
        p.Changed += () => changes++;
        p.Pause();
        Assert.True(p.IsPaused);
        p.SetOfficialRunning(true);
        p.SetOfficialRunning(false);
        Assert.True(p.IsPaused);
        p.Resume();
        Assert.False(p.IsPaused);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Watcher_raises_RunningChanged_only_on_change()
    {
        bool alive = false;
        using var w = new OfficialAppWatcher(() => alive);
        var seen = new List<bool>();
        w.RunningChanged += seen.Add;
        w.Poll();
        Assert.Empty(seen);
        alive = true;
        w.Poll();
        w.Poll();
        alive = false;
        w.Poll();
        Assert.Equal([true, false], seen);
        Assert.False(w.Running);
    }

    [Fact]
    public void Watcher_keeps_state_when_probe_throws()
    {
        bool fail = false;
        using var w = new OfficialAppWatcher(() => fail ? throw new InvalidOperationException() : true);
        w.Poll();
        fail = true;
        w.Poll();
        Assert.True(w.Running);
    }

    [Fact]
    public async Task Overlapping_polls_deliver_in_order()
    {
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        int calls = 0;
        using var w = new OfficialAppWatcher(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.Set(); gate.Wait(); return true; }
            return false;
        });
        var seen = new List<bool>();
        w.RunningChanged += v => { lock (seen) seen.Add(v); };
        var a = Task.Run(w.Poll);
        entered.Wait();
        var b = Task.Run(w.Poll);
        await Task.Delay(100);
        gate.Set();
        await Task.WhenAll(a, b);
        Assert.Equal([true, false], seen);
        Assert.False(w.Running);
    }
}
