using Microsoft.UI.Xaml;
using Visibility = Microsoft.UI.Xaml.Visibility;   // SideBar.Core.Nearby.Visibility (Hidden/Everyone) конфликтует
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideBar.Core.Nearby;
using Windows.Foundation;

namespace SideBar.Nearby;

/// <summary>
/// Содержимое плитки Quick Share, пока идёт отправка: поиск, иконки найденных устройств (до 4, имя под иконкой),
/// кольцо прогресса вокруг выбранного, галочка/крестик. В покое плитка показывает логотип (его держит CardWindow).
/// </summary>
public sealed partial class ShareTileView : UserControl
{
    const string PhoneGlyph = "", LaptopGlyph = "", TabletGlyph = "", OtherGlyph = "";
    const string CheckGlyph = "", CrossGlyph = "";
    // дуга по окружности радиуса 32 (диаметр дорожки 64) внутри холста 72×72
    const double RingRadius = 32, RingCenter = 36;

    readonly Grid[] _slots;
    readonly FontIcon[] _slotIcons;
    readonly TextBlock[] _slotNames;
    readonly string?[] _slotIds = new string?[ShareTileModel.MaxDevices];

    /// <summary>Клик по иконке устройства (UI-поток).</summary>
    public event Action<string>? DeviceChosen;

    public ShareTileView()
    {
        InitializeComponent();
        _slots = [Slot0, Slot1, Slot2, Slot3];
        _slotIcons = [SlotIcon0, SlotIcon1, SlotIcon2, SlotIcon3];
        _slotNames = [SlotName0, SlotName1, SlotName2, SlotName3];
        for (int i = 0; i < _slots.Length; i++)
        {
            int k = i;
            var slot = _slots[i];
            // нажатие на иконку засчитывается так же, как у кнопок запроса: сразу после смены содержимого плитки оно может попасть в чужую иконку
            bool armed = false;
            slot.PointerPressed += (_, _) => armed = CanPress();
            slot.Tapped += (_, _) =>
            {
                if (!armed) { Log.Write("tile tap ignored (not armed)"); return; }
                Log.Write($"share: slot {k} tapped");
                if (_slotIds[k] is { } id) DeviceChosen?.Invoke(id);
            };
            slot.PointerEntered += (_, _) => slot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            slot.PointerExited += (_, _) => slot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        }
    }

    ReceiveTileModel? _recv;
    Action<int, bool>? _decide;
    Func<bool> _settledOpen = () => false;
    readonly ConsentArming _arming = new();
    string _consentSignature = "", _noteSignature = "", _contentKey = "";

    /// <summary>Приём внутри плитки: модель запросов и заметок и обработчик «Принять»/«Отклонить» (UI-поток).</summary>
    public void BindReceive(ReceiveTileModel recv, Action<int, bool> decide, Func<bool> settledOpen)
    {
        _recv = recv;
        _decide = decide;
        _settledOpen = settledOpen;
        NotePanel.PointerEntered += (_, _) => recv.SetHovered(true, DateTimeOffset.UtcNow);
        NotePanel.PointerExited += (_, _) => recv.SetHovered(false, DateTimeOffset.UtcNow);
    }

