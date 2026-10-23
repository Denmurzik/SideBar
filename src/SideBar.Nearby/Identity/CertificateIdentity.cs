// Certificate crypto follows google/nearby sharing/certificates (Apache-2.0) and docs/research/certs.md.
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using SideBar.Nearby.Certs;
using SideBar.Nearby.Protocol.Sharing;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;

namespace SideBar.Nearby.Identity;

/// <summary>Our private certificate (from the official app's preferences.json), already decoded.</summary>
public sealed record PrivateCertificateData(
    byte[] SecretKey, byte[] MetadataEncryptionKey, byte[] KeyPairPkcs8, IReadOnlySet<ushort>? ConsumedSalts = null);

/// <summary>
/// A device's public certificate (from the official app's leveldb), already decoded.
/// <paramref name="EncryptedMetadata"/> (optional) lets <see cref="CertificateIdentity.TryGetPeerName"/> name the device.
/// </summary>
public sealed record PublicCertificateData(
    byte[] SecretId, byte[] SecretKey, byte[] PublicKeySpki, byte[] KeyTag, byte[]? EncryptedMetadata = null,
    byte[]? MetadataEncryptionKey = null);

/// <summary>
/// "Own devices" mode: advertises with an encrypted metadata key (without a device name), signs the UKEY2 auth token
/// with the certificate key and verifies the peer against known public certificates.
/// Loading the certificates is <see cref="OfficialCertificateStore"/>'s job.
/// </summary>
public sealed class CertificateIdentity(
    PrivateCertificateData own, IReadOnlyList<PublicCertificateData> peers)
    : IShareIdentity
{
    private const byte SenderPrefix = 0x01;
    private const byte ReceiverPrefix = 0x02;

    /// <summary>
    /// Endpoint info without a name (<see cref="EndpointInfo.Hidden"/>), as the own-devices advertisement of the official
    /// app: the receiver takes the name from our certificate metadata.
    /// </summary>
    public EndpointInfo CreateEndpointInfo(string deviceName, DeviceKind kind) => CreateEndpointInfo(deviceName, kind, named: false);

    /// <summary>
    /// <paramref name="named"/> true: the "Everyone" form with the device name (the metadata key is still present, so own
    /// devices can verify us). The salt is fresh on every call and never one of the consumed salts.
    /// </summary>
    public EndpointInfo CreateEndpointInfo(string deviceName, DeviceKind kind, bool named)
    {
        var salt = NewSalt();
        var encrypted = NearbyCrypto.Ctr(own.SecretKey, salt, own.MetadataEncryptionKey);
        return new EndpointInfo { Kind = kind, Hidden = !named, Name = named ? deviceName : null, Metadata = [.. salt, .. encrypted] };
    }

    private byte[] NewSalt()
    {
        var consumed = own.ConsumedSalts;
        var salt = RandomNumberGenerator.GetBytes(2);
        if (consumed is null || consumed.Count == 0) return salt;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            if (!consumed.Contains(ToSalt(salt))) return salt;
            salt = RandomNumberGenerator.GetBytes(2);
        }
        // Dense consumed set: scan from a random start so the result is still never a consumed salt.
        var start = ToSalt(salt);
        for (var i = 0; i < 65536; i++)
        {
            var candidate = (ushort)(start + i);
            if (!consumed.Contains(candidate)) return [(byte)(candidate >> 8), (byte)candidate];
        }
        return salt; // every salt is consumed: nothing better exists
    }

    private static ushort ToSalt(byte[] salt) => (ushort)((salt[0] << 8) | salt[1]);

    // google/nearby paired_key_verification_runner.cc: signed_data is made with OUR private certificate, while
    // secret_id_hash = HKDF(token, salt = PEER public certificate secret_key), the peer certificate being the one
    // that decrypts the peer's advertisement.
    public PairedKeyEncryptionFrame CreatePairedKeyEncryption(
        ReadOnlySpan<byte> authString, PairedKeyRole role, EndpointInfo? remote)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(own.KeyPairPkcs8, out _);
        var signature = ec.SignData(Padded(role, authString), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var peer = remote is null ? null : FindPeer(remote);
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
        var peer = remote is null ? null : FindPeer(remote);
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

    /// <summary>True when one of the known public certificates decrypts the advertised metadata key.</summary>
    public bool Identify(EndpointInfo info) => FindPeer(info) is not null;

    /// <summary>
    /// Device name (certificate metadata field 1) of a peer advertising with one of our known certificates;
    /// null when the peer is unknown or the certificate carries no name.
    /// </summary>
    public string? TryGetPeerName(EndpointInfo info)
    {
        var cert = FindPeer(info);
        if (cert?.EncryptedMetadata is not { Length: >= 16 } encrypted) return null;
        var key = NearbyCrypto.Ctr(cert.SecretKey, info.Metadata[..2], info.Metadata[2..]);
        return DeviceName(DecryptMetadata(cert.SecretKey, key, encrypted));
    }

    /// <summary>Stable device identifier (same <see cref="Key"/> across mDNS, BLE presence and <see cref="FindBluetoothTargets"/>).</summary>
    public sealed record BluetoothTarget(string Key, string? DeviceName, byte[] Mac, EndpointInfo RemoteInfo);

    /// <summary>
    /// Peer certificates that advertise a Bluetooth MAC (certificate metadata field 4), one entry per MAC. Lets Task 2
    /// connect over Bluetooth to a device that is not reachable over Wi-Fi LAN (locked screen, no mDNS).
    /// </summary>
    public IReadOnlyList<BluetoothTarget> FindBluetoothTargets()
    {
        var byMac = new Dictionary<string, BluetoothTarget>();
        foreach (var cert in peers)
        {
            if (cert.MetadataEncryptionKey is not { Length: > 0 } metadataKey) continue;
            if (cert.EncryptedMetadata is not { Length: >= 16 } encrypted) continue;
            if (DecryptMetadata(cert.SecretKey, metadataKey, encrypted) is not { } metadata) continue;
            if (ProtoReader.Bytes(ProtoReader.Parse(metadata), 4) is not { Length: 6 } mac) continue;

            var macKey = Convert.ToHexString(mac);
            if (byMac.ContainsKey(macKey)) continue;

            var salt = RandomNumberGenerator.GetBytes(2);
            var remoteInfo = new EndpointInfo { Metadata = [.. salt, .. NearbyCrypto.Ctr(cert.SecretKey, salt, metadataKey)] };
            byMac[macKey] = new BluetoothTarget(Convert.ToHexString(cert.SecretId), DeviceName(metadata), mac, remoteInfo);
        }
        return byMac.Values.ToList();
    }

    /// <summary>Stable identifier of a peer advertising with one of our known certificates, for dedup across transports.</summary>
    public string? TryGetPeerKey(EndpointInfo info) => FindPeer(info) is { } cert ? Convert.ToHexString(cert.SecretId) : null;

    // EncryptedMetadata: field 1 = device name (the only field ever read; accounts and e-mails are not touched).
    internal static string? DeviceName(byte[]? metadata)
    {
        if (metadata is null) return null;
        try
        {
            return ProtoReader.Bytes(ProtoReader.Parse(metadata), 1) is { } name
                ? new UTF8Encoding(false, true).GetString(name)
                : null;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { return null; }
    }

    internal static byte[]? DecryptMetadata(byte[] secretKey, byte[] metadataKey, byte[] encrypted)
    {
        if (encrypted.Length < 16) return null;
        var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, metadataKey, 32, salt: [], info: []);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, 12, salt: [], info: []);
        var ct = encrypted.AsSpan(0, encrypted.Length - 16);
        var tag = encrypted.AsSpan(encrypted.Length - 16);
        var pt = new byte[ct.Length];
        try
        {
            using var gcm = new AesGcm(aesKey, 16);
            gcm.Decrypt(nonce, ct, tag, pt);
            return pt;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Offline self-check of the own certificate: sign a random token and verify it with the own public key.</summary>
    public bool SelfTestSignature()
    {
        try
        {
            var token = RandomNumberGenerator.GetBytes(32);
            var frame = CreatePairedKeyEncryption(token, PairedKeyRole.Sender, null);
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(own.KeyPairPkcs8, out _);
            return ec.VerifyData(Padded(PairedKeyRole.Sender, token), frame.SignedData.Span,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }

    // Finds the public certificate whose key decrypts the advertised metadata key (null: unknown device).
    private PublicCertificateData? FindPeer(EndpointInfo info)
    {
        if (info.Metadata.Length != EndpointInfo.MetadataLength) return null;
        var salt = info.Metadata[..2];
        var encrypted = info.Metadata[2..];
        foreach (var cert in peers)
        {
            var key = NearbyCrypto.Ctr(cert.SecretKey, salt, encrypted);
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
}
