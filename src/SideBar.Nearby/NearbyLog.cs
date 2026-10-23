namespace SideBar.Nearby;

/// <summary>
/// Whitelist protocol log entry: <paramref name="Kind"/> is "role.direction.frame", <paramref name="Size"/> a byte count,
/// <paramref name="Value"/> only an enum or result name. Never file names, text, keys or addresses.
/// </summary>
public sealed record NearbyLogEvent(string Kind, long? Size = null, string? Value = null);

public static class NearbyLog
{
    /// <summary>Sink for protocol events; no-op while null. Called from arbitrary threads.</summary>
    public static Action<NearbyLogEvent>? Log { get; set; }

    internal static void Write(NearbyLogEvent e)
    {
        try { Log?.Invoke(e); }
        catch (Exception) { }
    }
}
