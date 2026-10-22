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

    public PairedKeyEncryptionFrame CreatePairedKeyEncryption(ReadOnlySpan<byte> authString, PairedKeyRole role)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(own.KeyPairPkcs8, out _);
        var signature = ec.SignData(Padded(role, authString), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new PairedKeyEncryptionFrame
        {
            SignedData = ByteString.CopyFrom(signature),
            SecretIdHash = ByteString.CopyFrom(HashToken(authString, own.SecretKey)),
        };
    }

    public PairedKeyResultFrame.Types.Status VerifyPairedKey(
        PairedKeyEncryptionFrame remote, ReadOnlySpan<byte> authString, PairedKeyRole role)
    {
        var theirRole = role == PairedKeyRole.Sender ? PairedKeyRole.Receiver : PairedKeyRole.Sender;
        foreach (var cert in peers)
        {
            if (!CryptographicOperations.FixedTimeEquals(HashToken(authString, cert.SecretKey), remote.SecretIdHash.Span))
                continue;
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(cert.PublicKeySpki, out _);
                return ec.VerifyData(Padded(theirRole, authString), remote.SignedData.Span, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence)
                    ? PairedKeyResultFrame.Types.Status.Success
                    : PairedKeyResultFrame.Types.Status.Fail;
            }
            catch (CryptographicException) { return PairedKeyResultFrame.Types.Status.Fail; }
        }
        return PairedKeyResultFrame.Types.Status.Unable;
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
