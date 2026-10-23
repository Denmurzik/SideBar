using SideBar.Core;
using SideBar.Core.Nearby;

namespace SideBar.Core.Tests;

public class ReceiveTileModelTests
{
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, ShareTileState.Sending, true, TileContent.Consent)]
    [InlineData(true, ShareTileState.Idle, false, TileContent.Consent)]
    [InlineData(false, ShareTileState.Searching, true, TileContent.Send)]
    [InlineData(false, ShareTileState.Done, true, TileContent.Send)]
    [InlineData(false, ShareTileState.Idle, true, TileContent.Note)]
    [InlineData(false, ShareTileState.Idle, false, TileContent.Idle)]
    public void Priority_is_consent_then_send_then_note_then_idle(bool consent, ShareTileState send, bool note, TileContent expected)
        => Assert.Equal(expected, ReceiveTileModel.Priority(consent, send, note));

    [Fact]
    public void Up_to_the_stack_limit_all_consents_are_visible()
    {
        var m = new ReceiveTileModel();
        m.AddConsent("a", "1111");
        m.AddConsent("b", "2222");
        Assert.False(m.Paged);
        Assert.Equal(2, m.VisibleConsents.Count);
        Assert.Equal(0, m.HiddenCount);
    }

    [Fact]
    public void Three_consents_are_paged_and_every_one_is_reachable()
    {
        var m = new ReceiveTileModel();
        m.AddConsent("a", "1"); m.AddConsent("b", "2"); m.AddConsent("c", "3");
        Assert.True(m.Paged);
        Assert.Equal(2, m.HiddenCount);
        var seen = new List<string>();
        for (int i = 0; i < 3; i++) { seen.Add(Assert.Single(m.VisibleConsents).Line); m.MovePage(1); }
        Assert.Equal(["a", "b", "c"], seen);
        m.MovePage(-1);
        Assert.Equal("c", m.VisibleConsents[0].Line);
    }

    [Fact]
    public void Removing_a_consent_keeps_the_shown_one_and_clamps_the_page()
    {
        var m = new ReceiveTileModel();
        var a = m.AddConsent("a", "1"); m.AddConsent("b", "2"); var c = m.AddConsent("c", "3");
        m.MovePage(1); m.MovePage(1);   // on c
        m.RemoveConsent(a, T0);
        Assert.Equal(["b", "c"], m.VisibleConsents.Select(x => x.Line));   // fits again: all shown
        m.RemoveConsent(c, T0);
        Assert.Equal("b", m.VisibleConsents[0].Line);
    }

    [Fact]
    public void Card_is_held_while_a_consent_is_pending_and_one_and_a_half_seconds_after()
    {
        var m = new ReceiveTileModel();
        Assert.False(m.HoldsCardOpen(T0));
        var id = m.AddConsent("a", "1");
        Assert.True(m.HoldsCardOpen(T0.AddMinutes(1)));
        m.RemoveConsent(id, T0);
        Assert.True(m.HoldsCardOpen(T0.AddSeconds(1.4)));
        Assert.False(m.HoldsCardOpen(T0.AddSeconds(1.6)));
        Assert.True(m.NeedsTick(T0.AddSeconds(1)));
        Assert.False(m.NeedsTick(T0.AddSeconds(2)));
    }

    [Fact]
    public void Hold_ends_only_after_the_last_consent()
    {
        var m = new ReceiveTileModel();
        var a = m.AddConsent("a", "1"); var b = m.AddConsent("b", "2");
        m.RemoveConsent(a, T0);
        Assert.True(m.HoldsCardOpen(T0.AddMinutes(1)));
        m.RemoveConsent(b, T0.AddMinutes(1));
        Assert.False(m.HoldsCardOpen(T0.AddMinutes(1).AddSeconds(2)));
    }

    [Fact]
    public void Visible_note_hides_after_eight_seconds()
    {
        var m = new ReceiveTileModel();
        m.AddNote(["x"], [], T0);
        m.Tick(T0, true);
        m.Tick(T0.AddSeconds(7.9), true);
        Assert.NotNull(m.CurrentNote);
        m.Tick(T0.AddSeconds(8.1), true);
        Assert.Null(m.CurrentNote);
    }

    [Fact]
    public void Unseen_note_is_kept_and_gets_its_full_time_once_visible()
    {
        var m = new ReceiveTileModel();
        m.AddNote(["x"], [], T0);
        m.Tick(T0, false);
        m.Tick(T0.AddMinutes(5), false);
        Assert.NotNull(m.CurrentNote);
        m.Tick(T0.AddMinutes(5).AddSeconds(7), true);
        Assert.NotNull(m.CurrentNote);
        m.Tick(T0.AddMinutes(5).AddSeconds(8.5), true);
        Assert.Null(m.CurrentNote);
    }

    [Fact]
    public void Countdown_pauses_when_the_card_hides_again()
    {
        var m = new ReceiveTileModel();
        m.AddNote(["x"], [], T0);
        m.Tick(T0, true);
        m.Tick(T0.AddSeconds(5), true);
        m.Tick(T0.AddSeconds(60), false);   // hidden for a minute
        m.Tick(T0.AddSeconds(62), true);
        Assert.NotNull(m.CurrentNote);      // 5 + 2 s seen
        m.Tick(T0.AddSeconds(65), true);
        Assert.Null(m.CurrentNote);
    }

    [Fact]
    public void Only_MaxNotes_unseen_notes_are_kept()
    {
        var m = new ReceiveTileModel();
        for (int i = 0; i < ReceiveTileModel.MaxNotes + 3; i++) m.AddNote([$"n{i}"], [], T0);
        int seen = 0;
        while (m.CurrentNote is not null && seen < 100) { m.RemoveNote(m.CurrentNote.Id); seen++; }
        Assert.Equal(ReceiveTileModel.MaxNotes, seen);
    }

    [Fact]
    public void Hovered_note_stays_and_gets_a_short_grace_after_the_pointer_leaves()
    {
        var m = new ReceiveTileModel();
        m.AddNote(["x"], [], T0);
        m.Tick(T0, true);
        m.Tick(T0.AddSeconds(6), true);   // 2 s left
        m.SetHovered(true, T0.AddSeconds(6));
        m.Tick(T0.AddSeconds(60), true);
        Assert.NotNull(m.CurrentNote);
        m.SetHovered(false, T0.AddSeconds(60));
        m.Tick(T0.AddSeconds(62), true);
        Assert.NotNull(m.CurrentNote);
        m.Tick(T0.AddSeconds(63.1), true);
        Assert.Null(m.CurrentNote);
    }

    [Fact]
    public void Newest_note_is_shown_and_the_previous_returns_when_it_goes()
    {
        var m = new ReceiveTileModel();
        m.AddNote(["old"], [], T0);
        var id = m.AddNote(["new"], [], T0.AddSeconds(1));
        Assert.Equal("new", m.CurrentNote!.Lines[0]);
        m.RemoveNote(id);
        Assert.Equal("old", m.CurrentNote!.Lines[0]);
    }
}

