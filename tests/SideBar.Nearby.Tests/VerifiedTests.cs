using SideBar.Nearby.Identity;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;
using Xunit;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Tests;

/// <summary>Whether a session reports the peer as one of the user's own devices (synthetic certificates only).</summary>
public sealed class VerifiedTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "sidebar-verified-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public VerifiedTests()
    {
        Directory.CreateDirectory(_out);
        _file = Path.Combine(_out, "src.bin");
        File.WriteAllBytes(_file, [1, 2, 3]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_out, recursive: true); } catch (IOException) { }
    }

    /// <summary>Phone ("Pixel 9") and PC that know each other's public certificates, as on the user's own account.</summary>
    private sealed class OwnPair
    {
        public CertificateIdentity Phone { get; }
        public CertificateIdentity Pc { get; }

        public OwnPair()
        {
            var phone = CertFixtures.NewDevice("Pixel 9");
            var pc = CertFixtures.NewDevice("My PC");
            Phone = new CertificateIdentity(PrivateOf(phone), [PublicOf(pc)]);
            Pc = new CertificateIdentity(PrivateOf(pc), [PublicOf(phone)]);
        }
    }

    private static PrivateCertificateData PrivateOf(CertFixtures.Device d) =>
        new(d.Private.SecretKey, d.Private.MetadataEncryptionKey, d.Private.KeyPairPkcs8);

    private static PublicCertificateData PublicOf(CertFixtures.Device d) =>
        new(d.Public.SecretId, d.Public.SecretKey, d.Public.PublicKey, d.Public.KeyTag, d.Public.EncryptedMetadata);

    private static CertificateIdentity Stranger() =>
        new(PrivateOf(CertFixtures.NewDevice("Stranger")), []);

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(30));

    // Real SendSession against real ReceiveSession.
    private async Task<(TransferResult Send, TransferResult Receive, IncomingTransfer? Offer, bool Asked)> RunPairAsync(
        IShareIdentity senderIdentity, EndpointInfo? senderRemote, IShareIdentity receiverIdentity, bool rejectUnverified)
    {
        using var cts = Timeout();
        using var link = await Link.OpenAsync(cts.Token);
        IncomingTransfer? offer = null;
        var asked = false;
        var receive = ReceiveSession.ReceiveAsync(
            link.Server, new SessionOptions("My PC", Identity: receiverIdentity) { RejectUnverified = rejectUnverified }, _out,
            (o, _) => { asked = true; offer = o; return Task.FromResult(true); }, ct: cts.Token);
        var send = await SendSession.SendFilesAsync(
            link.Client, new SessionOptions("Sender", Identity: senderIdentity) { RemoteEndpointInfo = senderRemote },
            [_file], ct: cts.Token);
        return (send, await receive, offer, asked);
    }

    // A hand-driven sender against the real ReceiveSession; `drive` talks to the receiver, the consent waits for it.
    private async Task<(IncomingTransfer? Offer, TransferResult Receive, bool Asked)> RunFakeAsync(
        IShareIdentity receiverIdentity, EndpointInfo advertised,
        Func<FakeSender, CancellationToken, Task> drive)
    {
        using var cts = Timeout();
        using var link = await Link.OpenAsync(cts.Token);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IncomingTransfer? offer = null;
        var asked = false;
        var receive = ReceiveSession.ReceiveAsync(
            link.Server, new SessionOptions("My PC", Identity: receiverIdentity), _out,
            async (o, ct) => { asked = true; offer = o; await gate.Task.WaitAsync(ct); return false; }, ct: cts.Token);
        await using (var sender = await FakeSender.ConnectAsync(link.Client, cts.Token, advertised))
        {
            await drive(sender, cts.Token);
            gate.TrySetResult();
            await sender.ReadResponseAsync(cts.Token);
        }
        return (offer, await receive, asked);
    }

    private static Share.PairedKeyEncryptionFrame Signed(CertificateIdentity who, byte[] authString) =>
        who.CreatePairedKeyEncryption(authString, PairedKeyRole.Sender, null);

    [Fact]
    public async Task Own_device_is_verified_before_introduction()
    {
        var pair = new OwnPair();

        var (send, receive, offer, _) = await RunPairAsync(
            pair.Phone, pair.Pc.CreateEndpointInfo("My PC", DeviceKind.Laptop), pair.Pc, rejectUnverified: false);

        Assert.Equal(TransferStatus.Completed, send.Status);
        Assert.Equal(TransferStatus.Completed, receive.Status);
        Assert.True(offer!.Verified);
        Assert.Equal("Pixel 9", offer.VerifiedName);
        Assert.True(send.PeerVerified);
    }

    [Fact]
    public async Task Introduction_before_paired_key_is_not_verified()
    {
        var pair = new OwnPair();
        var advert = pair.Phone.CreateEndpointInfo("x", DeviceKind.Phone);

        var (offer, _, asked) = await RunFakeAsync(pair.Pc, advert, async (s, ct) =>
        {
            await s.IntroduceAsync(ct, TestFrames.File(1, 3));
            // A perfectly valid key frame, but it arrives after the Introduction.
            await s.SendPairedKeyAsync(Signed(pair.Phone, s.AuthString), ct);
        });

        Assert.True(asked);
        Assert.False(offer!.Verified);
        Assert.Null(offer.VerifiedName);
    }

    [Fact]
    public async Task Forged_signature_is_not_verified()
    {
        var pair = new OwnPair();
        var advert = pair.Phone.CreateEndpointInfo("x", DeviceKind.Phone);

        var (offer, _, asked) = await RunFakeAsync(pair.Pc, advert, async (s, ct) =>
        {
            var frame = Signed(pair.Phone, s.AuthString);
            var bad = frame.SignedData.ToByteArray();
            bad[^1] ^= 0xFF;
            frame.SignedData = Google.Protobuf.ByteString.CopyFrom(bad);
            await s.SendPairedKeyAsync(frame, ct);
            await s.IntroduceAsync(ct, TestFrames.File(1, 3));
        });

        Assert.True(asked);
        Assert.False(offer!.Verified);
        Assert.Null(offer.VerifiedName);
    }

    [Fact]
    public async Task Name_like_own_device_but_unknown_cert_is_not_verified()
    {
        var pair = new OwnPair();
        var stranger = Stranger();
        // Claims to be "Pixel 9" in the clear, but its certificate is not one the receiver knows.
        var advert = new EndpointInfo
        {
            Kind = DeviceKind.Phone, Name = "Pixel 9",
            Metadata = stranger.CreateEndpointInfo("x", DeviceKind.Phone).Metadata,
        };

        var (offer, _, asked) = await RunFakeAsync(pair.Pc, advert, async (s, ct) =>
        {
            await s.SendPairedKeyAsync(Signed(stranger, s.AuthString), ct);
            await s.IntroduceAsync(ct, TestFrames.File(1, 3));
        });

        Assert.True(asked);
        Assert.False(offer!.Verified);
        Assert.Null(offer.VerifiedName);
        Assert.Equal("Pixel 9", offer.SenderName);
    }

    [Fact]
    public async Task RejectUnverified_rejects_without_asking()
    {
        var pair = new OwnPair();
        using var cts = Timeout();
        using var link = await Link.OpenAsync(cts.Token);
        var asked = false;
        var receive = ReceiveSession.ReceiveAsync(
            link.Server, new SessionOptions("My PC", Identity: pair.Pc) { RejectUnverified = true }, _out,
            (_, _) => { asked = true; return Task.FromResult(true); }, ct: cts.Token);

        await using var sender = await FakeSender.ConnectAsync(link.Client, cts.Token);
        await sender.IntroduceAsync(cts.Token, TestFrames.File(1, 3));
        var status = await sender.ReadResponseAsync(cts.Token);

        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Reject, status);
        Assert.Equal(TransferStatus.RejectedByPeer, (await receive).Status);
        Assert.False(asked);
    }

    [Fact]
    public async Task RejectUnverified_still_accepts_own_device()
    {
        var pair = new OwnPair();

        var (send, receive, offer, asked) = await RunPairAsync(
            pair.Phone, pair.Pc.CreateEndpointInfo("My PC", DeviceKind.Laptop), pair.Pc, rejectUnverified: true);

        Assert.Equal(TransferStatus.Completed, send.Status);
        Assert.Equal(TransferStatus.Completed, receive.Status);
        Assert.True(asked);
        Assert.True(offer!.Verified);
    }

    [Fact]
    public async Task Verified_name_comes_from_certificate_not_endpoint_name()
    {
        var pair = new OwnPair();
        // Visible name in the advertisement differs from the one in the certificate.
        var advert = new EndpointInfo
        {
            Kind = DeviceKind.Phone, Name = "Spoofed name",
            Metadata = pair.Phone.CreateEndpointInfo("x", DeviceKind.Phone).Metadata,
        };

        var (offer, _, _) = await RunFakeAsync(pair.Pc, advert, async (s, ct) =>
        {
            await s.SendPairedKeyAsync(Signed(pair.Phone, s.AuthString), ct);
            await s.IntroduceAsync(ct, TestFrames.File(1, 3));
        });

        Assert.True(offer!.Verified);
        Assert.Equal("Pixel 9", offer.VerifiedName);
        Assert.Equal("Spoofed name", offer.SenderName);
    }

    [Fact]
    public async Task First_paired_key_counts_and_a_later_valid_one_does_not_verify()
    {
        var pair = new OwnPair();
        var advert = pair.Phone.CreateEndpointInfo("x", DeviceKind.Phone);

        var (offer, _, asked) = await RunFakeAsync(pair.Pc, advert, async (s, ct) =>
        {
            var forged = Signed(pair.Phone, s.AuthString);
            var bad = forged.SignedData.ToByteArray();
            bad[^1] ^= 0xFF;
            forged.SignedData = Google.Protobuf.ByteString.CopyFrom(bad);
            await s.SendPairedKeyAsync(forged, ct);
            await s.SendPairedKeyAsync(Signed(pair.Phone, s.AuthString), ct);
            await s.IntroduceAsync(ct, TestFrames.File(1, 3));
        });

        Assert.True(asked);
        Assert.False(offer!.Verified);
    }

    [Fact]
    public async Task Sender_ignores_a_valid_paired_key_that_arrives_after_its_introduction()
    {
        var pair = new OwnPair();
        using var cts = Timeout();
        using var link = await Link.OpenAsync(cts.Token);
        var send = SendSession.SendFilesAsync(
            link.Client,
            new SessionOptions("Sender", Identity: pair.Phone) { RemoteEndpointInfo = pair.Pc.CreateEndpointInfo("My PC", DeviceKind.Laptop) },
            [_file], ct: cts.Token);

        // The anonymous key sent by FakeReceiver does not verify; the Introduction follows it.
        await using var receiver = await FakeReceiver.AcceptAsync(link.Server, cts.Token);
        await receiver.SendPairedKeyAsync(pair.Pc.CreatePairedKeyEncryption(receiver.AuthString, PairedKeyRole.Receiver, null), cts.Token);
        await receiver.SendSharingAsync(SharingFrames.Response(Share.ConnectionResponseFrame.Types.Status.Reject), cts.Token);

        var result = await send;
        Assert.Equal(TransferStatus.RejectedByPeer, result.Status);
        Assert.False(result.PeerVerified);
    }

    [Fact]
    public async Task Replayed_advertisement_of_own_device_with_stranger_signature_is_not_verified()
    {
        var pair = new OwnPair();
        var stranger = Stranger();
        // The metadata of the phone's public advertisement is copied, the signature is the stranger's own.
        var advert = pair.Phone.CreateEndpointInfo("x", DeviceKind.Phone);

        var (offer, _, asked) = await RunFakeAsync(pair.Pc, advert, async (s, ct) =>
        {
            await s.SendPairedKeyAsync(Signed(stranger, s.AuthString), ct);
            await s.IntroduceAsync(ct, TestFrames.File(1, 3));
        });

        Assert.True(asked);
        Assert.False(offer!.Verified);
        Assert.Null(offer.VerifiedName);
    }

    [Fact]
    public async Task Reflected_paired_key_is_not_verified()
    {
        var pair = new OwnPair();
        var advert = pair.Phone.CreateEndpointInfo("x", DeviceKind.Phone);

        var (offer, _, asked) = await RunFakeAsync(pair.Pc, advert, async (s, ct) =>
        {
            // Echo the receiver's own key frame back to it.
            var theirs = await s.ReadAsync(Share.V1Frame.Types.FrameType.PairedKeyEncryption, ct);
            await s.SendPairedKeyAsync(theirs.V1.PairedKeyEncryption, ct);
            await s.IntroduceAsync(ct, TestFrames.File(1, 3));
        });

        Assert.True(asked);
        Assert.False(offer!.Verified);
        Assert.Null(offer.VerifiedName);
    }

    [Fact]
    public async Task Stranger_posing_as_Pixel_9_is_rejected_without_asking_in_own_devices_mode()
    {
        var pair = new OwnPair();
        var stranger = Stranger();
        var advert = new EndpointInfo
        {
            Kind = DeviceKind.Phone, Name = "Pixel 9",
            Metadata = stranger.CreateEndpointInfo("x", DeviceKind.Phone).Metadata,
        };
        using var cts = Timeout();
        using var link = await Link.OpenAsync(cts.Token);
        var asked = false;
        var receive = ReceiveSession.ReceiveAsync(
            link.Server, new SessionOptions("My PC", Identity: pair.Pc) { RejectUnverified = true }, _out,
            (_, _) => { asked = true; return Task.FromResult(true); }, ct: cts.Token);

        await using var sender = await FakeSender.ConnectAsync(link.Client, cts.Token, advert);
        await sender.SendPairedKeyAsync(Signed(stranger, sender.AuthString), cts.Token);
        await sender.IntroduceAsync(cts.Token, TestFrames.File(1, 3));
        var status = await sender.ReadResponseAsync(cts.Token);

        var result = await receive;
        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Reject, status);
        Assert.Equal(TransferStatus.RejectedByPeer, result.Status);
        Assert.Equal("unverified", result.Detail);
        Assert.False(asked);
    }
}
