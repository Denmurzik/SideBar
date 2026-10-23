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
/// <paramref name="EncryptedMetadata"/> (optional) lets <see cref="CertificateIdentity.TryGetPeerName"/> name the device
/// and <see cref="CertificateIdentity.TryGetBluetoothTarget"/> read its Bluetooth MAC from an observed advertisement.
/// <paramref name="MetadataEncryptionKey"/> (optional) decrypts that metadata without any advertisement, so
/// <see cref="CertificateIdentity.FindBluetoothTargets"/> can list the device and make an endpoint info for it.
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
    /// Peer certificates that advertise a Bluetooth MAC (certificate metadata field 4), one entry per MAC (the first
    /// certificate wins, which is not necessarily the one the device advertises now: to connect to a device that was
    /// actually seen, use <see cref="TryGetBluetoothTarget"/>).
    /// </summary>
    public IReadOnlyList<BluetoothTarget> FindBluetoothTargets()
    {
        var byMac = new Dictionary<string, BluetoothTarget>();
        foreach (var cert in peers)
        {
            if (cert.MetadataEncryptionKey is not { Length: > 0 } metadataKey) continue;
            var salt = RandomNumberGenerator.GetBytes(2);
            var remoteInfo = new EndpointInfo { Metadata = [.. salt, .. NearbyCrypto.Ctr(cert.SecretKey, salt, metadataKey)] };
            if (TargetOf(cert, metadataKey, remoteInfo) is { } target) byMac.TryAdd(Convert.ToHexString(target.Mac), target);
        }
        return byMac.Values.ToList();
    }

    /// <summary>
    /// Bluetooth target of the device that advertised <paramref name="observed"/> (mDNS or BLE), built from the very
    /// certificate that decrypts that advertisement. A phone holds several valid certificates at once (E3 in
    /// docs/research/ble-locked-phone.md) and the PairedKey exchange must use the one it advertises now.
    /// Null when the advertiser is unknown or its certificate carries no 6-byte MAC.
    /// </summary>
    public BluetoothTarget? TryGetBluetoothTarget(EndpointInfo observed)
    {
        if (FindPeer(observed) is not { } cert) return null;
        var metadataKey = NearbyCrypto.Ctr(cert.SecretKey, observed.Metadata[..2], observed.Metadata[2..]);
        return TargetOf(cert, metadataKey, observed);
    }

    private static BluetoothTarget? TargetOf(PublicCertificateData cert, byte[] metadataKey, EndpointInfo remoteInfo)
    {
        if (cert.EncryptedMetadata is not { Length: >= 16 } encrypted) return null;
        if (DecryptMetadata(cert.SecretKey, metadataKey, encrypted) is not { } metadata) return null;
        if (!TryReadMetadataFields(metadata, out var mac, out var name) || mac is not { Length: 6 }) return null;
        return new BluetoothTarget(Convert.ToHexString(cert.SecretId), name, mac, remoteInfo);
    }

    /// <summary>Stable identifier of a peer advertising with one of our known certificates, for dedup across transports.</summary>
    public string? TryGetPeerKey(EndpointInfo info) => FindPeer(info) is { } cert ? Convert.ToHexString(cert.SecretId) : null;

    // EncryptedMetadata: field 1 = device name, field 4 = Bluetooth MAC (the only fields ever read; accounts and
    // e-mails are not touched).
    internal static string? DeviceName(byte[]? metadata) =>
        metadata is not null && TryReadMetadataFields(metadata, out _, out var name) ? name : null;

    // Parses EncryptedMetadata once and reads both fields callers care about; malformed protobuf makes the certificate
    // unreadable (not an error for the caller to propagate), a non-UTF8 name costs only the name, not the MAC.
    private static bool TryReadMetadataFields(byte[] metadata, out byte[]? mac, out string? name)
    {
        mac = null;
        name = null;
        byte[]? rawName;
        try
        {
            var fields = ProtoReader.Parse(metadata);
            mac = ProtoReader.Bytes(fields, 4);
            rawName = ProtoReader.Bytes(fields, 1);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { return false; }
        try { name = rawName is null ? null : new UTF8Encoding(false, true).GetString(rawName); }
        catch (DecoderFallbackException) { }
        return true;
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
