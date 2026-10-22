namespace SideBar.Core.Layout;

public static class CardMetrics
{
    public const double CardW = 216, CardH = 469, EdgeGap = 8, PeekVisible = 10;
}

/// <summary>Геометрия карточки на мониторе в физических пикселях.</summary>
public sealed class CardLayout(PxRect monitor, double scale)
{
    public int CardWidthPx { get; } = Px(CardMetrics.CardW * scale);
    public int CardHeightPx { get; } = Px(CardMetrics.CardH * scale);

    /// <summary>Верх карточки: центр по вертикали монитора.</summary>
    public int Top { get; } = monitor.Top + (monitor.Height - Px(CardMetrics.CardH * scale)) / 2;

    public int CenterY => Top + CardHeightPx / 2;

    /// <summary>1 см в физических пикселях при 96 dpi * scale.</summary>
    public int PeekBandPx { get; } = Px(96 * scale / 2.54);

    public int LeftFor(PanelState s) => s switch
    {
        PanelState.Hidden => monitor.Right,
        PanelState.Peek => monitor.Right - Px(CardMetrics.PeekVisible * scale),
        PanelState.Open => monitor.Right - Px((CardMetrics.CardW + CardMetrics.EdgeGap) * scale),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public PxRect ActivationBand => new(monitor.Right - PeekBandPx, Top - PeekBandPx, monitor.Right, Top + CardHeightPx + PeekBandPx);

    public PxRect CardRectAt(int left) => new(left, Top, left + CardWidthPx, Top + CardHeightPx);

    static int Px(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
