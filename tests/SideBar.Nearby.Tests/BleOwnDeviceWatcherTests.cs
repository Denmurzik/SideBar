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

        var presence = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData);

        Assert.NotNull(presence);
        Assert.Equal(target.Key, presence.Key);
        Assert.Equal(target.RemoteInfo.Metadata, presence.Info.Metadata);
        Assert.Equal("Pixel 9", identity.TryGetBluetoothTarget(presence.Info)?.DeviceName);
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

        var presence = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData);

        Assert.NotNull(presence);
        Assert.Equal(target.Key, presence.Key);
    }

    [Fact]
    public void Presence_of_the_second_of_two_same_mac_certificates_resolves_to_that_certificate()
    {
        // E3: the phone holds several valid certificates and advertises one of them, not necessarily the first.
        var mac = Convert.FromHexString("0A1B2C3D4E5F");
        var first = CertFixtures.NewDevice("Pixel 9", bluetoothMac: mac);
        var second = CertFixtures.NewDevice("Pixel 9", bluetoothMac: mac);
        var identity = NewIdentity(first, second);
        var advertised = NewIdentity(second).FindBluetoothTargets()[0].RemoteInfo;
        byte[] serviceData = [.. Uuid(0xFEF3), 0x00, .. advertised.Metadata];

        var presence = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData);
        var target = identity.TryGetBluetoothTarget(presence!.Info);

        Assert.Equal(Convert.ToHexString(second.Public.SecretId), presence.Key);
        Assert.NotNull(target);
        Assert.Equal(presence.Key, target.Key);
        Assert.Equal(mac, target.Mac);
    }

    [Fact]
    public void TryParseAdvertisement_ignores_a_different_service_uuid()
    {
        var device = CertFixtures.NewDevice("Pixel 9", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF"));
        var identity = NewIdentity(device);
        var target = identity.FindBluetoothTargets()[0];
        byte[] serviceData = [.. Uuid(0xFE2C), 0x00, .. target.RemoteInfo.Metadata];   // Fast Init, не Nearby Connections

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData);

        Assert.Null(found);
    }

    [Fact]
    public void TryParseAdvertisement_ignores_an_unknown_device()
    {
        var identity = NewIdentity(CertFixtures.NewDevice("Other", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF")));
        byte[] serviceData = [.. Uuid(0xFEF3), .. RandomNumberGenerator.GetBytes(27)];   // len=27, как в E2, но чужое тело

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData);

        Assert.Null(found);
    }

    [Fact]
    public void TryParseAdvertisement_rejects_a_body_too_short_for_any_offset()
    {
        var identity = NewIdentity(CertFixtures.NewDevice("Pixel 9", bluetoothMac: Convert.FromHexString("AABBCCDDEEFF")));
        byte[] serviceData = [.. Uuid(0xFEF3), .. new byte[16]];   // body.Length = 16, нужно минимум 17

        var found = BleOwnDeviceWatcher.TryParseAdvertisement(identity, serviceData);

        Assert.Null(found);
    }
}
