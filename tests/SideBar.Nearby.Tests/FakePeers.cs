using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using SideBar.Nearby.Crypto;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Tests;

/// <summary>A connected loopback TCP pair: <see cref="Server"/> is the accepted end, <see cref="Client"/> the connecting one.</summary>
internal sealed class Link : IDisposable
{
    private readonly TcpClient _server;
    private readonly TcpClient _client;
    public Stream Server => _server.GetStream();
    public Stream Client => _client.GetStream();

    private Link(TcpClient server, TcpClient client)
    {
        _server = server;
        _client = client;
    }

    public static async Task<Link> OpenAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(ct).AsTask();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            return new Link(await accept, client);
        }
        finally { listener.Stop(); }
    }

    public void Dispose()
    {
        _server.Dispose();
        _client.Dispose();
    }
}

internal static class TestFrames
{
    public static Share.FileMetadata File(long payloadId, long size, string name = "f.bin") => new()
    {
        Name = name,
        Size = size,
        MimeType = "application/octet-stream",
        Id = payloadId + 1000,
        PayloadId = payloadId,
        Type = Share.FileMetadata.Types.Type.Unknown,
    };

    public static Conn.OfflineFrame Offline(Conn.V1Frame v1) =>
        new() { Version = Conn.OfflineFrame.Types.Version.V1, V1 = v1 };

    public static Conn.OfflineFrame Control(long payloadId, Conn.PayloadTransferFrame.Types.ControlMessage.Types.EventType ev) =>
        Offline(new Conn.V1Frame
        {
            Type = Conn.V1Frame.Types.FrameType.PayloadTransfer,
            PayloadTransfer = new Conn.PayloadTransferFrame
            {
                PacketType = Conn.PayloadTransferFrame.Types.PacketType.Control,
                PayloadHeader = OfflineFrames.FileHeader(payloadId, 0, "x"),
                ControlMessage = new Conn.PayloadTransferFrame.Types.ControlMessage { Event = ev },
            },
        });

    public static Conn.OfflineFrame FileChunk(long payloadId, long total, long offset, byte[] body, bool last) =>
        OfflineFrames.PayloadChunk(OfflineFrames.FileHeader(payloadId, total, "x"), offset, body, last);

    public static Share.Frame Of(Share.V1Frame.Types.FrameType type) =>
        new() { Version = Share.Frame.Types.Version.V1, V1 = new Share.V1Frame { Type = type } };

    public static Conn.OfflineFrame Bwu(Conn.BandwidthUpgradeNegotiationFrame.Types.EventType type) =>
        Offline(new Conn.V1Frame
        {
            Type = Conn.V1Frame.Types.FrameType.BandwidthUpgradeNegotiation,
            BandwidthUpgradeNegotiation = new Conn.BandwidthUpgradeNegotiationFrame { EventType = type },
        });

    public static Conn.OfflineFrame UpgradePathAvailable(
        IPEndPoint endpoint, bool ack,
        Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo.Types.Medium medium =
            Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo.Types.Medium.WifiLan)
    {
        var frame = Bwu(Conn.BandwidthUpgradeNegotiationFrame.Types.EventType.UpgradePathAvailable);
        frame.V1.BandwidthUpgradeNegotiation.UpgradePathInfo = new Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo
        {
            Medium = medium,
            WifiLanSocket = new Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo.Types.WifiLanSocket
            {
                IpAddress = Google.Protobuf.ByteString.CopyFrom(endpoint.Address.GetAddressBytes()),
                WifiPort = endpoint.Port,
            },
            SupportsClientIntroductionAck = ack,
        };
        return frame;
    }
}

/// <summary>A hand-driven sender: does the real handshake, then lets a test inject arbitrary frames.</summary>
internal sealed class FakeSender : IAsyncDisposable
{
    private readonly NearbyConnection _connection;
    private FakeSender(NearbyConnection connection, byte[] authString)
    {
        _connection = connection;
        AuthString = authString;
    }

