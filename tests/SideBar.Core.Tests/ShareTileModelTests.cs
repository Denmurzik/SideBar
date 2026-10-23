using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class ShareTileModelTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    static readonly string[] Files = ["a.txt"];

    static DeviceView Dev(int n, ShareDeviceKind kind = ShareDeviceKind.Phone) => new($"id{n}", $"Device {n}", kind);

    static ShareTileModel Searching()
    {
        var m = new ShareTileModel();
        m.BeginShare(Files, T0);
        return m;
    }

    static ShareTileModel Sending()
    {
        var m = Searching();
        m.DeviceFound(Dev(1));
        m.Choose("id1");
        return m;
    }

    [Fact]
    public void Starts_idle()
    {
        var m = new ShareTileModel();
        Assert.Equal(ShareTileState.Idle, m.State);
        Assert.False(m.KeepsCardOpen);
    }

    [Fact]
    public void Drop_starts_searching()
    {
        var m = Searching();
        Assert.Equal(ShareTileState.Searching, m.State);
        Assert.Equal(Files, m.Paths);
        Assert.Empty(m.Devices);
    }

    [Fact]
    public void BeginShare_with_no_paths_is_ignored()
    {
        var m = new ShareTileModel();
        m.BeginShare([], T0);
        Assert.Equal(ShareTileState.Idle, m.State);
    }

    [Fact]
    public void First_device_moves_to_choosing()
    {
        var m = Searching();
        m.DeviceFound(Dev(1));
        Assert.Equal(ShareTileState.Choosing, m.State);
        Assert.Single(m.Devices);
    }

    [Fact]
    public void Found_devices_capped_at_4()
    {
        var m = Searching();
        for (int i = 1; i <= 6; i++) m.DeviceFound(Dev(i));
        Assert.Equal(4, m.Devices.Count);
        Assert.Equal(["id1", "id2", "id3", "id4"], m.Devices.Select(d => d.Id));
    }

    [Fact]
    public void Lost_device_frees_a_slot_for_a_hidden_one()
    {
        var m = Searching();
        for (int i = 1; i <= 5; i++) m.DeviceFound(Dev(i));
        m.DeviceLost("id2");
        Assert.Equal(["id1", "id3", "id4", "id5"], m.Devices.Select(d => d.Id));
    }

    [Fact]
    public void Found_again_updates_instead_of_duplicating()
    {
        var m = Searching();
        m.DeviceFound(Dev(1));
        m.DeviceFound(new DeviceView("id1", "Renamed", ShareDeviceKind.Tablet));
        Assert.Single(m.Devices);
        Assert.Equal("Renamed", m.Devices[0].Name);
    }

    [Fact]
    public void Losing_the_last_device_returns_to_searching()
    {
        var m = Searching();
        m.DeviceFound(Dev(1));
        m.DeviceLost("id1");
        Assert.Equal(ShareTileState.Searching, m.State);
    }

    [Fact]
    public void Choose_starts_sending()
    {
        var m = Sending();
        Assert.Equal(ShareTileState.Sending, m.State);
        Assert.Equal("id1", m.ChosenId);
        Assert.Equal(0, m.Fraction);
    }

    [Fact]
    public void Choose_unknown_device_is_ignored()
    {
        var m = Searching();
        m.DeviceFound(Dev(1));
        m.Choose("nope");
        Assert.Equal(ShareTileState.Choosing, m.State);
    }

    [Fact]
    public void Progress_is_clamped_and_only_while_sending()
    {
        var m = Searching();
        m.Progress(0.5);
        Assert.Equal(0, m.Fraction);
        m = Sending();
        m.Progress(0.4);
        Assert.Equal(0.4, m.Fraction);
        m.Progress(7);
        Assert.Equal(1, m.Fraction);
        m.Progress(-1);
        Assert.Equal(0, m.Fraction);
    }

    [Fact]
    public void No_device_for_60s_fails_with_hint()
    {
        var m = Searching();
        m.Tick(T0.AddSeconds(59.9));
        Assert.Equal(ShareTileState.Searching, m.State);
        m.Tick(T0.AddSeconds(60));
        Assert.Equal(ShareTileState.Failed, m.State);
        Assert.Equal("Устройства не найдены — откройте Quick Share на телефоне", m.FailReason);
    }

    [Fact]
    public void No_choice_for_60s_while_choosing_also_fails()
    {
        var m = Searching();
        m.DeviceFound(Dev(1));
        m.Tick(T0.AddSeconds(60));
        Assert.Equal(ShareTileState.Failed, m.State);
    }

    [Fact]
    public void Sending_is_not_subject_to_the_60s_timeout()
    {
        var m = Sending();
        m.Tick(T0.AddMinutes(10));
        Assert.Equal(ShareTileState.Sending, m.State);
    }

    [Fact]
    public void Done_returns_to_idle_after_1_5s()
    {
        var m = Sending();
        m.Tick(T0.AddSeconds(10));
        m.Finished(true, null);
        Assert.Equal(ShareTileState.Done, m.State);
        m.Tick(T0.AddSeconds(11.4));
        Assert.Equal(ShareTileState.Done, m.State);
        m.Tick(T0.AddSeconds(11.5));
        Assert.Equal(ShareTileState.Idle, m.State);
        Assert.Empty(m.Devices);
    }

    [Fact]
    public void Failed_keeps_reason_then_returns_to_idle()
    {
        var m = Sending();
        m.Tick(T0.AddSeconds(5));
        m.Finished(false, "Отклонено");
        Assert.Equal(ShareTileState.Failed, m.State);
        Assert.Equal("Отклонено", m.FailReason);
        m.Tick(T0.AddSeconds(6.5));
        Assert.Equal(ShareTileState.Idle, m.State);
    }

    [Fact]
    public void Finished_outside_sending_is_ignored()
    {
        var m = Searching();
        m.Finished(true, null);
        Assert.Equal(ShareTileState.Searching, m.State);
    }

    [Fact]
    public void Failure_before_choosing_shows_failed()
    {
        var m = Searching();
        m.Finished(false, "Нечего отправлять");
        Assert.Equal(ShareTileState.Failed, m.State);
        Assert.Equal("Нечего отправлять", m.FailReason);
    }

    [Theory]
    [InlineData(ShareTileState.Searching)]
    [InlineData(ShareTileState.Choosing)]
    [InlineData(ShareTileState.Sending)]
    [InlineData(ShareTileState.Done)]
    [InlineData(ShareTileState.Failed)]
    public void Cancel_from_any_state_returns_idle(ShareTileState from)
    {
        var m = Searching();
        if (from >= ShareTileState.Choosing) m.DeviceFound(Dev(1));
        if (from >= ShareTileState.Sending) m.Choose("id1");
        if (from == ShareTileState.Done) m.Finished(true, null);
        if (from == ShareTileState.Failed) m.Finished(false, "x");
        Assert.Equal(from, m.State);
        m.Cancel();
        Assert.Equal(ShareTileState.Idle, m.State);
        Assert.Empty(m.Devices);
        Assert.Empty(m.Paths);
    }

    [Fact]
    public void KeepsCardOpen_true_while_searching_choosing_sending()
    {
        var m = new ShareTileModel();
        Assert.False(m.KeepsCardOpen);
        m.BeginShare(Files, T0);
        Assert.True(m.KeepsCardOpen);
        m.DeviceFound(Dev(1));
        Assert.True(m.KeepsCardOpen);
        m.Choose("id1");
        Assert.True(m.KeepsCardOpen);
        m.Finished(true, null);
        Assert.False(m.KeepsCardOpen);
    }

    [Fact]
    public void BeginShare_while_sending_is_ignored()
    {
        var m = Sending();
        m.BeginShare(["other.txt"], T0.AddSeconds(1));
        Assert.Equal(ShareTileState.Sending, m.State);
        Assert.Equal(Files, m.Paths);
    }

    [Fact]
    public void Changed_is_raised_on_state_and_content_changes()
    {
        var m = new ShareTileModel();
        int n = 0;
        m.Changed += () => n++;
        m.BeginShare(Files, T0);
        m.DeviceFound(Dev(1));
        m.Choose("id1");
        m.Progress(0.5);
        m.Finished(true, null);
        Assert.Equal(5, n);
    }
}
