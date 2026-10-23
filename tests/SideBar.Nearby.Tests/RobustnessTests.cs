using SideBar.Nearby.Session;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Tests;

/// <summary>Timeouts, limits, CONTROL frames and protocol errors, driven by hand-made peers over loopback.</summary>
public sealed class RobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidebar-nearby-robust-" + Guid.NewGuid().ToString("N"));
    private readonly string _in;
    private readonly string _out;

    public RobustnessTests()
    {
        _in = Path.Combine(_dir, "in");
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_in);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CancellationToken Token(int seconds = 30) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private static Func<IncomingTransfer, CancellationToken, Task<bool>> AcceptAll => (_, _) => Task.FromResult(true);

    private Task<TransferResult> Receive(Link link, CancellationToken ct, SessionOptions? options = null,
        Func<IncomingTransfer, CancellationToken, Task<bool>>? consent = null, string? output = null) =>
        ReceiveSession.ReceiveAsync(link.Server, options ?? new SessionOptions("Receiver"), output ?? _out,
            consent ?? AcceptAll, null, ct);

    private string MakeFile(string name, long size)
    {
        var path = Path.Combine(_in, name);
        using var f = File.Create(path);
        f.SetLength(size);
        return path;
    }

    [Fact]
    public async Task Peer_cancel_mid_file_ends_session_cleanly()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 1_000_000, "secret-name.bin"));
        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Accept, await peer.ReadResponseAsync(ct));
        await peer.SendOfflineAsync(TestFrames.FileChunk(7, 1_000_000, 0, new byte[1000], false), ct);
        await peer.SendOfflineAsync(TestFrames.Control(7,
            Conn.PayloadTransferFrame.Types.ControlMessage.Types.EventType.PayloadCanceled), ct);

        var result = await receive;
        Assert.Equal(TransferStatus.CancelledByPeer, result.Status);
        Assert.DoesNotContain("secret-name", result.Detail ?? "");
        Assert.False(Directory.EnumerateFileSystemEntries(_out).Any());
    }

    [Fact]
    public async Task Control_ack_frames_are_ignored()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 10, "a.bin"));
        await peer.ReadResponseAsync(ct);
        var ack = Conn.PayloadTransferFrame.Types.ControlMessage.Types.EventType.PayloadReceivedAck;
        await peer.SendOfflineAsync(TestFrames.Control(7, ack), ct);
        await peer.SendOfflineAsync(TestFrames.FileChunk(7, 10, 0, new byte[5], false), ct);
        await peer.SendOfflineAsync(TestFrames.Control(7, Conn.PayloadTransferFrame.Types.ControlMessage.Types.EventType.PayloadError), ct);
        await peer.SendOfflineAsync(TestFrames.FileChunk(7, 10, 5, new byte[5], true), ct);
        await peer.DisconnectAsync(ct);

        var result = await receive;
        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Equal(10, new FileInfo(Assert.Single(result.Files)).Length);
    }

    [Theory]
    [InlineData(0)] // PayloadTransfer frame type without a PayloadTransfer body
    [InlineData(1)] // DATA without a chunk
    [InlineData(2)] // CONTROL without a message
    [InlineData(3)] // Introduction frame without an Introduction body
    [InlineData(4)] // PairedKeyEncryption frame without a body
    public async Task Missing_submessage_is_protocol_error_not_crash(int kind)
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        switch (kind)
        {
            case 0:
                await peer.SendOfflineAsync(TestFrames.Offline(new Conn.V1Frame
                    { Type = Conn.V1Frame.Types.FrameType.PayloadTransfer }), ct);
                break;
            case 1:
                // Only after an accepted Introduction: a chunk-less DATA frame for an announced file.
                await peer.IntroduceAsync(ct, TestFrames.File(1, 1));
                await peer.ReadResponseAsync(ct);
                await peer.SendOfflineAsync(TestFrames.Offline(new Conn.V1Frame
                {
                    Type = Conn.V1Frame.Types.FrameType.PayloadTransfer,
                    PayloadTransfer = new Conn.PayloadTransferFrame
                    {
                        PacketType = Conn.PayloadTransferFrame.Types.PacketType.Data,
                        PayloadHeader = SideBar.Nearby.Session.OfflineFrames.FileHeader(1, 1, "x"),
                    },
                }), ct);
                break;
            case 2:
                await peer.SendOfflineAsync(TestFrames.Offline(new Conn.V1Frame
                {
                    Type = Conn.V1Frame.Types.FrameType.PayloadTransfer,
                    PayloadTransfer = new Conn.PayloadTransferFrame
                        { PacketType = Conn.PayloadTransferFrame.Types.PacketType.Control },
                }), ct);
                break;
            case 3:
                await peer.SendSharingAsync(TestFrames.Of(Share.V1Frame.Types.FrameType.Introduction), ct);
                break;
            default:
                await peer.SendSharingAsync(TestFrames.Of(Share.V1Frame.Types.FrameType.PairedKeyEncryption), ct);
                break;
        }

        Assert.Equal(TransferStatus.ProtocolError, (await receive).Status);
    }

    [Fact]
    public async Task Silent_peer_times_out_after_idle_timeout()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var options = new SessionOptions("R") { IdleTimeout = TimeSpan.FromMilliseconds(400) };
        var receive = Receive(link, ct, options);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);

        Assert.Equal(TransferStatus.TimedOut, (await receive).Status);

        // The next session is not blocked by the dead one.
        using var second = await Link.OpenAsync(ct);
        var again = Receive(second, ct, options);
        await using var peer2 = await FakeSender.ConnectAsync(second.Client, ct);
        Assert.Equal(TransferStatus.TimedOut, (await again).Status);
    }

    [Fact]
    public async Task Handshake_timeout()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var options = new SessionOptions("R") { HandshakeTimeout = TimeSpan.FromMilliseconds(300) };

        Assert.Equal(TransferStatus.TimedOut, (await Receive(link, ct, options)).Status);
    }

    [Fact]
    public async Task Not_enough_free_space_rejects_before_asking_the_user()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var options = new SessionOptions("R") { FreeBytes = _ => 100L * 1024 * 1024 };
        var asked = false;
        var receive = Receive(link, ct, options, (_, _) => { asked = true; return Task.FromResult(true); });
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 40L * 1024 * 1024));

        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Reject, await peer.ReadResponseAsync(ct));
        var result = await receive;
        Assert.Equal(TransferStatus.RejectedByPeer, result.Status);
        Assert.Equal("not-enough-space", result.Detail);
        Assert.False(asked);
    }

    [Fact]
    public async Task User_decision_timeout_rejects()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var options = new SessionOptions("R") { DecisionTimeout = TimeSpan.FromMilliseconds(300) };
        var receive = Receive(link, ct, options, async (_, t) =>
        {
            await Task.Delay(Timeout.Infinite, t);
            return true;
        });
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 10));

        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Reject, await peer.ReadResponseAsync(ct));
        Assert.Equal(TransferStatus.TimedOut, (await receive).Status);
    }

    [Theory]
    [InlineData(0)] // declared total over the limit
    [InlineData(1)] // declared small, body over the limit
    [InlineData(2)] // more payloads in flight than allowed
    public async Task Bytes_payload_over_limit_is_rejected(int kind)
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        switch (kind)
        {
            case 0:
                await peer.SendOfflineAsync(OfflineFrames.PayloadChunk(OfflineFrames.BytesHeader(1, 100_000), 0, new byte[10], false), ct);
                break;
            case 1:
                await peer.SendOfflineAsync(OfflineFrames.PayloadChunk(OfflineFrames.BytesHeader(1, 10), 0, new byte[70_000], false), ct);
                break;
            default:
                for (var id = 1; id <= 9; id++)
                    await peer.SendOfflineAsync(OfflineFrames.PayloadChunk(OfflineFrames.BytesHeader(id, 10), 0, new byte[1], false), ct);
                break;
        }

        Assert.Equal(TransferStatus.ProtocolError, (await receive).Status);
    }

    [Fact]
    public async Task File_chunk_beyond_declared_size_is_rejected()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 10, "private.bin"));
        await peer.ReadResponseAsync(ct);
        await peer.SendOfflineAsync(TestFrames.FileChunk(7, 10, 0, new byte[20], false), ct);

        var result = await receive;
        Assert.Equal(TransferStatus.ProtocolError, result.Status);
        Assert.DoesNotContain("private", result.Detail ?? "");
        Assert.False(Directory.EnumerateFileSystemEntries(_out).Any());
    }

    [Fact]
    public async Task Duplicate_introduction_is_protocol_error()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 10));
        await peer.ReadResponseAsync(ct);
        await peer.IntroduceAsync(ct, TestFrames.File(8, 10));

        Assert.Equal(TransferStatus.ProtocolError, (await receive).Status);
    }

    [Fact]
    public async Task Duplicate_payload_id_in_introduction_is_protocol_error()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 10, "a"), TestFrames.File(7, 10, "b"));

        Assert.Equal(TransferStatus.ProtocolError, (await receive).Status);
    }

    [Fact]
    public async Task Negative_size_is_protocol_error()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, -5));

        Assert.Equal(TransferStatus.ProtocolError, (await receive).Status);
    }

    [Fact]
    public async Task Too_many_files_rejected()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, Enumerable.Range(1, 101).Select(i => TestFrames.File(i, 1)).ToArray());

        Assert.NotEqual(Share.ConnectionResponseFrame.Types.Status.Accept, await peer.ReadResponseAsync(ct));
        var result = await receive;
        Assert.Equal(TransferStatus.ProtocolError, result.Status);
        Assert.False(Directory.Exists(_out) && Directory.EnumerateFileSystemEntries(_out).Any());
    }

    [Theory]
    [InlineData(false)] // Quick Share Cancel frame
    [InlineData(true)] // CONTROL PAYLOAD_CANCELED
    public async Task Sender_reads_peer_cancel_during_send(bool control)
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        // Far more than the socket buffers hold: the sender is blocked writing when the cancel arrives.
        var big = MakeFile("big.bin", 64L * 1024 * 1024);
        var send = SendSession.SendFilesAsync(link.Client, new SessionOptions("S"), [big], null, ct);
        await using var peer = await FakeReceiver.AcceptAsync(link.Server, ct);
        await peer.AcceptTransferAsync(ct);
        if (control)
            await peer.SendOfflineAsync(TestFrames.Control(peer.Introduction.FileMetadata[0].PayloadId,
                Conn.PayloadTransferFrame.Types.ControlMessage.Types.EventType.PayloadCanceled), ct);
        else
            await peer.SendSharingAsync(TestFrames.Of(Share.V1Frame.Types.FrameType.Cancel), ct);

        Assert.Equal(TransferStatus.CancelledByPeer, (await send).Status);
    }

    [Fact]
    public async Task Sender_sends_cancel_when_the_user_cancels()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        using var user = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var observer = new CancelOnProgress(user);
        var file = MakeFile("c.bin", 8L * 1024 * 1024);
        var send = SendSession.SendFilesAsync(link.Client, new SessionOptions("S"), [file], observer, user.Token);
        await using var peer = await FakeReceiver.AcceptAsync(link.Server, ct);
        await peer.AcceptTransferAsync(ct);

        await peer.ReadAsync(Share.V1Frame.Types.FrameType.Cancel, ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    private sealed class CancelOnProgress(CancellationTokenSource cts) : ITransferObserver
    {
        void ITransferObserver.Progress(TransferProgress progress) => cts.Cancel();
    }

    [Fact]
    public async Task Sender_open_failure_reports_and_leaks_nothing()
    {
        var ct = Token();
        var good = MakeFile("good.bin", 10);
        var missing = Path.Combine(_in, "missing-secret.bin");

        var result = await SendSession.SendFilesAsync(new MemoryStream(), new SessionOptions("S"), [good, missing], null, ct);

        Assert.Equal(TransferStatus.Failed, result.Status);
        Assert.DoesNotContain("secret", result.Detail ?? "");
        // The first file was released: it can be opened exclusively.
        using var exclusive = new FileStream(good, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Three_receives_run_in_parallel()
    {
        var ct = Token();
        var offers = new List<IncomingTransfer>();
        var sessions = Enumerable.Range(0, 3).Select(async i =>
        {
            using var link = await Link.OpenAsync(ct);
            var file = MakeFile($"p{i}.bin", 100_000 + i);
            var receive = ReceiveSession.ReceiveAsync(link.Server, new SessionOptions("R" + i), _out,
                (o, _) => { lock (offers) offers.Add(o); return Task.FromResult(true); }, null, ct);
            var send = await SendSession.SendFilesAsync(link.Client, new SessionOptions("S" + i), [file], null, ct);
            return (send.Status, (await receive).Status);
        }).ToList();

        foreach (var (send, receive) in await Task.WhenAll(sessions))
        {
            Assert.Equal(TransferStatus.Completed, send);
            Assert.Equal(TransferStatus.Completed, receive);
        }
        Assert.Equal(3, Directory.GetFiles(_out).Length);
        Assert.All(offers, o => Assert.False(o.Verified));
    }

    [Fact]
    public async Task Consent_that_ignores_the_token_still_times_out()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var options = new SessionOptions("R") { DecisionTimeout = TimeSpan.FromMilliseconds(300) };
        var never = new TaskCompletionSource<bool>();
        var receive = Receive(link, ct, options, (_, _) => never.Task);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.IntroduceAsync(ct, TestFrames.File(7, 10));

        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Reject, await peer.ReadResponseAsync(ct));
        Assert.Equal(TransferStatus.TimedOut, (await receive).Status);
    }

    [Fact]
    public async Task Keep_alives_keep_the_session_alive()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var options = new SessionOptions("R") { IdleTimeout = TimeSpan.FromSeconds(1) };
        var receive = Receive(link, ct, options);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        for (var i = 0; i < 30; i++)
        {
            await peer.SendOfflineAsync(OfflineFrames.KeepAlive(ack: false), ct);
            await Task.Delay(100, ct);
        }
        await peer.IntroduceAsync(ct, TestFrames.File(7, 10));

        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Accept, await peer.ReadResponseAsync(ct));
        Assert.Equal(TransferStatus.TimedOut, (await receive).Status); // silent after that
    }

    [Fact]
    public async Task Sender_maps_cancel_seen_during_the_last_chunks_to_cancelled_by_peer()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var file = MakeFile("tiny.bin", 1);
        var send = SendSession.SendFilesAsync(link.Client, new SessionOptions("S"), [file], new SlowProgress(), ct);
        await using var peer = await FakeReceiver.AcceptAsync(link.Server, ct);
        await peer.AcceptTransferAsync(ct);
        await peer.SendSharingAsync(TestFrames.Of(Share.V1Frame.Types.FrameType.Cancel), ct);

        Assert.Equal(TransferStatus.CancelledByPeer, (await send).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sender_maps_cancel_after_send_finished_to_cancelled_by_peer(bool control)
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var file = MakeFile("late.bin", 10);
        var send = SendSession.SendFilesAsync(link.Client, new SessionOptions("S"), [file], null, ct);
        await using var peer = await FakeReceiver.AcceptAsync(link.Server, ct);
        await peer.AcceptTransferAsync(ct);
        await peer.ReadUntilLastFileChunkAsync(ct);
        // Everything is sent: the sender now waits for our Disconnection and must keep doing so after the cancel.
        if (control)
            await peer.SendOfflineAsync(TestFrames.Control(1,
                Conn.PayloadTransferFrame.Types.ControlMessage.Types.EventType.PayloadCanceled), ct);
        else
            await peer.SendSharingAsync(TestFrames.Of(Share.V1Frame.Types.FrameType.Cancel), ct);
        await peer.SendOfflineAsync(OfflineFrames.Disconnection(), ct);

        Assert.Equal(TransferStatus.CancelledByPeer, (await send).Status);
    }

    private sealed class SlowProgress : ITransferObserver
    {
        // Holds the sender after its first chunk so the reader has surely seen the peer's cancel.
        void ITransferObserver.Progress(TransferProgress progress) => Thread.Sleep(500);
    }

    [Fact]
    public async Task Sender_reports_failure_when_the_peer_drops_the_connection()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var big = MakeFile("drop.bin", 64L * 1024 * 1024);
        var send = SendSession.SendFilesAsync(link.Client, new SessionOptions("S"), [big], null, ct);
        var peer = await FakeReceiver.AcceptAsync(link.Server, ct);
        await peer.AcceptTransferAsync(ct);
        link.Server.Dispose();

        Assert.Equal(TransferStatus.Failed, (await send).Status);
        await peer.DisposeAsync();
    }
}
