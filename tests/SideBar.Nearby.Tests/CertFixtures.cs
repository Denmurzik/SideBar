using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SideBar.Nearby.Certs;

namespace SideBar.Nearby.Tests;

/// <summary>Synthetic certificates and a synthetic Google "Sharing" directory; nothing touches real official-app data.</summary>
internal static class CertFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public sealed record Device(string Name, PrivateCertificate Private, PublicCertificate Public);

    public static Device NewDevice(
        string name, int visibility = 2, TimeSpan? offset = null, TimeSpan? lifetime = null, byte[]? bluetoothMac = null)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var metadataKey = RandomNumberGenerator.GetBytes(14);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var start = Now + (offset ?? TimeSpan.Zero) - TimeSpan.FromHours(1);
        var end = start + (lifetime ?? TimeSpan.FromHours(72));

        // EncryptedMetadata message with field 1 (device name) and, when given, field 4 (Bluetooth MAC).
        var nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] plain = bluetoothMac is null
            ? [0x0A, (byte)nameBytes.Length, .. nameBytes]
            : [0x0A, (byte)nameBytes.Length, .. nameBytes, (4 << 3) | 2, (byte)bluetoothMac.Length, .. bluetoothMac];

        var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, metadataKey, 32, [], []);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 12, [], []);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(aesKey, 16)) gcm.Encrypt(nonce, plain, cipher, tag);

        var id = SHA256.HashData(secret);
        return new Device(name,
            new PrivateCertificate
            {
                Visibility = visibility, NotBefore = start, NotAfter = end,
                KeyPairPkcs8 = ec.ExportPkcs8PrivateKey(), SecretKey = secret, MetadataEncryptionKey = metadataKey, Id = id,
            },
            new PublicCertificate
            {
                SecretId = id, SecretKey = secret, PublicKey = ec.ExportSubjectPublicKeyInfo(),
                StartSeconds = start.ToUnixTimeSeconds(), EndSeconds = end.ToUnixTimeSeconds(),
                MetadataEncryptionKey = metadataKey, EncryptedMetadata = [.. cipher, .. tag],
                KeyTag = HMACSHA256.HashData(new byte[32], metadataKey), ForSelfShare = true,
            });
    }

    /// <summary>Writes preferences.json and a leveldb (one .log) the way the official app lays them out.</summary>
    public static void WriteSharingDir(
        string dir, IEnumerable<PrivateCertificate> privates, IEnumerable<PublicCertificate> publics)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "preferences.json"), PreferencesJson(privates));
        WriteLevelDb(Path.Combine(dir, "NearbySharePublicCertificateDatabase"), publics);
    }

    public static string PreferencesJson(IEnumerable<PrivateCertificate> privates)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("nearby_sharing.private_certificate_list");
            foreach (var c in privates)
            {
                w.WriteStartObject();
                w.WriteNumber("visibility", c.Visibility);
                w.WriteNumber("not_before", c.NotBefore.ToUnixTimeMilliseconds() * 1_000_000);
                w.WriteNumber("not_after", c.NotAfter.ToUnixTimeMilliseconds() * 1_000_000);
                w.WriteString("key_pair", B64Url(c.KeyPairPkcs8));
                w.WriteString("secret_key", B64Url(c.SecretKey));
                w.WriteString("metadata_encryption_key", B64Url(c.MetadataEncryptionKey));
                w.WriteString("id", B64Url(c.Id));
                w.WriteString("unencrypted_metadata", B64Url(c.UnencryptedMetadata));
                w.WriteString("consumed_salts", string.Concat(c.ConsumedSalts.Select(Convert.ToHexString)));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void WriteLevelDb(string dir, IEnumerable<PublicCertificate> publics)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CURRENT"), "MANIFEST-000001\n");

        // VersionEdit: comparator (tag 1), log number 3 (tag 2).
        var comparator = Encoding.ASCII.GetBytes("leveldb.BytewiseComparator");
        File.WriteAllBytes(Path.Combine(dir, "MANIFEST-000001"),
            LogRecord([1, (byte)comparator.Length, .. comparator, 2, 3]));

        var batch = new List<byte>();
        batch.AddRange(BitConverter.GetBytes(1UL));
        var list = publics.ToList();
        batch.AddRange(BitConverter.GetBytes(list.Count));
        foreach (var cert in list)
        {
            var value = EncodePublic(cert);
            batch.Add(1);
            AddVarint(batch, cert.SecretId.Length);
            batch.AddRange(cert.SecretId);
            AddVarint(batch, value.Length);
            batch.AddRange(value);
        }
        File.WriteAllBytes(Path.Combine(dir, "000003.log"), LogRecord([.. batch]));
    }

    // One physical record (type 1 = full); the reader does not check the CRC.
    private static byte[] LogRecord(byte[] payload) =>
        [0, 0, 0, 0, (byte)payload.Length, (byte)(payload.Length >> 8), 1, .. payload];

    private static byte[] EncodePublic(PublicCertificate c)
    {
        var m = new List<byte>();
        Bytes(m, 1, c.SecretId);
        Bytes(m, 2, c.SecretKey);
        Bytes(m, 3, c.PublicKey);
        Bytes(m, 4, Timestamp(c.StartSeconds));
        Bytes(m, 5, Timestamp(c.EndSeconds));
        Bytes(m, 7, c.MetadataEncryptionKey);
        Bytes(m, 8, c.EncryptedMetadata);
        Bytes(m, 9, c.KeyTag);
        m.Add(10 << 3);
        m.Add(c.ForSelfShare ? (byte)1 : (byte)0);
        return [.. m];
    }

    private static byte[] Timestamp(long seconds)
    {
        var m = new List<byte> { 1 << 3 };
        AddVarint(m, seconds);
        return [.. m];
    }

    private static void Bytes(List<byte> m, int field, byte[] value)
    {
        m.Add((byte)((field << 3) | 2));
        AddVarint(m, value.Length);
        m.AddRange(value);
    }

    private static void AddVarint(List<byte> m, long value)
    {
        var v = (ulong)value;
        while (v >= 0x80) { m.Add((byte)(v | 0x80)); v >>= 7; }
        m.Add((byte)v);
    }

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>TimeProvider whose timers fire only when the test says so.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public List<ManualTimer> Timers { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state) { Due = dueTime };
        Timers.Add(timer);
        return timer;
    }
}

internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
{
    public TimeSpan Due { get; set; }
    public bool Disposed { get; private set; }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        Due = dueTime;
        return true;
    }

    public void Fire() => callback(state);

    public void Dispose() => Disposed = true;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
