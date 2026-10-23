// Certificate model and crypto, following google/nearby sharing/certificates (Apache-2.0):
//   nearby_share_private_certificate.cc, nearby_share_decrypted_public_certificate.cc, common.cc.
using System.Security.Cryptography;
using System.Text.Json;

namespace SideBar.Nearby.Certs;

internal sealed class PrivateCertificate
{
    public int Visibility;
    public DateTimeOffset NotBefore, NotAfter;
    public byte[] KeyPairPkcs8 = [];
    public byte[] SecretKey = [];
    public byte[] MetadataEncryptionKey = [];
    public byte[] Id = [];
    public byte[] UnencryptedMetadata = [];
    public List<byte[]> ConsumedSalts = [];

    // preferences.json -> nearby_sharing.private_certificate_list[]
    public static PrivateCertificate FromJson(JsonElement e) => new()
    {
        Visibility = e.GetProperty("visibility").GetInt32(),
        NotBefore = FromUnixNanos(e.GetProperty("not_before").GetInt64()),
        NotAfter = FromUnixNanos(e.GetProperty("not_after").GetInt64()),
        KeyPairPkcs8 = B64Url(e.GetProperty("key_pair").GetString()!),
        SecretKey = B64Url(e.GetProperty("secret_key").GetString()!),
        MetadataEncryptionKey = B64Url(e.GetProperty("metadata_encryption_key").GetString()!),
        Id = B64Url(e.GetProperty("id").GetString()!),
        UnencryptedMetadata = e.TryGetProperty("unencrypted_metadata", out var um) && um.GetString() is { } umText ? B64Url(umText) : [],
        ConsumedSalts = e.TryGetProperty("consumed_salts", out var cs) && cs.GetString() is { } csText ? Salts(csText) : [],
    };

    static DateTimeOffset FromUnixNanos(long ns) => DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000);

    static List<byte[]> Salts(string hex)
    {
        var l = new List<byte[]>();
        for (int i = 0; i + 4 <= hex.Length; i += 4) l.Add(Convert.FromHexString(hex.AsSpan(i, 4)));
        return l;
    }

    public static byte[] B64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight((s.Length + 3) / 4 * 4, '=');
        return Convert.FromBase64String(s);
    }
}

internal sealed class PublicCertificate
{
    public byte[] SecretId = [], SecretKey = [], PublicKey = [], MetadataEncryptionKey = [], EncryptedMetadata = [], KeyTag = [];
    public long StartSeconds, EndSeconds;
    public bool ForSelfShare;

    public static PublicCertificate Parse(byte[] value)
    {
        var f = ProtoReader.Parse(value);
        var ts = (int n) => { var b = ProtoReader.Bytes(f, n); return b is null ? 0 : ProtoReader.Int(ProtoReader.Parse(b), 1); };
        return new()
        {
            SecretId = ProtoReader.Bytes(f, 1) ?? [],
            SecretKey = ProtoReader.Bytes(f, 2) ?? [],
            PublicKey = ProtoReader.Bytes(f, 3) ?? [],
            StartSeconds = ts(4),
            EndSeconds = ts(5),
            MetadataEncryptionKey = ProtoReader.Bytes(f, 7) ?? [],
            EncryptedMetadata = ProtoReader.Bytes(f, 8) ?? [],
            KeyTag = ProtoReader.Bytes(f, 9) ?? [],
            ForSelfShare = ProtoReader.Int(f, 10) != 0,
        };
    }

    public bool IsStructurallyValid =>
        StartSeconds < EndSeconds && PublicKey.Length > 0 && SecretKey.Length == 32 && SecretId.Length == 32 &&
        EncryptedMetadata.Length > 0 && KeyTag.Length == 32;
}

internal static class NearbyCrypto
{
    // AES-256-CTR, counter block = HKDF-SHA256(salt(2 bytes), no salt, no info, 16 bytes).
    public static byte[] Ctr(byte[] secretKey, byte[] salt, byte[] data)
    {
        var counter = HKDF.DeriveKey(HashAlgorithmName.SHA256, salt, 16, salt: [], info: []);
        using var aes = Aes.Create();
        aes.Key = secretKey;
        var outp = new byte[data.Length];
        var ks = new byte[16];
        for (int off = 0; off < data.Length; off += 16)
        {
            aes.EncryptEcb(counter, ks, PaddingMode.None);
            int n = Math.Min(16, data.Length - off);
            for (int i = 0; i < n; i++) outp[off + i] = (byte)(data[off + i] ^ ks[i]);
            for (int i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        return outp;
    }
}
