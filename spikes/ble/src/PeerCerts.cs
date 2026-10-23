using System.Text.Json;
using SideBar.Certs;
using SideBar.Nearby.Wire;
using LevelDbReader = SideBar.Nearby.Certs.LevelDbReader;

// Memory-only view of the official app's certificates for `watch --identify` (docs/research/ble-locked-phone.md).
// Files are read with FileShare.ReadWrite | Delete into memory; nothing is written to disk. Only device_name, counts,
// validity and yes/no facts may be printed: never salts, hashes, ids or keys.
sealed record PeerCert(int Index, PublicCertificate Cert, string? DeviceName, bool ValidNow, double HoursLeft);

sealed class PeerCerts
{
    private static readonly TimeSpan PublicTolerance = TimeSpan.FromMinutes(30); // as google/nearby

    public IReadOnlyList<PeerCert> Peers { get; private init; } = [];

    public static string SharingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Nearby", "Sharing");

    public static PeerCerts Load(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        List<PrivateCertificate> privates;
        using (var doc = JsonDocument.Parse(ReadShared(Path.Combine(SharingDirectory, "preferences.json"))))
            privates = doc.RootElement.GetProperty("nearby_sharing.private_certificate_list").EnumerateArray()
                .Select(PrivateCertificate.FromJson).ToList();
        // Our own certificates come back from the server as public ones; their ids exclude them.
        var ownIds = privates.Select(c => Convert.ToHexString(c.Id)).ToHashSet();

        var dbDir = Path.Combine(SharingDirectory, "NearbySharePublicCertificateDatabase");
        LevelDbReader db;
        try { db = LevelDbReader.Open(Snapshot(dbDir)); }
        catch (Exception e) when (e is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException)
        { db = LevelDbReader.Open(Snapshot(dbDir)); } // compaction race: one retry on a fresh snapshot

        var nowSeconds = at.ToUnixTimeSeconds();
        var tolerance = (long)PublicTolerance.TotalSeconds;
        // Order: device, then window start (google/nearby GetValidPrivateCertificate picks the first valid one).
        var peers = db.Entries.Values.Select(PublicCertificate.Parse)
            .Where(c => c.IsStructurallyValid && !ownIds.Contains(Convert.ToHexString(c.SecretId)))
            .Select(c => (Cert: c, Meta: Metadata(c)))
            .OrderBy(p => p.Meta ?? "").ThenBy(p => p.Cert.StartSeconds)
            .Select((p, i) => new PeerCert(i, p.Cert, p.Meta,
                nowSeconds >= p.Cert.StartSeconds - tolerance && nowSeconds < p.Cert.EndSeconds + tolerance,
                (p.Cert.EndSeconds - nowSeconds) / 3600.0))
            .ToList();
        return new PeerCerts { Peers = peers };
    }

    /// <summary>The peer certificate whose key decrypts this advertised salt + encrypted metadata key, or null.</summary>
    public PeerCert? Identify(ReadOnlySpan<byte> metadata16)
    {
        if (metadata16.Length != EndpointInfo.MetadataLength) return null;
        var salt = metadata16[..2].ToArray();
        var encrypted = metadata16[2..].ToArray();
        return Peers.FirstOrDefault(p => p.Cert.TryDecryptMetadataKey(salt, encrypted) is not null);
    }

    // EncryptedMetadata field 1 = device_name; no other field is read.
    private static string? Metadata(PublicCertificate cert)
    {
        if (cert.MetadataEncryptionKey.Length == 0) return null;
        var plain = cert.DecryptMetadata(cert.MetadataEncryptionKey);
        if (plain is null) return null;
        try { return Proto.Bytes(Proto.Parse(plain), 1) is { } n ? System.Text.Encoding.UTF8.GetString(n) : null; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    private static Dictionary<string, byte[]> Snapshot(string dir) =>
        Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != "LOCK")
            .ToDictionary(f => Path.GetFileName(f), ReadShared);

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
