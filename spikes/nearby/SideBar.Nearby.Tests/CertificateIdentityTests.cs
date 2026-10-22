using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SideBar.Nearby.Protocol.Sharing;
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
        Assert.NotNull(b.Identify(decoded!));
        Assert.Null(a.Identify(decoded!));
    }

    [Fact]
    public void PairedKeyRoundTripSucceeds()
    {
        var (a, b) = Pair();
        var token = RandomNumberGenerator.GetBytes(32);

        var fromSender = a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender);
        var fromReceiver = b.CreatePairedKeyEncryption(token, PairedKeyRole.Receiver);

        Assert.Equal(PairedKeyResultFrame.Types.Status.Success, b.VerifyPairedKey(fromSender, token, PairedKeyRole.Receiver));
        Assert.Equal(PairedKeyResultFrame.Types.Status.Success, a.VerifyPairedKey(fromReceiver, token, PairedKeyRole.Sender));
        Assert.Equal(6, fromSender.SecretIdHash.Length);
    }

    [Fact]
    public void WrongRolePrefixOrTokenIsNotAccepted()
    {
        var (a, b) = Pair();
        var token = RandomNumberGenerator.GetBytes(32);
        var frame = a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender);

        // Receiver verifying a frame that claims the receiver role: prefix mismatch.
        Assert.Equal(PairedKeyResultFrame.Types.Status.Fail,
            b.VerifyPairedKey(frame, token, PairedKeyRole.Sender));
        // Different token: the hash no longer matches any certificate.
        Assert.Equal(PairedKeyResultFrame.Types.Status.Unable,
            b.VerifyPairedKey(frame, RandomNumberGenerator.GetBytes(32), PairedKeyRole.Receiver));
    }

    [Fact]
    public void UnknownPeerGivesUnable()
    {
        var (a, _) = Pair();
        var (_, stranger) = Pair();
        var token = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(PairedKeyResultFrame.Types.Status.Unable,
            stranger.VerifyPairedKey(a.CreatePairedKeyEncryption(token, PairedKeyRole.Sender), token, PairedKeyRole.Receiver));
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
            var send = await SendSession.SendFilesAsync(client.GetStream(), new SessionOptions("S", Identity: a), [file], null, cts.Token);

            Assert.Equal(TransferStatus.Completed, send.Status);
            Assert.Equal(TransferStatus.Completed, (await receive).Status);
            listener.Stop();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
