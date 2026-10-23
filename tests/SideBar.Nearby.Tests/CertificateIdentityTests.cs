using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SideBar.Nearby.Protocol.Sharing;
using SideBar.Nearby.Identity;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;
using Xunit;

namespace SideBar.Nearby.Tests;

public class CertificateIdentityTests
{
    private static (PrivateCertificateData Private, PublicCertificateData Public) NewCert()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var metadataKey = RandomNumberGenerator.GetBytes(14);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            new PrivateCertificateData(secret, metadataKey, ec.ExportPkcs8PrivateKey()),
            new PublicCertificateData(SHA256.HashData(secret), secret, ec.ExportSubjectPublicKeyInfo(),
                HMACSHA256.HashData(new byte[32], metadataKey)));
    }

    private static (CertificateIdentity A, CertificateIdentity B) Pair()
    {
        var (privA, pubA) = NewCert();
        var (privB, pubB) = NewCert();
        return (new CertificateIdentity(privA, [pubB]), new CertificateIdentity(privB, [pubA]));
    }

    [Fact]
    public void AdvertisedMetadataKeyIsRecognisedByThePeer()
    {
        var (a, b) = Pair();
        var info = a.CreateEndpointInfo("PC", DeviceKind.Laptop);

        Assert.True(EndpointInfo.TryDecode(info.Encode(), out var decoded));
        Assert.True(b.Identify(decoded!));
        Assert.False(a.Identify(decoded!));
    }

    [Fact]
    public void Endpoint_info_in_own_devices_has_no_name()
    {
        var (a, _) = Pair();

        var info = a.CreateEndpointInfo("My PC", DeviceKind.Laptop);

        Assert.True(info.Hidden);
        Assert.Null(info.Name);
        var encoded = info.Encode();
        Assert.Equal(1 + EndpointInfo.MetadataLength, encoded.Length);
        Assert.True(EndpointInfo.TryDecode(encoded, out var decoded));
        Assert.True(decoded!.Hidden);
        Assert.Null(decoded.Name);
    }

    [Fact]
    public void New_salt_each_CreateEndpointInfo_and_not_in_consumed()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var consumed = Enumerable.Range(0, 40000).Select(i => (ushort)i).ToHashSet();
        var identity = new CertificateIdentity(
            new PrivateCertificateData(secret, RandomNumberGenerator.GetBytes(14), ec.ExportPkcs8PrivateKey(), consumed), []);

        var salts = Enumerable.Range(0, 300)
            .Select(_ => identity.CreateEndpointInfo("x", DeviceKind.Laptop).Metadata[..2])
            .Select(b => (ushort)((b[0] << 8) | b[1]))
            .ToList();

        Assert.All(salts, salt => Assert.DoesNotContain(salt, consumed));
        Assert.True(salts.Distinct().Count() > 200, "salts must be fresh on every registration");
    }

    private static EndpointInfo Advert(CertificateIdentity who) => who.CreateEndpointInfo("x", DeviceKind.Phone);

    [Fact]
    public void PairedKeyRoundTripSucceeds()
    {
        var (a, b) = Pair();
        var token = RandomNumberGenerator.GetBytes(32);

        var fromSender = a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender, Advert(b));
        var fromReceiver = b.CreatePairedKeyEncryption(token, PairedKeyRole.Receiver, Advert(a));

        Assert.Equal(PairedKeyResultFrame.Types.Status.Success,
            b.VerifyPairedKey(fromSender, token, PairedKeyRole.Receiver, Advert(a)));
        Assert.Equal(PairedKeyResultFrame.Types.Status.Success,
            a.VerifyPairedKey(fromReceiver, token, PairedKeyRole.Sender, Advert(b)));
        Assert.Equal(6, fromSender.SecretIdHash.Length);
    }

    [Fact]
    public void SecretIdHashIsComputedWithThePeersCertificateSecret()
    {
        // google/nearby: the hash uses the peer's public certificate secret, so the peer can check it against
        // its own private certificate.
        var (privA, pubA) = NewCert();
        var (privB, pubB) = NewCert();
        var a = new CertificateIdentity(privA, [pubB]);
        var token = RandomNumberGenerator.GetBytes(32);

        var frame = a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender,
            new CertificateIdentity(privB, [pubA]).CreateEndpointInfo("b", DeviceKind.Phone));

        Assert.Equal(HKDF.DeriveKey(HashAlgorithmName.SHA256, token, 6, privB.SecretKey, []), frame.SecretIdHash.ToByteArray());
    }

    [Fact]
    public void WrongRolePrefixIsRejectedAsFail()
    {
        var (a, b) = Pair();
        var token = RandomNumberGenerator.GetBytes(32);
        var frame = a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender, Advert(b));

        // Verified as if it came from a receiver: prefix 0x02 expected, 0x01 was signed.
        Assert.Equal(PairedKeyResultFrame.Types.Status.Fail,
            b.VerifyPairedKey(frame, token, PairedKeyRole.Sender, Advert(a)));
        Assert.Equal(PairedKeyResultFrame.Types.Status.Fail,
            b.VerifyPairedKey(frame, RandomNumberGenerator.GetBytes(32), PairedKeyRole.Receiver, Advert(a)));
    }

    [Fact]
    public void OptionalSignedDataIsAcceptedWhenTheMainSignatureDoesNotVerify()
    {
        var (a, b) = Pair();
        var token = RandomNumberGenerator.GetBytes(32);
        var good = a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender, Advert(b));
        var frame = new PairedKeyEncryptionFrame
        {
            SignedData = Google.Protobuf.ByteString.CopyFrom(RandomNumberGenerator.GetBytes(72)),
            OptionalSignedData = good.SignedData,
            SecretIdHash = good.SecretIdHash,
        };
        Assert.Equal(PairedKeyResultFrame.Types.Status.Success,
            b.VerifyPairedKey(frame, token, PairedKeyRole.Receiver, Advert(a)));
    }

    [Fact]
    public void UnknownPeerGivesUnable()
    {
        var (a, b) = Pair();
        var (_, stranger) = Pair();
        var token = RandomNumberGenerator.GetBytes(32);
        var frame = a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender, Advert(b));
        // The stranger cannot identify a's advertisement, nor can it be handed none.
        Assert.Equal(PairedKeyResultFrame.Types.Status.Unable,
            stranger.VerifyPairedKey(frame, token, PairedKeyRole.Receiver, Advert(a)));
        Assert.Equal(PairedKeyResultFrame.Types.Status.Unable,
            b.VerifyPairedKey(frame, token, PairedKeyRole.Receiver, null));
    }

    [Fact]
    public async Task FullTransferWorksWithCertificatesOnBothSides()
    {
        var (a, b) = Pair();
        var dir = Path.Combine(Path.GetTempPath(), "sidebar-nearby-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "f.bin");
            File.WriteAllBytes(file, [1, 2, 3, 4]);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var receive = Task.Run(async () =>
            {
                using var c = await listener.AcceptTcpClientAsync(cts.Token);
                return await ReceiveSession.ReceiveAsync(c.GetStream(), new SessionOptions("R", Identity: b),
                    Path.Combine(dir, "out"), (_, _) => Task.FromResult(true), null, cts.Token);
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cts.Token);
            var send = await SendSession.SendFilesAsync(client.GetStream(), new SessionOptions("S", Identity: a) { RemoteEndpointInfo = Advert(b) }, [file], null, cts.Token);

            Assert.Equal(TransferStatus.Completed, send.Status);
            Assert.Equal(TransferStatus.Completed, (await receive).Status);
            listener.Stop();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
