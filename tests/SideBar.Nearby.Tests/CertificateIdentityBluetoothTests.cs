using System.Security.Cryptography;
using SideBar.Nearby.Identity;
using Xunit;

namespace SideBar.Nearby.Tests;

public class CertificateIdentityBluetoothTests
{
    private static byte[] NewOwnKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ec.ExportPkcs8PrivateKey();
    }

    private static PublicCertificateData ToPublicCertificateData(CertFixtures.Device device) => new(
        device.Public.SecretId, device.Public.SecretKey, device.Public.PublicKey,
        device.Public.KeyTag, device.Public.EncryptedMetadata, device.Public.MetadataEncryptionKey);

    [Fact]
    public void FindBluetoothTargets_reads_mac_and_dedupes_same_mac()
    {
        var mac = Convert.FromHexString("AABBCCDDEEFF");
        var device = CertFixtures.NewDevice("Pixel 9", bluetoothMac: mac);
        var identity = new CertificateIdentity(
            new PrivateCertificateData(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(14), NewOwnKeyPair()),
            [ToPublicCertificateData(device)]);

        var targets = identity.FindBluetoothTargets();

        Assert.Single(targets);
        Assert.Equal(mac, targets[0].Mac);
        Assert.Equal("Pixel 9", targets[0].DeviceName);
        Assert.True(identity.Identify(targets[0].RemoteInfo));
        Assert.Equal(targets[0].Key, identity.TryGetPeerKey(targets[0].RemoteInfo));
    }

    [Fact]
    public void FindBluetoothTargets_dedupes_two_certs_with_the_same_mac()
    {
        var mac = Convert.FromHexString("112233445566");
        var deviceA = CertFixtures.NewDevice("Pixel 9 A", bluetoothMac: mac);
        var deviceB = CertFixtures.NewDevice("Pixel 9 B", bluetoothMac: mac);
        var identity = new CertificateIdentity(
            new PrivateCertificateData(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(14), NewOwnKeyPair()),
            [ToPublicCertificateData(deviceA), ToPublicCertificateData(deviceB)]);

        var targets = identity.FindBluetoothTargets();

        Assert.Single(targets);
        Assert.Equal(mac, targets[0].Mac);
    }

    private static CertificateIdentity NewIdentity(params PublicCertificateData[] peers) => new(
        new PrivateCertificateData(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(14), NewOwnKeyPair()),
        peers);

    [Fact]
    public void TryGetBluetoothTarget_uses_the_certificate_that_was_actually_advertised()
    {
        // E3 (docs/research/ble-locked-phone.md): one phone, several valid certificates at once, one MAC.
        var mac = Convert.FromHexString("0A1B2C3D4E5F");
        var first = CertFixtures.NewDevice("Pixel 9", bluetoothMac: mac);
        var second = CertFixtures.NewDevice("Pixel 9", bluetoothMac: mac);
        var identity = NewIdentity(ToPublicCertificateData(first), ToPublicCertificateData(second));
        // What the phone advertises right now: an endpoint info made with the SECOND certificate.
        var observed = NewIdentity(ToPublicCertificateData(second)).FindBluetoothTargets()[0].RemoteInfo;

        var target = identity.TryGetBluetoothTarget(observed);

        Assert.NotNull(target);
        Assert.Equal(Convert.ToHexString(second.Public.SecretId), target.Key);
        Assert.NotEqual(Convert.ToHexString(first.Public.SecretId), target.Key);
        Assert.Equal(identity.TryGetPeerKey(observed), target.Key);
        Assert.Equal(mac, target.Mac);
        Assert.Equal("Pixel 9", target.DeviceName);
        Assert.Equal(target.Key, identity.TryGetPeerKey(target.RemoteInfo));
    }

    [Fact]
    public void TryGetBluetoothTarget_returns_null_for_an_unknown_advertiser()
    {
        var identity = NewIdentity(ToPublicCertificateData(CertFixtures.NewDevice("Pixel 9", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF"))));
        var stranger = NewIdentity(ToPublicCertificateData(CertFixtures.NewDevice("Other", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF"))))
            .FindBluetoothTargets()[0].RemoteInfo;

        Assert.Null(identity.TryGetBluetoothTarget(stranger));
    }

    [Fact]
    public void A_name_that_is_not_utf8_does_not_cost_the_mac()
    {
        var mac = Convert.FromHexString("C0FFEE123456");
        byte[] plain = [0x0A, 2, 0xC3, 0x28, (4 << 3) | 2, 6, .. mac];   // C3 28: invalid UTF-8 sequence
        var cert = NewCertWithMetadata(plain);
        var identity = NewIdentity(cert);

        var targets = identity.FindBluetoothTargets();

        Assert.Single(targets);
        Assert.Equal(mac, targets[0].Mac);
        Assert.Null(targets[0].DeviceName);
        var observed = identity.TryGetBluetoothTarget(targets[0].RemoteInfo);
        Assert.NotNull(observed);
        Assert.Equal(mac, observed.Mac);
        Assert.Null(observed.DeviceName);
    }

    // The AEAD tag verifies (so DecryptMetadata succeeds) but the plaintext is not valid protobuf: wire type 6 does
    // not exist, so ProtoReader.Parse throws InvalidDataException. One such certificate must not stop the search.
    private static PublicCertificateData NewCertWithUnparsableMetadata() => NewCertWithMetadata([0x0E]);

    private static PublicCertificateData NewCertWithMetadata(byte[] plain)
    {
        var secretKey = RandomNumberGenerator.GetBytes(32);
        var metadataKey = RandomNumberGenerator.GetBytes(14);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, metadataKey, 32, [], []);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, 12, [], []);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(aesKey, 16)) gcm.Encrypt(nonce, plain, cipher, tag);
        return new PublicCertificateData(
            SHA256.HashData(secretKey), secretKey, ec.ExportSubjectPublicKeyInfo(),
            HMACSHA256.HashData(new byte[32], metadataKey), [.. cipher, .. tag], metadataKey);
    }

    [Fact]
    public void FindBluetoothTargets_skips_a_certificate_with_unparsable_metadata_without_failing_others()
    {
        var mac = Convert.FromHexString("AA00BB11CC22");
        var good = CertFixtures.NewDevice("Good Device", bluetoothMac: mac);
        var corrupt = NewCertWithUnparsableMetadata();
        var identity = new CertificateIdentity(
            new PrivateCertificateData(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(14), NewOwnKeyPair()),
            [corrupt, ToPublicCertificateData(good)]);

        var targets = identity.FindBluetoothTargets();

        Assert.Single(targets);
        Assert.Equal(mac, targets[0].Mac);
    }
}