    /// <summary>UKEY2 auth string of this session, for signing a PairedKeyEncryption.</summary>
    public byte[] AuthString { get; }

    public static async Task<FakeSender> ConnectAsync(Stream stream, CancellationToken ct, EndpointInfo? endpointInfo = null)
    {
        var options = new SessionOptions("fake sender");
        var c = new NearbyConnection(stream, "fake-send", options);
        await c.WritePlainAsync(OfflineFrames.ConnectionRequest(
            "FAKE", "fake sender", (endpointInfo ?? EndpointInfo.CreateAnonymous("fake sender", DeviceKind.Laptop)).Encode()), ct);
        using var ukey = new Ukey2Client();
        await c.WritePlainAsync(ukey.CreateClientInit(), ct);
        var (finish, handshake) = ukey.ProcessServerInit(await c.ReadPlainAsync(ct));
        await c.WritePlainAsync(finish, ct);
        await c.WritePlainAsync(OfflineFrames.ConnectionResponse(accept: true), ct);
        await c.ReadPlainOfflineAsync(ct);
        c.EnableEncryption(SecureChannel.ForClient(handshake.NextSecret));
        return new FakeSender(c, handshake.AuthString.ToArray());
    }

    public Task SendPairedKeyAsync(Share.PairedKeyEncryptionFrame frame, CancellationToken ct) =>
        SendSharingAsync(SharingFrames.PairedKeyEncryption(frame), ct);

    public Task SendOfflineAsync(Conn.OfflineFrame frame, CancellationToken ct) => _connection.SendOfflineAsync(frame, ct);

    public Task SendSharingAsync(Share.Frame frame, CancellationToken ct) => _connection.SendSharingAsync(frame, ct);

    public Task IntroduceAsync(CancellationToken ct, params Share.FileMetadata[] files) =>
        SendSharingAsync(SharingFrames.Introduction(files), ct);

    /// <summary>Reads Quick Share frames until one of the given type arrives.</summary>
    public async Task<Share.Frame> ReadAsync(Share.V1Frame.Types.FrameType type, CancellationToken ct)
    {
        while (true)
        {
            var frame = (await _connection.ReadSharingAsync(null, ct))!;
            if (frame.V1?.Type == type) return frame;
        }
    }

    public async Task<Share.ConnectionResponseFrame.Types.Status> ReadResponseAsync(CancellationToken ct) =>
        (await ReadAsync(Share.V1Frame.Types.FrameType.Response, ct)).V1.ConnectionResponse.Status;

    public Task DisconnectAsync(CancellationToken ct) => _connection.SendOfflineAsync(OfflineFrames.Disconnection(), ct);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>A hand-driven receiver for sender tests: handshake, pairing frames, then it hands over the Introduction.</summary>
internal sealed class FakeReceiver : IAsyncDisposable
{
    private readonly NearbyConnection _connection;
    public Share.IntroductionFrame Introduction { get; }

    private FakeReceiver(NearbyConnection connection, Share.IntroductionFrame introduction, byte[] authString)
    {
        _connection = connection;
        Introduction = introduction;
        AuthString = authString;
    }

    /// <summary>UKEY2 auth string of this session, for signing a PairedKeyEncryption.</summary>
    public byte[] AuthString { get; }