    /// <summary>Возвращает true, если плитка занята (логотип надо скрыть). Приоритет: запрос > отправка > заметка > покой.</summary>
    public bool Render(ShareTileModel m)
    {
        string key = m.State + "|" + string.Join(',', m.Devices.Select(d => d.Id)) + "|" + (_recv?.HasConsent ?? false) + "|" + (_recv?.CurrentNote?.Id ?? 0);
        if (key != _contentKey) { _contentKey = key; _arming.Placed(DateTimeOffset.UtcNow); }   // под курсором могла оказаться другая кнопка
        var content = _recv?.Content(m.State) ?? (m.State == ShareTileState.Idle ? TileContent.Idle : TileContent.Send);
        bool send = content == TileContent.Send;   // отправка под запросом не прерывается, просто не видна
        SearchPanel.Visibility = send && m.State == ShareTileState.Searching ? Visibility.Visible : Visibility.Collapsed;
        DevicesGrid.Visibility = send && m.State == ShareTileState.Choosing ? Visibility.Visible : Visibility.Collapsed;
        ChosenPanel.Visibility = send && m.State is ShareTileState.Sending or ShareTileState.Done or ShareTileState.Failed ? Visibility.Visible : Visibility.Collapsed;
        ConsentPanel.Visibility = content == TileContent.Consent ? Visibility.Visible : Visibility.Collapsed;
        NotePanel.Visibility = content == TileContent.Note ? Visibility.Visible : Visibility.Collapsed;
        if (content == TileContent.Consent) RenderConsents();
        else if (content == TileContent.Note) RenderNote();

        switch (m.State)
        {
            case ShareTileState.Searching:
                SearchText.Text = Strings.Searching;
                break;
            case ShareTileState.Choosing:
                RenderDevices(m.Devices);
                break;
            case ShareTileState.Sending:
                var chosen = m.Devices.FirstOrDefault(d => d.Id == m.ChosenId);
                ChosenIcon.Glyph = GlyphFor(chosen?.Kind ?? ShareDeviceKind.Other);
                ChosenText.Text = chosen?.Name.Trim() ?? "";
                RingTrack.Visibility = Visibility.Visible;
                SetArc(m.Fraction);
                break;
            case ShareTileState.Done:
                RingTrack.Visibility = Visibility.Collapsed;
                RingArc.Data = null;
                ChosenIcon.Glyph = CheckGlyph;
                ChosenText.Text = m.Note ?? Strings.Sent;
                break;
            case ShareTileState.Failed:
                RingTrack.Visibility = Visibility.Collapsed;
                RingArc.Data = null;
                ChosenIcon.Glyph = CrossGlyph;
                ChosenText.Text = m.FailReason ?? Strings.SendFailed;
                break;
        }
        return content != TileContent.Idle;
    }

    /// <summary>Карточка приехала на место: строки запроса сдвинулись под курсор, нажатия снова ждут 600 мс.</summary>
    public void NotifyCardMoved() => _arming.Placed(DateTimeOffset.UtcNow);

    bool CanPress() => _arming.CanAccept(DateTimeOffset.UtcNow, _settledOpen());

    void RenderConsents()
    {
        var r = _recv!;
        string signature = string.Join(',', r.VisibleConsents.Select(c => c.Id)) + "|" + r.Paged + "|" + r.Page + "|" + r.Consents.Count;
        if (signature == _consentSignature) return;   // пересборка под курсором сорвала бы клик
        _consentSignature = signature;
        _arming.Placed(DateTimeOffset.UtcNow);   // новая строка, пропавшая строка или другая страница: под курсором может оказаться чужая кнопка
        ConsentPanel.Children.Clear();
        if (r.Paged) ConsentPanel.Children.Add(PagerRow(r));
        foreach (var c in r.VisibleConsents) ConsentPanel.Children.Add(ConsentRow(c, compact: r.VisibleConsents.Count > 1));
    }

