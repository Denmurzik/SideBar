using System.Security.Cryptography;
using System.Text;

namespace SideBar.Nearby.Wire;

public enum DeviceKind : byte
{
    Unknown = 0,
    Phone = 1,
    Tablet = 2,
    Laptop = 3,
    Car = 4,
    Foldable = 5,
    Xr = 6,
}

public readonly record struct EndpointTlv(byte Type, byte[] Value)
{
    public const byte QrCode = 1;
    public const byte VendorId = 2;
}

/// <summary>
/// Endpoint info as carried in the mDNS "n" TXT record and in ConnectionRequestFrame.endpoint_info:
/// 1 byte (version:3 | hidden:1 | type:3 | reserved:1), 16 bytes (salt + encrypted metadata key),
/// name (1-byte length + UTF-8, only when visible), optional TLV records.
/// </summary>
public sealed class EndpointInfo
{
    public const int MetadataLength = 16;

    public int Version { get; init; }
    public DeviceKind Kind { get; init; }
    public bool Hidden { get; init; }
    public byte[] Metadata { get; init; } = new byte[MetadataLength];
    public string? Name { get; init; }
    public IReadOnlyList<EndpointTlv> Tlvs { get; init; } = [];

    public static EndpointInfo CreateAnonymous(string name, DeviceKind kind) => new()
    {
        Kind = kind,
        Name = name,
        Metadata = RandomNumberGenerator.GetBytes(MetadataLength),
    };

    public byte[] Encode()
    {
        if (Metadata.Length != MetadataLength)
            throw new InvalidOperationException("Metadata must be 16 bytes.");

        using var ms = new MemoryStream();
        ms.WriteByte((byte)(((Version & 7) << 5) | (Hidden ? 0x10 : 0) | (((int)Kind & 7) << 1)));
        ms.Write(Metadata);
        if (!Hidden)
        {
            var name = TruncateUtf8(Name ?? string.Empty, 255);
            ms.WriteByte((byte)name.Length);
            ms.Write(name);
        }
        foreach (var tlv in Tlvs)
        {
            if (tlv.Value.Length > 255) throw new InvalidOperationException("TLV value too long.");
            ms.WriteByte(tlv.Type);
            ms.WriteByte((byte)tlv.Value.Length);
            ms.Write(tlv.Value);
        }
        return ms.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out EndpointInfo? info)
    {
        info = null;
        if (data.Length < 1 + MetadataLength) return false;

        var b0 = data[0];
        var hidden = (b0 & 0x10) != 0;
        var pos = 1 + MetadataLength;
        var metadata = data.Slice(1, MetadataLength).ToArray();

        string? name = null;
        if (!hidden)
        {
            if (pos >= data.Length) return false;
            int len = data[pos++];
            if (pos + len > data.Length) return false;
            try { name = new UTF8Encoding(false, true).GetString(data.Slice(pos, len)); }
            catch (ArgumentException) { return false; }
            pos += len;
        }

        var tlvs = new List<EndpointTlv>();
        while (pos + 2 <= data.Length)
        {
            var type = data[pos++];
            int len = data[pos++];
            if (pos + len > data.Length) return false;
            tlvs.Add(new EndpointTlv(type, data.Slice(pos, len).ToArray()));
            pos += len;
        }

        info = new EndpointInfo
        {
            Version = b0 >> 5,
            Hidden = hidden,
            Kind = (DeviceKind)((b0 >> 1) & 7),
            Metadata = metadata,
            Name = name,
            Tlvs = tlvs,
        };
        return true;
    }

    public string ToBase64Url() => Base64Url.Encode(Encode());

    public static bool TryFromBase64Url(string text, out EndpointInfo? info)
    {
        info = null;
        return Base64Url.TryDecode(text, out var bytes) && TryDecode(bytes, out info);
    }

    private static byte[] TruncateUtf8(string s, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes) return bytes;
        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return bytes.AsSpan(0, cut).ToArray();
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        try
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException) { return false; }
    }
}