    public static async Task<FakeReceiver> AcceptAsync(Stream stream, CancellationToken ct)
    {
        var c = new NearbyConnection(stream, "fake-recv", new SessionOptions("fake receiver"));
        await c.ReadPlainOfflineAsync(ct);
        using var ukey = new Ukey2Server();
        await c.WritePlainAsync(ukey.ProcessClientInit(await c.ReadPlainAsync(ct)), ct);
        var handshake = ukey.ProcessClientFinish(await c.ReadPlainAsync(ct));
        await c.ReadPlainOfflineAsync(ct);
        await c.WritePlainAsync(OfflineFrames.ConnectionResponse(accept: true), ct);
        c.EnableEncryption(SecureChannel.ForServer(handshake.NextSecret));

        var identity = new AnonymousIdentity();
        await c.SendSharingAsync(SharingFrames.PairedKeyEncryption(
            identity.CreatePairedKeyEncryption(handshake.AuthString, PairedKeyRole.Receiver, null)), ct);
        await c.SendSharingAsync(SharingFrames.PairedKeyResult(Share.PairedKeyResultFrame.Types.Status.Unable), ct);
        while (true)
        {
            var frame = (await c.ReadSharingAsync(null, ct))!;
            if (frame.V1?.Type == Share.V1Frame.Types.FrameType.Introduction)
                return new FakeReceiver(c, frame.V1.Introduction, handshake.AuthString);
        }
    }

    public Task SendOfflineAsync(Conn.OfflineFrame frame, CancellationToken ct) => _connection.SendOfflineAsync(frame, ct);

    public Task SendSharingAsync(Share.Frame frame, CancellationToken ct) => _connection.SendSharingAsync(frame, ct);

    public Task SendPairedKeyAsync(Share.PairedKeyEncryptionFrame frame, CancellationToken ct) =>
        SendSharingAsync(SharingFrames.PairedKeyEncryption(frame), ct);

    public Task AcceptTransferAsync(CancellationToken ct) =>
        SendSharingAsync(SharingFrames.Response(Share.ConnectionResponseFrame.Types.Status.Accept), ct);

    /// <summary>Reads (and discards file chunks) until a Quick Share frame of the type arrives.</summary>
    public async Task<Share.Frame> ReadAsync(Share.V1Frame.Types.FrameType type, CancellationToken ct)
    {
        while (true)
        {
            var frame = await _connection.ReadSharingAsync(_ => Task.FromResult(false), ct);
            if (frame?.V1?.Type == type) return frame;
        }
    }

    /// <summary>Reads until the last chunk of a FILE payload has arrived (the sender has then sent everything).</summary>
    public Task ReadUntilLastFileChunkAsync(CancellationToken ct) =>
        _connection.ReadSharingAsync(
            t => Task.FromResult((t.PayloadChunk.Flags & (int)Conn.PayloadTransferFrame.Types.PayloadChunk.Types.Flags.LastChunk) != 0), ct);