    UIElement PagerRow(ReceiveTileModel r)
    {
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var prev = Button("‹", () => r.MovePage(-1), accent: false, narrow: true, gate: CanPress);
        var next = Button("›", () => r.MovePage(1), accent: false, narrow: true, gate: CanPress);
        var label = new TextBlock
        {
            Text = $"{r.Page + 1}/{r.Consents.Count} · {Strings.MoreConsents(r.HiddenCount)}",
            FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(next, 2);
        row.Children.Add(prev);
        row.Children.Add(label);
        row.Children.Add(next);
        return row;
    }

    UIElement ConsentRow(ConsentItem c, bool compact)
    {
        var panel = new StackPanel { Spacing = compact ? 4 : 6 };
        panel.Children.Add(new TextBlock
        {
            Text = c.Line, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        // PIN отдельной строкой без усечения: имя отправителя вытеснить его не может
        panel.Children.Add(new TextBlock { Text = Strings.Pin(c.Pin), FontSize = compact ? 15 : 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.NoWrap });
        var buttons = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() } };
        var accept = Button(Strings.Accept, () => _decide?.Invoke(c.Id, true), accent: true, gate: CanPress);
        var decline = Button(Strings.Decline, () => _decide?.Invoke(c.Id, false), accent: false, gate: CanPress);
        Grid.SetColumn(decline, 1);
        buttons.Children.Add(accept);
        buttons.Children.Add(decline);
        panel.Children.Add(buttons);
        return panel;
    }

    void RenderNote()
    {
        var r = _recv!;
        var note = r.CurrentNote;
        string signature = note is null ? "" : note.Id.ToString();
        if (signature == _noteSignature) return;
        _noteSignature = signature;
        NotePanel.Children.Clear();
        if (note is null) return;
        for (int i = 0; i < note.Lines.Count; i++)
        {
            NotePanel.Children.Add(new TextBlock
            {
                Text = note.Lines[i], FontSize = 12,
                FontWeight = i == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        foreach (var b in note.Buttons)
        {
            var button = b;
            NotePanel.Children.Add(Button(button.Label, () =>
            {
                try { button.Act(); }
                catch (Exception e) { Log.Write("note action failed: " + Log.Describe(e)); }
                r.RemoveNote(note.Id);
            }, accent: false, gate: CanPress));
        }
    }

    // Кнопка на Border + Tapped, как у иконок устройств: карточка не активируется, штатный Button в ней не проверен
    // gate: нажатие засчитывается, только если оно прошло проверку в момент PointerPressed (карточка открыта и стоит, строки не двигались 600 мс)
    static Border Button(string label, Action act, bool accent, bool narrow = false, Func<bool>? gate = null)
    {
        Brush normal = accent ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        Brush hover = accent ? (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"] : new SolidColorBrush(Windows.UI.Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF));
        var text = new TextBlock
        {
            Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = accent ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        var border = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(narrow ? 10 : 6, 5, narrow ? 10 : 6, 5), Background = normal, Child = text };
        border.PointerEntered += (_, _) => border.Background = hover;
        border.PointerExited += (_, _) => border.Background = normal;
        bool armed = gate is null;
        border.PointerPressed += (_, _) => armed = gate is null || gate();
        border.Tapped += (_, e) =>
        {
            e.Handled = true;
            if (!armed) { Log.Write("tile tap ignored (not armed)"); return; }
            try { act(); }
            catch (Exception ex) { Log.Write("tile action failed: " + Log.Describe(ex)); }
        };
        return border;
    }

    void RenderDevices(IReadOnlyList<DeviceView> devices)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (i >= devices.Count)
            {
                _slotIds[i] = null;
                _slots[i].Visibility = Visibility.Collapsed;
                continue;
            }
            var d = devices[i];
            _slotIds[i] = d.Id;
            _slotIcons[i].Glyph = GlyphFor(d.Kind);
            _slotNames[i].Text = d.Name.Trim();
            _slots[i].Visibility = Visibility.Visible;
        }
    }

    public static string GlyphFor(ShareDeviceKind kind) => kind switch
    {
        ShareDeviceKind.Phone => PhoneGlyph,
        ShareDeviceKind.Laptop => LaptopGlyph,
        ShareDeviceKind.Tablet => TabletGlyph,
        _ => OtherGlyph,
    };

    void SetArc(double fraction)
    {
        if (fraction <= 0.001) { RingArc.Data = null; return; }
        double f = Math.Min(fraction, 0.9999);
        double angle = f * 2 * Math.PI;
        var start = new Point(RingCenter, RingCenter - RingRadius);
        var end = new Point(RingCenter + RingRadius * Math.Sin(angle), RingCenter - RingRadius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(RingRadius, RingRadius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = f > 0.5,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        RingArc.Data = geometry;
    }
}
