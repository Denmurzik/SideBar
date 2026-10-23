namespace SideBar.Nearby.Crypto;

public static class Pin
{
    private const int Modulo = 9973;
    private const int Multiplier = 31;

    /// <summary>
    /// Port of the Chromium Nearby Share pin code: signed bytes, polynomial hash modulo 9973, absolute value, 4 digits.
    /// </summary>
    public static string FromAuthString(ReadOnlySpan<byte> authString)
    {
        var hash = 0;
        var multiplier = 1;
        foreach (var b in authString)
        {
            hash = (hash + (sbyte)b * multiplier) % Modulo;
            multiplier = multiplier * Multiplier % Modulo;
        }
        return Math.Abs(hash).ToString("D4");
    }
}
