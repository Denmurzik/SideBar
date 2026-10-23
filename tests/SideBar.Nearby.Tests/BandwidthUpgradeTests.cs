using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;
using Bwu = SideBar.Nearby.Protocol.Connections.BandwidthUpgradeNegotiationFrame.Types.EventType;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Tests;

/// <summary>
/// The phone moves a running send from the "Bluetooth" socket to a Wi-Fi LAN one. Both are real loopback TCP sockets;
/// the phone side is <see cref="RawReceiver"/>, which carries one encrypted session over both.
/// </summary>
public sealed class BandwidthUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidebar-nearby-bwu-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _wifi = new(IPAddress.Loopback, 0);
    private readonly List<IPEndPoint> _dialed = [];

    public BandwidthUpgradeTests()
    {
        Directory.CreateDirectory(_dir);
        _wifi.Start();
    }

    public void Dispose()
    {
        _wifi.Stop();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IPEndPoint WifiEndpoint => (IPEndPoint)_wifi.LocalEndpoint;

    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private (string Path, byte[] Content) MakeFile(int size)
    {
        var content = RandomNumberGenerator.GetBytes(size);
        var path = System.IO.Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(path, content);
        return (path, content);
    }

    private SessionOptions Options(bool refuse = false) => new("PC")
    {
        ConnectWifiLanUpgradeAsync = async (endpoint, ct) =>
        {
            lock (_dialed) _dialed.Add(endpoint);
            if (refuse) throw new SocketException((int)SocketError.ConnectionRefused);
            var client = new TcpClient();
            await client.ConnectAsync(endpoint, ct);
            return client.GetStream();
        },
    };

    /// <summary>Holds the sender after its first chunk until the test opens it.</summary>
    private sealed class Gate : ITransferObserver
    {
        public readonly ManualResetEventSlim Open = new(false);
        void ITransferObserver.Progress(TransferProgress progress) => Open.Wait(TimeSpan.FromSeconds(20));
    }

    private static bool IsIntroduction(List<Share.Frame> frames) =>
        frames.Any(f => f.V1?.Type == Share.V1Frame.Types.FrameType.Introduction);

    private static async Task<RawReceiver> AcceptTransferAsync(Stream old, FileSink sink, CancellationToken ct)
    {
        var peer = await RawReceiver.HandshakeAsync(old, ct);
        await peer.SendPairedKeyEncryptionAsync(old, ct);
        await peer.SendPairedKeyResultAsync(old, ct);
        var sharing = new List<Share.Frame>();
        await peer.ReadUntilAsync(old, _ => IsIntroduction(sharing), sink, ct, sharing);
        await peer.AcceptAsync(old, ct);
        return peer;
    }

    private async Task<Stream> AcceptWifiAsync(RawReceiver peer, bool ack, CancellationToken ct)
    {
        var stream = (await _wifi.AcceptTcpClientAsync(ct)).GetStream();
        // The introduction is plaintext: the phone does not yet know which session this socket belongs to.
        var intro = Conn.OfflineFrame.Parser.ParseFrom(await FrameIO.ReadAsync(stream, ct));
        Assert.True(RawReceiver.IsBwu(intro, Bwu.ClientIntroduction));
        Assert.Equal(peer.SenderEndpointId, intro.V1.BandwidthUpgradeNegotiation.ClientIntroduction.EndpointId);
        if (ack) await FrameIO.WriteAsync(stream, TestFrames.Bwu(Bwu.ClientIntroductionAck).ToByteArray(), ct);
        return stream;
    }

    [Fact]
    public async Task SendSession_upgrades_to_wifi_lan_mid_transfer_without_losing_bytes()
    {
        var ct = Token();
        var (file, content) = MakeFile(OfflineFrames.PayloadChunkSize * 6 + 123);
        using var link = await Link.OpenAsync(ct);
        var old = link.Server;
        var gate = new Gate();
        var send = SendSession.SendFilesAsync(link.Client, Options(), [file], gate, ct);

        var sink = new FileSink();
        var peer = await AcceptTransferAsync(old, sink, ct);
        await peer.ReadUntilAsync(old, _ => sink.Chunks == 1, sink, ct);
        await peer.SendAsync(old, TestFrames.UpgradePathAvailable(WifiEndpoint, ack: true), ct);
        using var wifi = await AcceptWifiAsync(peer, ack: true, ct);

        await peer.ReadUntilAsync(old, f => RawReceiver.IsBwu(f, Bwu.LastWriteToPriorChannel), sink, ct);
        var onOld = sink.Length;
        // The rest of the file is written while the old channel drains: it must wait for the new one.
        gate.Open.Set();
        await peer.SendAsync(old, TestFrames.Bwu(Bwu.LastWriteToPriorChannel), ct);
        await peer.ReadUntilAsync(old, f => RawReceiver.IsBwu(f, Bwu.SafeToClosePriorChannel), sink, ct);
        Assert.Equal(onOld, sink.Length);
        await peer.SendAsync(old, TestFrames.Bwu(Bwu.SafeToClosePriorChannel), ct);
        // Like the phone: an unencrypted Disconnection on the old channel, then the other side's.
        await FrameIO.WriteAsync(old, OfflineFrames.Disconnection().ToByteArray(), ct);
        var bye = Conn.OfflineFrame.Parser.ParseFrom(await FrameIO.ReadAsync(old, ct));
        Assert.Equal(Conn.V1Frame.Types.FrameType.Disconnection, bye.V1.Type);

        await peer.ReadUntilAsync(wifi, _ => sink.Done, sink, ct);
        await peer.SendAsync(wifi, OfflineFrames.Disconnection(), ct);

        var result = await send;
        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Equal(content, sink.Bytes);
        // Exactly the first chunk went over "Bluetooth", everything after it over Wi-Fi.
        Assert.Equal(OfflineFrames.PayloadChunkSize, onOld);
        Assert.Equal([WifiEndpoint], _dialed);
    }

    [Fact]
    public async Task Upgrade_offered_before_the_paired_key_result_waits_for_it()
    {
        var ct = Token();
        var (file, content) = MakeFile(OfflineFrames.PayloadChunkSize * 2 + 5);
        using var link = await Link.OpenAsync(ct);
        var old = link.Server;
        var send = SendSession.SendFilesAsync(link.Client, Options(), [file], null, ct);

        var peer = await RawReceiver.HandshakeAsync(old, ct);
        await peer.SendPairedKeyEncryptionAsync(old, ct);
        await peer.SendAsync(old, TestFrames.UpgradePathAvailable(WifiEndpoint, ack: false), ct);
        // The peer is not checked yet: the offer must not make the sender dial anything.
        await Task.Delay(300, ct);
        Assert.Empty(_dialed);
        Assert.False(_wifi.Pending());

        await peer.SendPairedKeyResultAsync(old, ct);
        using var wifi = await AcceptWifiAsync(peer, ack: false, ct);
        await peer.SendAsync(old, TestFrames.Bwu(Bwu.LastWriteToPriorChannel), ct);

        var sink = new FileSink();
        var onOld = new List<Share.Frame>();
        await peer.ReadUntilAsync(old, f => RawReceiver.IsBwu(f, Bwu.LastWriteToPriorChannel), sink, ct, onOld);
        // The deferred offer is taken up right after the Introduction, which therefore still went out here.
        Assert.True(IsIntroduction(onOld));
        await peer.ReadUntilAsync(old, f => RawReceiver.IsBwu(f, Bwu.SafeToClosePriorChannel), sink, ct, onOld);
        await peer.SendAsync(old, TestFrames.Bwu(Bwu.SafeToClosePriorChannel), ct);
        // A phone may also just drop the old socket instead of the Disconnection exchange.
        old.Dispose();

        await peer.AcceptAsync(wifi, ct);
        await peer.ReadUntilAsync(wifi, _ => sink.Done, sink, ct);
        await peer.SendAsync(wifi, OfflineFrames.Disconnection(), ct);

        Assert.Equal(TransferStatus.Completed, (await send).Status);
        Assert.Equal(content, sink.Bytes);
    }

    [Fact]
    public async Task Offer_from_an_unverified_peer_is_never_acted_on()
    {
        var ct = Token();
        var (file, _) = MakeFile(100);
        using var link = await Link.OpenAsync(ct);
        var old = link.Server;
        var options = Options() with { RequireVerifiedPeer = true };
        var send = SendSession.SendFilesAsync(link.Client, options, [file], null, ct);

        var peer = await RawReceiver.HandshakeAsync(old, ct);
        await peer.SendPairedKeyEncryptionAsync(old, ct);
        await peer.SendAsync(old, TestFrames.UpgradePathAvailable(WifiEndpoint, ack: true), ct);
        await peer.SendPairedKeyResultAsync(old, ct);

        var result = await send;
        Assert.Equal(TransferStatus.Failed, result.Status);
        Assert.Equal(TransferDetails.Unverified, result.Detail);
        Assert.Empty(_dialed);
        Assert.False(_wifi.Pending());
    }

    [Fact]
    public async Task Session_without_a_connector_ignores_offers_as_before()
    {
        var ct = Token();
        var (file, content) = MakeFile(OfflineFrames.PayloadChunkSize * 2 + 9);
        using var link = await Link.OpenAsync(ct);
        var old = link.Server;
        // As on the Wi-Fi LAN (mDNS) path: no ConnectWifiLanUpgradeAsync.
        var send = SendSession.SendFilesAsync(link.Client, new SessionOptions("PC"), [file], null, ct);

        var sink = new FileSink();
        var peer = await AcceptTransferAsync(old, sink, ct);
        await peer.SendAsync(old, TestFrames.UpgradePathAvailable(WifiEndpoint, ack: true), ct);
        var bwuFromSender = false;
        await peer.ReadUntilAsync(old, f =>
        {
            bwuFromSender |= f.V1?.Type == Conn.V1Frame.Types.FrameType.BandwidthUpgradeNegotiation;
            return sink.Done;
        }, sink, ct);
        await peer.SendAsync(old, OfflineFrames.Disconnection(), ct);

        Assert.Equal(TransferStatus.Completed, (await send).Status);
        Assert.Equal(content, sink.Bytes);
        Assert.False(bwuFromSender);
        Assert.False(_wifi.Pending());
    }

    [Theory]
    [InlineData("wrong-medium")]
    [InlineData("connect-fails")]
    [InlineData("no-ack")]
    public async Task Failed_upgrade_finishes_the_transfer_on_the_old_channel(string failure)
    {
        var ct = Token();
        var (file, content) = MakeFile(OfflineFrames.PayloadChunkSize * 3 + 7);
        using var link = await Link.OpenAsync(ct);
        var old = link.Server;
        var send = SendSession.SendFilesAsync(link.Client, Options(refuse: failure == "connect-fails"), [file], null, ct);

        var sink = new FileSink();
        var peer = await AcceptTransferAsync(old, sink, ct);
        await peer.ReadUntilAsync(old, _ => sink.Chunks >= 1, sink, ct);
        await peer.SendAsync(old, TestFrames.UpgradePathAvailable(WifiEndpoint, ack: true,
            failure == "wrong-medium"
                ? Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo.Types.Medium.WifiHotspot
                : Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo.Types.Medium.WifiLan), ct);
        if (failure == "no-ack")
        {
            // Introduction read, then the socket is dropped without the promised ack.
            var wifi = await AcceptWifiAsync(peer, ack: false, ct);
            await wifi.DisposeAsync();
        }

        var failed = false;
        await peer.ReadUntilAsync(old, f =>
        {
            failed |= RawReceiver.IsBwu(f, Bwu.UpgradeFailure);
            return failed && sink.Done;
        }, sink, ct);
        await peer.SendAsync(old, OfflineFrames.Disconnection(), ct);

        Assert.Equal(TransferStatus.Completed, (await send).Status);
        Assert.Equal(content, sink.Bytes);
        Assert.Equal(failure == "wrong-medium" ? 0 : 1, _dialed.Count);
    }
}
