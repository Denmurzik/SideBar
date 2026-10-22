using System.Security.Cryptography;

namespace SideBar.Nearby.Wire;

/// <summary>mDNS service type and instance name of the Quick Share Wi-Fi LAN medium.</summary>
public static class MdnsName
{
    public const string ServiceType = "_FC9F5ED42C8A._tcp";
    public const string ServiceTypeLocal = ServiceType + ".local";
    public const string TxtKeyEndpointInfo = "n";

    private const byte Pcp = 0x23;
    private static readonly byte[] ServiceId = [0xFC, 0x9F, 0x5E];
    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string NewEndpointId() =>
        string.Create(4, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
        });

    /// <summary>10 bytes: PCP, 4-byte endpoint id, 3-byte service id, 2 zero bytes; URL-safe base64 without padding.</summary>
    public static string Encode(string endpointId)
    {
        if (endpointId.Length != 4) throw new ArgumentException("Endpoint id must be 4 characters.", nameof(endpointId));
        var raw = new byte[10];
        raw[0] = Pcp;
        for (var i = 0; i < 4; i++) raw[1 + i] = checked((byte)endpointId[i]);
        ServiceId.CopyTo(raw, 5);
        return Base64Url.Encode(raw);
    }

    public static bool TryDecode(string instanceName, out string endpointId)
    {
        endpointId = string.Empty;
        if (!Base64Url.TryDecode(instanceName, out var raw)) return false;
        if (raw.Length != 10 || raw[0] != Pcp) return false;
        if (!raw.AsSpan(5, 3).SequenceEqual(ServiceId)) return false;
        endpointId = new string([(char)raw[1], (char)raw[2], (char)raw[3], (char)raw[4]]);
        return true;
    }

    /// <summary>Extracts the instance label from "{instance}._FC9F5ED42C8A._tcp.local".</summary>
    public static bool TryGetInstanceLabel(string fullName, out string label)
    {
        label = string.Empty;
        var idx = fullName.IndexOf("." + ServiceType, StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return false;
        label = fullName[..idx];
        return true;
    }
}
