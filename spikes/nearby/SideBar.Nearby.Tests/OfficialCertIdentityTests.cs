using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SideBar.Certs;
using PublicCertificate = SideBar.Certs.PublicCertificate;
using SideBar.Nearby.Protocol.Sharing;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;
using Xunit;

namespace SideBar.Nearby.Tests;

/// <summary>Synthetic certificates only; nothing here touches the real official-app data.</summary>
public class OfficialCertIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed record Device(PrivateCertificate Private, PublicCertificate Public);

    private static Device NewDevice(string name, int visibility = 2, TimeSpan? offset = null)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var metadataKey = RandomNumberGenerator.GetBytes(14);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var start = Now + (offset ?? TimeSpan.Zero) - TimeSpan.FromHours(1);
        var end = start + TimeSpan.FromHours(72);

        // EncryptedMetadata message with only field 1 (device name).
        var nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] plain = [0x0A, (byte)nameBytes.Length, .. nameBytes];

        var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, metadataKey, 32, [], []);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 12, [], []);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(aesKey, 16)) gcm.Encrypt(nonce, plain, cipher, tag);

        var id = SHA256.HashData(secret);
        return new Device(
            new PrivateCertificate
            {
                Visibility = visibility, NotBefore = start, NotAfter = end,
                KeyPairPkcs8 = ec.ExportPkcs8PrivateKey(), SecretKey = secret, MetadataEncryptionKey = metadataKey, Id = id,
            },
            new PublicCertificate
            {
                SecretId = id, SecretKey = secret, PublicKey = ec.ExportSubjectPublicKeyInfo(),
                StartSeconds = start.ToUnixTimeSeconds(), EndSeconds = end.ToUnixTimeSeconds(),
                MetadataEncryptionKey = metadataKey, EncryptedMetadata = [.. cipher, .. tag],
                KeyTag = HMACSHA256.HashData(new byte[32], metadataKey), ForSelfShare = true,
            });
    }

    private static OfficialCertIdentity For(Device self, params Device[] peers) =>
        OfficialCertIdentity.Build([self.Private], [self.Public, .. peers.Select(p => p.Public)], Now);

    [Fact]
    public void SummaryCountsAndNamesPeers()
    {
        var identity = For(NewDevice("Den PC"), NewDevice("Pixel 9"));

        var s = identity.Summary;
        Assert.True(s.SelfShareCertSelected);
        Assert.Equal(1, s.PrivateValidNow);
        Assert.Equal(2, s.PublicTotal);
        Assert.Equal(1, s.PublicOwnExcluded);
        Assert.Equal(["Pixel 9"], s.PeerNames);
        Assert.True(identity.SelfTestSignature());
    }

    [Fact]
    public void ExpiredOrWrongVisibilityCertificateIsNotSelected()
    {
        var expired = NewDevice("PC", offset: TimeSpan.FromDays(-10));
        Assert.False(OfficialCertIdentity.Build([expired.Private], [], Now).HasSelfCertificate);

        var contacts = NewDevice("PC", visibility: 3);
        Assert.False(OfficialCertIdentity.Build([contacts.Private], [], Now).HasSelfCertificate);
    }

    [Fact]
    public void ExpiredPublicCertificatesAreIgnored()
    {
        var oldPhone = NewDevice("Old phone", offset: TimeSpan.FromDays(-10));
        Assert.Empty(For(NewDevice("PC"), oldPhone).Summary.PeerNames);
    }

    [Fact]
    public void PeerAdvertisementIsRecognisedAndNamed()
    {
        var pc = NewDevice("Den PC");
        var phone = NewDevice("Pixel 9");
        var pcSide = For(pc, phone);
        var phoneSide = For(phone, pc);

        var info = phoneSide.CreateEndpointInfo("whatever", DeviceKind.Phone);

        Assert.Equal("Pixel 9", pcSide.TryGetPeerName(info));
        Assert.Null(pcSide.TryGetPeerName(EndpointInfo.CreateAnonymous("x", DeviceKind.Phone)));
    }

    [Fact]
    public void PairedKeyIsVerifiedBetweenTwoCertificateIdentities()
    {
        var pc = NewDevice("PC");
        var phone = NewDevice("Phone");
        var pcSide = For(pc, phone);
        var phoneSide = For(phone, pc);
        var token = RandomNumberGenerator.GetBytes(32);

        Assert.Equal(PairedKeyResultFrame.Types.Status.Success,
            phoneSide.VerifyPairedKey(pcSide.CreatePairedKeyEncryption(token, PairedKeyRole.Sender), token, PairedKeyRole.Receiver));
        Assert.Equal(PairedKeyResultFrame.Types.Status.Success,
            pcSide.VerifyPairedKey(phoneSide.CreatePairedKeyEncryption(token, PairedKeyRole.Receiver), token, PairedKeyRole.Sender));
    }

    [Fact]
    public void ConsumedSaltsAreNotReused()
    {
        var pc = NewDevice("PC");
        // Everything except salt 0xABCD is consumed.
        pc.Private.ConsumedSalts = Enumerable.Range(0, 65536).Where(i => i != 0xABCD)
            .Select(i => new[] { (byte)(i >> 8), (byte)i }).ToList();

        var info = For(pc).CreateEndpointInfo("PC", DeviceKind.Laptop);

        Assert.Equal(new byte[] { 0xAB, 0xCD }, info.Metadata[..2]);
    }

    [Fact]
    public async Task FullTransferBetweenOfficialIdentities()
    {
        var pc = NewDevice("PC");
        var phone = NewDevice("Phone");
        var dir = Path.Combine(Path.GetTempPath(), "sidebar-nearby-off-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "f.bin");
            File.WriteAllBytes(file, [7, 7, 7]);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var receive = Task.Run(async () =>
            {
                using var c = await listener.AcceptTcpClientAsync(cts.Token);
                return await ReceiveSession.ReceiveAsync(c.GetStream(), new SessionOptions("R", Identity: For(phone, pc)),
                    Path.Combine(dir, "out"), (_, _) => Task.FromResult(true), null, cts.Token);
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cts.Token);
            var send = await SendSession.SendFilesAsync(client.GetStream(),
                new SessionOptions("S", Identity: For(pc, phone)), [file], null, cts.Token);

            Assert.Equal(TransferStatus.Completed, send.Status);
            Assert.Equal(TransferStatus.Completed, (await receive).Status);
            listener.Stop();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
