using SideBar.Core.Official;

namespace SideBar.Core.Tests;

public sealed class RefreshSchedulerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SideBar.Tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly TimeSpan Busy = TimeSpan.FromMinutes(1), Idle = TimeSpan.FromMinutes(5);
    private readonly RefreshScheduler _s = new();

    public RefreshSchedulerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RefreshDecision Decide(TimeSpan age, TimeSpan idle, bool installed = true, bool running = false) =>
        _s.Decide(Now, Now - age, idle, installed, running);

    [Fact]
    public void Under_20h_waits()
    {
        Assert.Equal(RefreshDecision.Wait, Decide(TimeSpan.FromHours(19.9), Idle));
        Assert.Equal(RefreshDecision.Wait, Decide(TimeSpan.Zero, Idle));
        Assert.Equal(RefreshDecision.Wait, Decide(TimeSpan.FromHours(-3), Idle));   // часы ушли назад
    }

    [Fact]
    public void Over_20h_runs_only_when_idle_5min()
    {
        Assert.Equal(RefreshDecision.RunWhenIdle, Decide(TimeSpan.FromHours(20), Busy));
        Assert.Equal(RefreshDecision.RunWhenIdle, Decide(TimeSpan.FromHours(30), TimeSpan.FromMinutes(4.9)));
        Assert.Equal(RefreshDecision.RunNow, Decide(TimeSpan.FromHours(20), Idle));
        Assert.Equal(RefreshDecision.RunNow, Decide(TimeSpan.FromHours(48), Idle));
        Assert.Equal(RefreshDecision.RunWhenIdle, Decide(TimeSpan.FromHours(48), Busy));
    }

    [Fact]
    public void Over_48h_runs_now()
    {
        Assert.Equal(RefreshDecision.RunNow, Decide(TimeSpan.FromHours(48.01), TimeSpan.Zero));
        Assert.Equal(RefreshDecision.RunNow, Decide(TimeSpan.FromDays(3), TimeSpan.Zero));   // ноутбук спал
    }

    [Fact]
    public void Never_refreshed_runs_now()
    {
        Assert.Equal(RefreshDecision.RunNow, _s.Decide(Now, null, TimeSpan.Zero, officialInstalled: true, officialRunning: false));
    }

    [Fact]
    public void Not_installed_waits_and_never_throws()
    {
        Assert.Equal(RefreshDecision.Wait, Decide(TimeSpan.FromDays(10), Idle, installed: false));
        Assert.Equal(RefreshDecision.Wait, _s.Decide(Now, null, Idle, officialInstalled: false, officialRunning: false));
        Assert.Equal(RefreshDecision.Wait, _s.Decide(Now, null, Idle, officialInstalled: false, officialRunning: true));
    }

    [Fact]
    public void Official_running_waits()
    {
        Assert.Equal(RefreshDecision.Wait, Decide(TimeSpan.FromDays(10), Idle, running: true));
        Assert.Equal(RefreshDecision.Wait, _s.Decide(Now, null, Idle, officialInstalled: true, officialRunning: true));
    }

    [Fact]
    public void Warn_after_72h_once()
    {
        Assert.False(_s.ShouldWarn(Now, Now - TimeSpan.FromHours(72), null));
        Assert.False(_s.ShouldWarn(Now, null, null));
        Assert.True(_s.ShouldWarn(Now, Now - TimeSpan.FromHours(72.01), null));
        Assert.True(_s.ShouldWarn(Now, Now - TimeSpan.FromHours(72.01)));

        var success = Now - TimeSpan.FromDays(5);
        Assert.False(_s.ShouldWarn(Now, success, lastWarned: Now - TimeSpan.FromDays(1)));   // уже предупреждали в этот период
        Assert.True(_s.ShouldWarn(Now, success, lastWarned: success - TimeSpan.FromDays(10)));   // предупреждение было до успеха
    }

    [Fact]
    public void State_round_trips_and_corrupt_is_empty()
    {
        var path = Path.Combine(_dir, "sub", "state.json");
        var store = new AppStateStore(path);
        Assert.Equal(new AppState(), store.Load());   // нет файла

        var state = new AppState { LastSuccess = Now, LastWarned = Now.AddHours(-1) };
        store.Save(state);
        Assert.Equal(state, store.Load());
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppState(), store.Load());
        File.WriteAllText(path, "null");
        Assert.Equal(new AppState(), store.Load());

        File.WriteAllText(path, """{"lastSuccess":"2026-10-23T12:00:00+03:00","future":{"x":1}}""");   // неизвестные поля
        Assert.Equal(Now, store.Load().LastSuccess);
    }
}
