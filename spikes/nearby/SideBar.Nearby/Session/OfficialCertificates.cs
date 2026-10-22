using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SideBar.Certs;
using PublicCertificate = SideBar.Certs.PublicCertificate;
using SideBar.Nearby.Protocol.Sharing;
using SideBar.Nearby.Wire;

namespace SideBar.Nearby.Session;

/// <summary>Non-secret facts about the loaded certificates, safe to print.</summary>
public sealed record CertificateSummary(
    int PrivateTotal,
    int PrivateValidNow,
    bool SelfShareCertSelected,
    double SelfShareHoursLeft,
    int SelfShareConsumedSalts,
    int PublicTotal,
    int PublicValidNow,
    int PublicOwnExcluded,
    IReadOnlyList<string> PeerNames);

/// <summary>Reads the official Quick Share app's certificates (read-only) and exposes them as an identity.</summary>
public sealed class OfficialCertIdentity : IShareIdentity
{
    // google/nearby accepts public certificates 30 minutes outside their window.
    private static readonly TimeSpan PublicTolerance = TimeSpan.FromMinutes(30);

    private readonly CertificateIdentity _inner;
    private readonly Dictionary<string, PublicCertificate> _peers;

    public CertificateSummary Summary { get; }

    private OfficialCertIdentity(
        CertificateIdentity inner, Dictionary<string, PublicCertificate> peers, CertificateSummary summary,
        byte[]? selfSpki = null)
    {
        _selfSpki = selfSpki;
        _inner = inner;
        _peers = peers;
        Summary = summary;
    }

    public static string DefaultSharingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Nearby", "Sharing");

    /// <summary>Loads the SELF_SHARE certificate and the public certificates of other devices.</summary>
    public static OfficialCertIdentity Load(string? sharingDirectory = null, DateTimeOffset? now = null)
    {
        var dir = sharingDirectory ?? DefaultSharingDirectory;
        var at = now ?? DateTimeOffset.UtcNow;

        List<PrivateCertificate> privates;
        using (var doc = JsonDocument.Parse(ReadShared(Path.Combine(dir, "preferences.json"))))
        {
            privates = doc.RootElement.GetProperty("nearby_sharing.private_certificate_list").EnumerateArray()
                .Select(PrivateCertificate.FromJson).ToList();
        }

        var publics = ReadPublicCertificates(Path.Combine(dir, "NearbySharePublicCertificateDatabase"));
        return Build(privates, publics, at);
    }

    internal static OfficialCertIdentity Build(
        IReadOnlyList<PrivateCertificate> privates, IReadOnlyList<PublicCertificate> publics, DateTimeOffset at)
    {
        var self = privates.FirstOrDefault(c => c.Visibility == SelfShareVisibility && at >= c.NotBefore && at < c.NotAfter);
        var ownIds = privates.Select(c => Convert.ToHexString(c.Id)).ToHashSet();

        var nowSeconds = at.ToUnixTimeSeconds();
        var tolerance = (long)PublicTolerance.TotalSeconds;
        var usable = publics
            .Where(c => c.IsStructurallyValid
                && nowSeconds >= c.StartSeconds - tolerance && nowSeconds < c.EndSeconds + tolerance)
            .ToList();
        var peers = usable.Where(c => !ownIds.Contains(Convert.ToHexString(c.SecretId)))
            .GroupBy(c => Convert.ToHexString(c.SecretId)).ToDictionary(g => g.Key, g => g.First());

        var names = peers.Values.Select(PeerNameFromField7).Where(n => n is not null).Select(n => n!).Distinct().ToList();
        var summary = new CertificateSummary(
            privates.Count, privates.Count(c => at >= c.NotBefore && at < c.NotAfter),
            self is not null, self is null ? 0 : (self.NotAfter - at).TotalHours,
            self?.ConsumedSalts.Count ?? 0,
            publics.Count, usable.Count, usable.Count - peers.Count, names);

        if (self is null)
            return new OfficialCertIdentity(
                new CertificateIdentity(new PrivateCertificateData([], [], []), []), peers, summary);

        var salts = self.ConsumedSalts.Select(s => (ushort)((s[0] << 8) | s[1])).ToHashSet();
        var inner = new CertificateIdentity(
            new PrivateCertificateData(self.SecretKey, self.MetadataEncryptionKey, self.KeyPairPkcs8, salts),
            peers.Values.Select(c => new PublicCertificateData(c.SecretId, c.SecretKey, c.PublicKey, c.KeyTag)).ToList());
        return new OfficialCertIdentity(inner, peers, summary, self.PublicKeySpki());
    }