public class AttentionBounceTests
{
    static bool No() => false;

    [Fact]
    public void Bounces_at_once_then_every_ten_seconds_while_a_consent_is_pending()
    {
        var a = new AttentionBounce();
        Assert.True(a.Due(0, true, PanelState.Hidden, No));
        Assert.False(a.Due(9_000, true, PanelState.Hidden, No));
        Assert.True(a.Due(10_000, true, PanelState.Hidden, No));
        Assert.False(a.Due(19_999, true, PanelState.Hidden, No));
        Assert.True(a.Due(20_000, true, PanelState.Hidden, No));
    }

    [Fact]
    public void Nothing_without_a_consent_and_the_count_restarts_for_the_next_one()
    {
        var a = new AttentionBounce();
        Assert.False(a.Due(0, false, PanelState.Hidden, No));
        Assert.True(a.Due(1_000, true, PanelState.Hidden, No));
        Assert.False(a.Due(2_000, false, PanelState.Hidden, No));
        Assert.True(a.Due(3_000, true, PanelState.Hidden, No));
    }

    [Theory]
    [InlineData(PanelState.Open)]
    [InlineData(PanelState.Peek)]
    public void Never_while_open_or_peek(PanelState state)
    {
        var a = new AttentionBounce();
        Assert.False(a.Due(0, true, state, No));
        Assert.False(a.Due(60_000, true, state, No));
        Assert.True(a.Due(60_001, true, PanelState.Hidden, No));
    }

    [Fact]
    public void Fullscreen_suppresses_and_is_only_probed_when_due()
    {
        var a = new AttentionBounce();
        int probes = 0;
        bool Full() { probes++; return true; }
        Assert.False(a.Due(0, true, PanelState.Hidden, Full));
        Assert.Equal(1, probes);
        Assert.False(a.Due(1_000, true, PanelState.Open, Full));
        Assert.Equal(1, probes);
        Assert.True(a.Due(2_000, true, PanelState.Hidden, No));   // fullscreen is gone: bounce without waiting 10 s
    }
}
