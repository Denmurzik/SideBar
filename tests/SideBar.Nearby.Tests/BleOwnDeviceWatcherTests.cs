using System.Security.Cryptography;
using SideBar.Nearby.Discovery;
using SideBar.Nearby.Identity;
using Xunit;

namespace SideBar.Nearby.Tests;

public class BleOwnDeviceWatcherTests
{
    private static byte[] NewOwnKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ec.ExportPkcs8PrivateKey();
    }

    private static PublicCertificateData ToPublicCertificateData(CertFixtures.Device device) => new(
        device.Public.SecretId, device.Public.SecretKey, device.Public.PublicKey,
        device.Public.KeyTag, device.Public.EncryptedMetadata, device.Public.MetadataEncryptionKey);

    private static CertificateIdentity NewIdentity(params CertFixtures.Device[] peers) => new(
        new PrivateCertificateData(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(14), NewOwnKeyPair()),
        peers.Select(ToPublicCertificateData).ToList());

    // Little-endian, как читает TryParseAdvertisement (и spikes/ble watch).
    private static byte[] Uuid(ushort value) => [(byte)value, (byte)(value >> 8)];

    [Fact]
    public void TryParseAdvertisement_finds_own_device_at_offset_zero()
    {
        var device = CertFixtures.NewDevice("Pixel 9", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF"));
        var identity = NewIdentity(device);
        var target = identity.FindBluetoothTargets()[0];
        // 1 ведущий байт обёртки (игнорируется), затем ровно 16-байтовое Metadata на смещении 0.
        byte[] serviceData = [.. Uuid(0xFEF3), 0x00, .. target.RemoteInfo.Metadata];

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData, out var key, out var name);

        Assert.True(found);
        Assert.Equal(target.Key, key);
        Assert.Equal("Pixel 9", name);
    }

    [Fact]
    public void TryParseAdvertisement_finds_own_device_behind_a_wrapper_prefix()
    {
        // E2 (docs/research/ble-locked-phone.md §2.2): на реальном Pixel 9 endpoint info лежало со смещением 8.
        var device = CertFixtures.NewDevice("Pixel 9", bluetoothMac: Convert.FromHexString("112233445566"));
        var identity = NewIdentity(device);
        var target = identity.FindBluetoothTargets()[0];
        byte[] wrapper = RandomNumberGenerator.GetBytes(8);
        byte[] serviceData = [.. Uuid(0xFEF3), .. wrapper, 0x00, .. target.RemoteInfo.Metadata];

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData, out var key, out _);

        Assert.True(found);
        Assert.Equal(target.Key, key);
    }

    [Fact]
    public void TryParseAdvertisement_ignores_a_different_service_uuid()
    {
        var device = CertFixtures.NewDevice("Pixel 9", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF"));
        var identity = NewIdentity(device);
        var target = identity.FindBluetoothTargets()[0];
        byte[] serviceData = [.. Uuid(0xFE2C), 0x00, .. target.RemoteInfo.Metadata];   // Fast Init, не Nearby Connections

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData, out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryParseAdvertisement_ignores_an_unknown_device()
    {
        var identity = NewIdentity(CertFixtures.NewDevice("Other", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF")));
        byte[] serviceData = [.. Uuid(0xFEF3), .. RandomNumberGenerator.GetBytes(27)];   // len=27, как в E2, но чужое тело

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData, out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryParseAdvertisement_rejects_a_body_too_short_for_any_offset()
    {
        var identity = NewIdentity(CertFixtures.NewDevice("Pixel 9", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF")));
        byte[] serviceData = [.. Uuid(0xFEF3), .. new byte[16]];   // body.Length = 16, нужно минимум 17

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData, out _, out _);

        Assert.False(found);
    }
}
