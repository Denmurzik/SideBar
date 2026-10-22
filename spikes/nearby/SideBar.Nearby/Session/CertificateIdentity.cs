// Certificate crypto follows google/nearby sharing/certificates (Apache-2.0) and docs/research/certs.md.
using System.Security.Cryptography;
using Google.Protobuf;
using SideBar.Nearby.Protocol.Sharing;
using SideBar.Nearby.Wire;

namespace SideBar.Nearby.Session;

/// <summary>Our private certificate (from the official app's preferences.json), already decoded.</summary>
public sealed record PrivateCertificateData(
    byte[] SecretKey, byte[] MetadataEncryptionKey, byte[] KeyPairPkcs8, IReadOnlySet<ushort>? ConsumedSalts = null);

/// <summary>A device's public certificate (from the official app's leveldb), already decoded.</summary>
public sealed record PublicCertificateData(byte[] SecretId, byte[] SecretKey, byte[] PublicKeySpki, byte[] KeyTag);

/// <summary>
/// "Own devices" mode: advertises with an encrypted metadata key, signs the UKEY2 auth token with the certificate key
/// and verifies the peer against known public certificates. Loading the certificates is the caller's job.
/// </summary>
public sealed class CertificateIdentity(
    PrivateCertificateData own, IReadOnlyList<PublicCertificateData> peers)
    : IShareIdentity
{
    private const byte SenderPrefix = 0x01;
    private const byte ReceiverPrefix = 0x02;

    public EndpointInfo CreateEndpointInfo(string deviceName, DeviceKind kind)
    {
        byte[] salt;
        do salt = RandomNumberGenerator.GetBytes(2);
        while (own.ConsumedSalts?.Contains((ushort)((salt[0] << 8) | salt[1])) == true);
        var encrypted = AesCtr(own.SecretKey, salt, own.MetadataEncryptionKey);
        return new EndpointInfo { Kind = kind, Name = deviceName, Metadata = [.. salt, .. encrypted] };
    }

    /// <summary>Diagnostics sink: counts and booleans only, never key material.</summary>
    public Action<string>? Diag { get; set; }

    // google/nearby paired_key_verification_runner.cc: signed_data is made with OUR private certificate, while
    // secret_id_hash = HKDF(token, salt = PEER public certificate secret_key), the peer certificate being the one
    // that decrypts the peer's advertisement.
    public PairedKeyEncryptionFrame CreatePairedKeyEncryption(
        ReadOnlySpan<byte> authString, PairedKeyRole role, EndpointInfo? remote)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(own.KeyPairPkcs8, out _);
        var signature = ec.SignData(Padded(role, authString), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var peer = remote is null ? null : Identify(remote);
        Diag?.Invoke($"diag: create role={role} peerCertificateKnown={peer is not null}");
        return new PairedKeyEncryptionFrame
        {
            SignedData = ByteString.CopyFrom(signature),
            SecretIdHash = ByteString.CopyFrom(peer is null
                ? RandomNumberGenerator.GetBytes(6)
                : HashToken(authString, peer.SecretKey)),
        };
    }

    public PairedKeyResultFrame.Types.Status VerifyPairedKey(
        PairedKeyEncryptionFrame frame, ReadOnlySpan<byte> authString, PairedKeyRole role, EndpointInfo? remote)
    {
        var theirRole = role == PairedKeyRole.Sender ? PairedKeyRole.Receiver : PairedKeyRole.Sender;
        var peer = remote is null ? null : Identify(remote);
        if (Diag is not null) Diagnose(frame, authString, theirRole, peer);
        if (peer is null) return PairedKeyResultFrame.Types.Status.Unable;

        var padded = Padded(theirRole, authString);
        if (VerifyWith(peer, padded, frame.SignedData.Span)
            || (frame.HasOptionalSignedData && VerifyWith(peer, padded, frame.OptionalSignedData.Span)))
            return PairedKeyResultFrame.Types.Status.Success;
        return PairedKeyResultFrame.Types.Status.Fail;
    }

    private static bool VerifyWith(PublicCertificateData cert, byte[] padded, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(cert.PublicKeySpki, out _);
            return ec.VerifyData(padded, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }

    private void Diagnose(
        PairedKeyEncryptionFrame frame, ReadOnlySpan<byte> authString, PairedKeyRole theirRole, PublicCertificateData? peer)
    {
        var token = authString.ToArray();
        var hashOwn = CryptographicOperations.FixedTimeEquals(HashToken(token, own.SecretKey), frame.SecretIdHash.Span);
        var hashPeers = peers.Count(c => CryptographicOperations.FixedTimeEquals(HashToken(token, c.SecretKey), frame.SecretIdHash.Span));
        Diag!($"diag: hash==ownPrivateCert={hashOwn} hash matches {hashPeers}/{peers.Count} public certs; " +
              $"peerIdentifiedByAdvertisement={peer is not null}; optionalSignedData={frame.HasOptionalSignedData}");

        byte[] Raw() => token;
        foreach (var (label, padded) in new (string, byte[])[]
                 {
                     ("prefix 0x01", Padded(PairedKeyRole.Sender, token)),
                     ("prefix 0x02", Padded(PairedKeyRole.Receiver, token)),
                     ("raw token", Raw()),
                 })
        {
            var signed = peers.Count(c => VerifyWith(c, padded, frame.SignedData.Span));
            var optional = frame.HasOptionalSignedData ? peers.Count(c => VerifyWith(c, padded, frame.OptionalSignedData.Span)) : 0;
            Diag!($"diag: {label}: signedData verifies with {signed}/{peers.Count} peer certs, optionalSignedData with {optional}" +
                  $"{(peer is not null ? $"; with identified peer: {VerifyWith(peer, padded, frame.SignedData.Span)}" : "")}");
        }
        Diag!($"diag: expected prefix for the peer = 0x{(theirRole == PairedKeyRole.Sender ? 1 : 2):x2}");
    }

    /// <summary>Finds the public certificate whose key decrypts the advertised metadata key (null: unknown device).</summary>
    public PublicCertificateData? Identify(EndpointInfo info)
    {
        if (info.Metadata.Length != EndpointInfo.MetadataLength) return null;
        var salt = info.Metadata[..2];
        var encrypted = info.Metadata[2..];
        foreach (var cert in peers)
        {
            var key = AesCtr(cert.SecretKey, salt, encrypted);
            if (CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(new byte[32], key), cert.KeyTag))
                return cert;
        }
        return null;
    }

    private static byte[] Padded(PairedKeyRole role, ReadOnlySpan<byte> authString)
    {
        var data = new byte[1 + authString.Length];
        data[0] = role == PairedKeyRole.Sender ? SenderPrefix : ReceiverPrefix;
        authString.CopyTo(data.AsSpan(1));
        return data;
    }

    // HKDF-SHA256(ikm = raw token, salt = certificate secret key, info = empty) truncated to 6 bytes.
    private static byte[] HashToken(ReadOnlySpan<byte> authString, byte[] secretKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, authString.ToArray(), 6, secretKey, []);

    // AES-256-CTR with counter block = HKDF-SHA256(salt, no salt, no info, 16 bytes).
    internal static byte[] AesCtr(byte[] secretKey, byte[] salt, byte[] data)
    {
        var counter = HKDF.DeriveKey(HashAlgorithmName.SHA256, salt, 16, [], []);
        using var aes = Aes.Create();
        aes.Key = secretKey;
        var output = new byte[data.Length];
        var keystream = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            var n = Math.Min(16, data.Length - offset);
            for (var i = 0; i < n; i++) output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        return output;
    }
}
