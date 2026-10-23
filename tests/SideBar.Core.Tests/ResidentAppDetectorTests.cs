using SideBar.Core.Official;

namespace SideBar.Core.Tests;

public class ResidentAppDetectorTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    [Fact]
    public void Fires_once_after_threshold()
    {
        var d = new ResidentAppDetector(Ten);
        Assert.Equal(ResidentNote.None, d.Observe(true, T0, false));
        Assert.Equal(ResidentNote.None, d.Observe(true, T0.AddMinutes(9), false));
        Assert.Equal(ResidentNote.UserApp, d.Observe(true, T0.AddMinutes(10), false));
        Assert.Equal(ResidentNote.None, d.Observe(true, T0.AddMinutes(30), false));
    }

    [Fact]
    public void User_app_counts_as_resident_only_after_threshold_and_not_for_our_leftover()
    {
        var d = new ResidentAppDetector(Ten);
        d.Observe(true, T0, false);
        Assert.False(d.UserAppResident);
        d.Observe(true, T0.AddMinutes(9), false);
        Assert.False(d.UserAppResident);
        d.Observe(true, T0.AddMinutes(10), false);
        Assert.True(d.UserAppResident);
        d.Observe(true, T0.AddMinutes(60), false);
        Assert.True(d.UserAppResident);   // остаётся истиной, хотя заметка уже показана
        d.Observe(false, T0.AddMinutes(61), false);
        Assert.False(d.UserAppResident);

        var ours = new ResidentAppDetector(Ten);
        ours.Observe(true, T0, false, ourLeftover: true);
        ours.Observe(true, T0.AddMinutes(30), false, ourLeftover: true);
        Assert.False(ours.UserAppResident);

        var cycle = new ResidentAppDetector(Ten);
        cycle.Observe(true, T0, false);
        cycle.Observe(true, T0.AddMinutes(20), true);   // идёт наш цикл: отсчёт сброшен
        Assert.False(cycle.UserAppResident);
    }

    [Fact]
    public void Our_leftover_process_gets_its_own_note()
    {
        var d = new ResidentAppDetector(Ten);
        d.Observe(true, T0, false, ourLeftover: true);
        Assert.Equal(ResidentNote.OurLeftover, d.Observe(true, T0.AddMinutes(10), false, ourLeftover: true));
        Assert.Equal(ResidentNote.None, d.Observe(true, T0.AddMinutes(20), false, ourLeftover: true));
    }

    [Fact]
    public void Restart_of_official_app_allows_next_note()
    {
        var d = new ResidentAppDetector(Ten);
        d.Observe(true, T0, false);
        Assert.Equal(ResidentNote.UserApp, d.Observe(true, T0.AddMinutes(11), false));
        Assert.Equal(ResidentNote.None, d.Observe(false, T0.AddMinutes(12), false));
        d.Observe(true, T0.AddMinutes(13), false);
        Assert.Equal(ResidentNote.UserApp, d.Observe(true, T0.AddMinutes(24), false));
    }

    [Fact]
    public void Own_cycle_and_short_runs_do_not_count()
    {
        var d = new ResidentAppDetector(Ten);
        for (int m = 0; m < 30; m++) Assert.Equal(ResidentNote.None, d.Observe(true, T0.AddMinutes(m), ownCycle: true));
        d.Observe(false, T0.AddMinutes(31), false);
        d.Observe(true, T0.AddMinutes(32), false);
        Assert.Equal(ResidentNote.None, d.Observe(true, T0.AddMinutes(41), false));
    }
}

public class RefreshNotesTests
{
    [Theory]
    [InlineData(RefreshOutcome.Succeeded, RefreshNote.Done)]
    [InlineData(RefreshOutcome.NotInstalled, RefreshNote.NotInstalled)]
    [InlineData(RefreshOutcome.OfficialAlreadyRunning, RefreshNote.OfficialOpen)]
    [InlineData(RefreshOutcome.CycleInProgress, RefreshNote.None)]
    [InlineData(RefreshOutcome.MarkerTimeout, RefreshNote.Deferred)]
    [InlineData(RefreshOutcome.DidNotExit, RefreshNote.Deferred)]
    [InlineData(RefreshOutcome.UserTookOver, RefreshNote.Deferred)]
    public void Outcome_maps_to_note(RefreshOutcome outcome, RefreshNote note) => Assert.Equal(note, RefreshNotes.For(outcome));

    [Fact]
    public void Every_outcome_has_a_theory_row()
    {
        // новый исход без строки выше не должен молча стать «Обновление отложено»
        Assert.Equal(7, Enum.GetValues<RefreshOutcome>().Length);
    }
}