    /// <summary>Reads until the sender's Disconnection arrives; false when something else ends the session first.</summary>
    public async Task<bool> ReadUntilDisconnectionAsync(CancellationToken ct)
    {
        try { await _connection.ReadSharingAsync(_ => Task.FromResult(false), ct); return false; }
        catch (PeerDisconnectedException) { return true; }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>
/// A receiver that speaks the wire format by hand (no <see cref="NearbyConnection"/>), so one encrypted session can be
/// driven over two sockets the way a phone does in a bandwidth upgrade.
/// </summary>
internal sealed class RawReceiver
{
    private readonly SecureChannel _channel;
    private readonly byte[] _authString;
    public string SenderEndpointId { get; }

    private RawReceiver(SecureChannel channel, byte[] authString, string senderEndpointId)
    {
        _channel = channel;
        _authString = authString;
        SenderEndpointId = senderEndpointId;
    }

    /// <summary>Plaintext handshake on <paramref name="stream"/>; everything after it is encrypted.</summary>
    public static async Task<RawReceiver> HandshakeAsync(Stream stream, CancellationToken ct)
    {
        var request = Conn.OfflineFrame.Parser.ParseFrom(await FrameIO.ReadAsync(stream, ct));
        using var ukey = new Ukey2Server();
        await FrameIO.WriteAsync(stream, ukey.ProcessClientInit(await FrameIO.ReadAsync(stream, ct)), ct);
        var handshake = ukey.ProcessClientFinish(await FrameIO.ReadAsync(stream, ct));
        await FrameIO.ReadAsync(stream, ct);
        await FrameIO.WriteAsync(stream, OfflineFrames.ConnectionResponse(accept: true).ToByteArray(), ct);
        return new RawReceiver(SecureChannel.ForServer(handshake.NextSecret), handshake.AuthString.ToArray(),
            request.V1.ConnectionRequest.EndpointId);
    }

    public Task SendAsync(Stream stream, Conn.OfflineFrame frame, CancellationToken ct) =>
        FrameIO.WriteAsync(stream, _channel.Encrypt(frame.ToByteArray()), ct).AsTask();

    public async Task SendSharingAsync(Stream stream, Share.Frame frame, CancellationToken ct)
    {
        var data = frame.ToByteArray();
        var header = OfflineFrames.BytesHeader(Random.Shared.NextInt64(), data.Length);
        await SendAsync(stream, OfflineFrames.PayloadChunk(header, 0, data, last: false), ct);
        await SendAsync(stream, OfflineFrames.PayloadChunk(header, data.Length, [], last: true), ct);
    }

    public Task SendPairedKeyEncryptionAsync(Stream stream, CancellationToken ct) =>
        SendSharingAsync(stream, SharingFrames.PairedKeyEncryption(
            new AnonymousIdentity().CreatePairedKeyEncryption(_authString, PairedKeyRole.Receiver, null)), ct);

    public Task SendPairedKeyResultAsync(Stream stream, CancellationToken ct) =>
        SendSharingAsync(stream, SharingFrames.PairedKeyResult(Share.PairedKeyResultFrame.Types.Status.Unable), ct);

    public Task AcceptAsync(Stream stream, CancellationToken ct) =>
        SendSharingAsync(stream, SharingFrames.Response(Share.ConnectionResponseFrame.Types.Status.Accept), ct);

    /// <summary>
    /// Reads encrypted frames until <paramref name="stop"/> matches one (which is returned). FILE chunks go to
    /// <paramref name="sink"/>, Quick Share frames (sent whole in their first chunk) to <paramref name="sharing"/>.
    /// </summary>
    public async Task<Conn.OfflineFrame> ReadUntilAsync(
        Stream stream, Func<Conn.OfflineFrame, bool> stop, FileSink sink, CancellationToken ct, List<Share.Frame>? sharing = null)
    {
        while (true)
        {
            var frame = Conn.OfflineFrame.Parser.ParseFrom(_channel.Decrypt(await FrameIO.ReadAsync(stream, ct)));
            var transfer = frame.V1?.PayloadTransfer;
            if (transfer?.PayloadChunk is { } chunk)
            {
                if (transfer.PayloadHeader.Type == Conn.PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.File)
                    sink.Add(chunk);
                else if (!chunk.Body.IsEmpty)
                    sharing?.Add(Share.Frame.Parser.ParseFrom(chunk.Body));
            }
            if (stop(frame)) return frame;
        }
    }

    public static bool IsBwu(Conn.OfflineFrame frame, Conn.BandwidthUpgradeNegotiationFrame.Types.EventType type) =>
        frame.V1?.Type == Conn.V1Frame.Types.FrameType.BandwidthUpgradeNegotiation
        && frame.V1.BandwidthUpgradeNegotiation?.EventType == type;
}

/// <summary>Reassembles one FILE payload and fails on any gap, overlap or chunk after the last one.</summary>
internal sealed class FileSink
{
    private readonly MemoryStream _data = new();
    public bool Done { get; private set; }
    public int Chunks { get; private set; }
    public long Length => _data.Length;
    public byte[] Bytes => _data.ToArray();

    public void Add(Conn.PayloadTransferFrame.Types.PayloadChunk chunk)
    {
        Assert.False(Done, "Chunk after the last one.");
        Assert.Equal(_data.Length, chunk.Offset);
        chunk.Body.WriteTo(_data);
        Chunks++;
        Done = (chunk.Flags & (int)Conn.PayloadTransferFrame.Types.PayloadChunk.Types.Flags.LastChunk) != 0;
    }
}