    public bool HasSelfCertificate => Summary.SelfShareCertSelected;

    public EndpointInfo CreateEndpointInfo(string deviceName, DeviceKind kind) =>
        _inner.CreateEndpointInfo(deviceName, kind);

    public PairedKeyEncryptionFrame CreatePairedKeyEncryption(ReadOnlySpan<byte> authString, PairedKeyRole role) =>
        _inner.CreatePairedKeyEncryption(authString, role);

    public PairedKeyResultFrame.Types.Status VerifyPairedKey(
        PairedKeyEncryptionFrame remote, ReadOnlySpan<byte> authString, PairedKeyRole role) =>
        _inner.VerifyPairedKey(remote, authString, role);

    /// <summary>Device name of a peer advertising with one of our known certificates, or null when unknown.</summary>
    public string? TryGetPeerName(EndpointInfo info)
    {
        var cert = _inner.Identify(info);
        if (cert is null || !_peers.TryGetValue(Convert.ToHexString(cert.SecretId), out var full)) return null;
        var key = full.TryDecryptMetadataKey(info.Metadata[..2], info.Metadata[2..]);
        return key is null ? null : DeviceName(full.DecryptMetadata(key));
    }

    private const int SelfShareVisibility = 2;

    // Self-share certificates carry the metadata key in field 7, which lets us name a peer without an advertisement.
    private static string? PeerNameFromField7(PublicCertificate cert) =>
        cert.MetadataEncryptionKey.Length == 0 ? null : DeviceName(cert.DecryptMetadata(cert.MetadataEncryptionKey));

    // EncryptedMetadata: field 1 = device name (the only field printed; accounts and e-mails are never read).
    private static string? DeviceName(byte[]? metadata)
    {
        if (metadata is null) return null;
        try { return Proto.Bytes(Proto.Parse(metadata), 1) is { } name ? new UTF8Encoding(false, true).GetString(name) : null; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or ArgumentOutOfRangeException) { return null; }
    }

    private static List<PublicCertificate> ReadPublicCertificates(string databaseDir)
    {
        // The official app keeps the database locked: work on a copy outside the repository, delete it afterwards.
        var temp = Path.Combine(Path.GetTempPath(), "sidebar-certs-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(temp);
            foreach (var file in Directory.GetFiles(databaseDir))
            {
                if (Path.GetFileName(file) == "LOCK") continue;
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Create(Path.Combine(temp, Path.GetFileName(file)));
                input.CopyTo(output);
            }
            return LevelDb.Open(temp).Entries.Values.Select(PublicCertificate.Parse).ToList();
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private readonly byte[]? _selfSpki;

    /// <summary>Offline self-check of the own certificate: sign a random token and verify it with the own public key.</summary>
    public bool SelfTestSignature()
    {
        if (_selfSpki is null) return false;
        var token = RandomNumberGenerator.GetBytes(32);
        return VerifyWithSpki(_selfSpki, token, _inner.CreatePairedKeyEncryption(token, PairedKeyRole.Sender));
    }

    private static bool VerifyWithSpki(byte[] spki, byte[] token, PairedKeyEncryptionFrame frame)
    {
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(spki, out _);
        var padded = new byte[token.Length + 1];
        padded[0] = 0x01;
        token.CopyTo(padded, 1);
        return ec.VerifyData(padded, frame.SignedData.Span, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
