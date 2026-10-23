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
}
