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

    // The AEAD tag verifies (so DecryptMetadata succeeds) but the plaintext is not valid protobuf: wire type 6 does
    // not exist, so ProtoReader.Parse throws InvalidDataException. One such certificate must not stop the search.
    private static PublicCertificateData NewCertWithUnparsableMetadata()
    {
        var secretKey = RandomNumberGenerator.GetBytes(32);
        var metadataKey = RandomNumberGenerator.GetBytes(14);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] plain = [0x0E];
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
