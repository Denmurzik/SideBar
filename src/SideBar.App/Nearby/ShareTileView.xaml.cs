using Microsoft.UI.Xaml;
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
            slot.Tapped += (_, _) => { Log.Write($"share: slot {k} tapped"); if (_slotIds[k] is { } id) DeviceChosen?.Invoke(id); };
            slot.PointerEntered += (_, _) => slot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            slot.PointerExited += (_, _) => slot.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>Возвращает true, если плитка занята отправкой (логотип надо скрыть).</summary>
    public bool Render(ShareTileModel m)
    {
        SearchPanel.Visibility = m.State == ShareTileState.Searching ? Visibility.Visible : Visibility.Collapsed;
        DevicesGrid.Visibility = m.State == ShareTileState.Choosing ? Visibility.Visible : Visibility.Collapsed;
        ChosenPanel.Visibility = m.State is ShareTileState.Sending or ShareTileState.Done or ShareTileState.Failed ? Visibility.Visible : Visibility.Collapsed;

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
        return m.State != ShareTileState.Idle;
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
