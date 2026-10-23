namespace SideBar.Core.Nearby;

public enum TileContent { Idle, Send, Note, Consent }

/// <summary>A button inside the tile; <paramref name="Act"/> runs on the UI thread.</summary>
public sealed record TileButton(string Label, Action Act);

/// <summary>An incoming request waiting for Accept/Decline; each one has its own decision timer owned by its session.</summary>
public sealed record ConsentItem(int Id, string Line, string Pin);

/// <summary>A short result line with buttons; the first line is the headline.</summary>
public sealed record NoteItem(int Id, IReadOnlyList<string> Lines, IReadOnlyList<TileButton> Buttons);

/// <summary>
/// What the receive side puts into the Quick Share tile: pending consents (stacked, paged when they do not fit) and short
/// notes that hide on their own. Pure model, UI thread only, time comes from the caller.
/// Priority in the tile: consent > send (anything but Idle) > note > idle logo.
/// </summary>
public sealed class ReceiveTileModel
{
    /// <summary>Consent rows that fit into the tile at once; with more, one row is shown with a counter and prev/next.</summary>
    public const int MaxStackedRows = 2;
    public const int MaxNotes = 4;
    public static readonly TimeSpan NoteDuration = TimeSpan.FromSeconds(8);
    /// <summary>Time left for a note after the pointer leaves it.</summary>
    public static readonly TimeSpan NoteAfterHover = TimeSpan.FromSeconds(3);
    /// <summary>The card stays open this long after the last consent was decided.</summary>
    public static readonly TimeSpan HoldAfterDecision = TimeSpan.FromSeconds(1.5);

    readonly List<ConsentItem> _consents = [];
    readonly List<(NoteItem Note, DateTimeOffset Until)> _notes = [];
    int _seq, _page;
    bool _hovered;
    DateTimeOffset _holdUntil;

    public event Action? Changed;

    public IReadOnlyList<ConsentItem> Consents => _consents;
    public bool HasConsent => _consents.Count > 0;

    /// <summary>Index of the consent shown in paged mode.</summary>
    public int Page => _consents.Count == 0 ? 0 : Math.Clamp(_page, 0, _consents.Count - 1);

    public bool Paged => _consents.Count > MaxStackedRows;

    /// <summary>Consents drawn right now: all of them when they fit, otherwise only the current page.</summary>
    public IReadOnlyList<ConsentItem> VisibleConsents => Paged ? [_consents[Page]] : _consents;

    /// <summary>Other consents not on screen (the "ещё N" counter).</summary>
    public int HiddenCount => Paged ? _consents.Count - 1 : 0;

    public NoteItem? CurrentNote => _notes.Count == 0 ? null : _notes[^1].Note;

    public int AddConsent(string line, string pin)
    {
        int id = ++_seq;
        _consents.Add(new ConsentItem(id, line, pin));
        Changed?.Invoke();
        return id;
    }

    public void RemoveConsent(int id, DateTimeOffset now)
    {
        int i = _consents.FindIndex(c => c.Id == id);
        if (i < 0) return;
        _consents.RemoveAt(i);
        if (i < _page) _page--;   // the same consent stays on screen
        _page = Math.Clamp(_page, 0, Math.Max(0, _consents.Count - 1));
        if (_consents.Count == 0) _holdUntil = now + HoldAfterDecision;
        Changed?.Invoke();
    }

    public void MovePage(int delta)
    {
        if (!Paged) return;
        _page = ((Page + delta) % _consents.Count + _consents.Count) % _consents.Count;
        Changed?.Invoke();
    }

    public int AddNote(IReadOnlyList<string> lines, IReadOnlyList<TileButton> buttons, DateTimeOffset now)
    {
        int id = ++_seq;
        _notes.Add((new NoteItem(id, lines, buttons), now + NoteDuration));
        if (_notes.Count > MaxNotes) _notes.RemoveAt(0);
        Changed?.Invoke();
        return id;
    }

    public void RemoveNote(int id)
    {
        if (_notes.RemoveAll(n => n.Note.Id == id) > 0) Changed?.Invoke();
    }

    /// <summary>The pointer is over the note: it does not hide; after it leaves the note has <see cref="NoteAfterHover"/> left.</summary>
    public void SetHovered(bool hovered, DateTimeOffset now)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        if (!hovered && _notes.Count > 0)
        {
            var last = _notes[^1];
            var until = now + NoteAfterHover;
            if (until > last.Until) _notes[^1] = (last.Note, until);
        }
    }

    public void Tick(DateTimeOffset now)
    {
        if (_hovered && _notes.Count > 0) return;
        if (_notes.RemoveAll(n => now >= n.Until) > 0) Changed?.Invoke();
    }

    /// <summary>Consents pending, or the pause after the last decision is not over.</summary>
    public bool HoldsCardOpen(DateTimeOffset now) => HasConsent || now < _holdUntil;

    /// <summary>The tile needs ticks (notes expire, the hold ends, attention bounces repeat).</summary>
    public bool NeedsTick(DateTimeOffset now) => HasConsent || _notes.Count > 0 || now < _holdUntil;

    public TileContent Content(ShareTileState send) => Priority(HasConsent, send, _notes.Count > 0);

    public static TileContent Priority(bool consent, ShareTileState send, bool note) =>
        consent ? TileContent.Consent
        : send != ShareTileState.Idle ? TileContent.Send
        : note ? TileContent.Note
        : TileContent.Idle;
}
