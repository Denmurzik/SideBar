namespace SideBar.Core.Motion;

/// <summary>
/// Dock-style bounce of the hidden card on an incoming request: three hops out from the screen edge with decaying height.
/// Pure function of time; heights are absolute, in epx (scaled to pixels by the caller).
/// Each hop rises fast (ease-out), then falls on a parabola; the next hop starts at the landing.
/// </summary>
public static class Bounce
{
    /// <summary>Hop heights in epx.</summary>
    public static readonly IReadOnlyList<double> Peaks = [40, 22, 10];

    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.3);

    /// <summary>Share of one hop spent rising; the rest is the fall.</summary>
    public const double RiseShare = 0.35;

    static readonly double[] HopSeconds = HopDurations();

    static double[] HopDurations()
    {
        // a ball's flight time grows with the square root of its height
        var weights = Peaks.Select(Math.Sqrt).ToArray();
        double sum = weights.Sum();
        return weights.Select(w => Duration.TotalSeconds * w / sum).ToArray();
    }

    /// <summary>Start time of hop <paramref name="index"/> in seconds.</summary>
    public static double HopStart(int index) => HopSeconds.Take(index).Sum();

    public static double HopLength(int index) => HopSeconds[index];

    /// <summary>Distance of the card from its hidden position, in epx.</summary>
    public static double OffsetEpx(double tSeconds)
    {
        if (tSeconds <= 0 || tSeconds >= Duration.TotalSeconds) return 0;
        double start = 0;
        for (int i = 0; i < Peaks.Count; i++)
        {
            double len = HopSeconds[i];
            if (tSeconds < start + len || i == Peaks.Count - 1)
            {
                double u = Math.Clamp((tSeconds - start) / len, 0, 1);
                if (u < RiseShare) return Peaks[i] * Math.Sin(Math.PI / 2 * (u / RiseShare));
                double w = (u - RiseShare) / (1 - RiseShare);
                return Peaks[i] * (1 - w * w);
            }
            start += len;
        }
        return 0;
    }
}
