namespace SideBar.Core.Motion;

/// <summary>
/// Attention cue for InPlace mode (a monitor sits to the right, so the card cannot hop out): the card at its in-place position
/// fades 0 to <see cref="Peak"/> to 0, <see cref="Pulses"/> times over <see cref="Duration"/>. Pure function of time.
/// </summary>
public static class AttentionPulse
{
    public const double Peak = 0.85;
    public const int Pulses = 2;
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.3);

    public static double Opacity(double tSeconds)
    {
        double total = Duration.TotalSeconds;
        if (tSeconds <= 0 || tSeconds >= total) return 0;
        double u = tSeconds / total * Pulses % 1.0;   // position inside the current pulse
        double s = Math.Sin(Math.PI * u);
        return Peak * s * s;
    }
}
