using Google.Protobuf;
using SideBar.Nearby.Crypto;
using SideBar.Nearby.Wire;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Session;

public sealed class PeerDisconnectedException(string message) : IOException(message);

/// <summary>
/// One Nearby Connections link over a stream: plaintext frames before UKEY2 completes, encrypted offline frames after.
/// Writes are serialized, a keep-alive frame goes out every 10 seconds once encryption is on.
/// </summary>
internal sealed class NearbyConnection : IAsyncDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Random _idSource = new();
    private SecureChannel? _channel;
    private Task? _keepAlive;

    // In-flight BYTES payloads (negotiation messages), keyed by payload id.
    private readonly Dictionary<long, MemoryStream> _bytesPayloads = new();

    private readonly Action<string>? _trace;

    public NearbyConnection(Stream stream, Action<string>? trace = null)
    {
        _stream = stream;
        _trace = trace;
    }

    private static readonly JsonFormatter TraceFormatter = new(JsonFormatter.Settings.Default.WithIndentation(""));
    private static readonly System.Text.RegularExpressions.Regex LongBase64 = new("\"[A-Za-z0-9+/=_-]{24,}\"");

    // Field names and values for diagnostics; long binary values (keys, signatures, random data) are reduced to a length.
    private static string TraceJson(IMessage message) =>
        LongBase64.Replace(TraceFormatter.Format(message).ReplaceLineEndings(" "), m => $"\"<{m.Length - 2} chars>\"");

    private void Trace(string direction, string what) => _trace?.Invoke($"{direction} {what}");

    // Frame types and sizes only, never payload bytes or keys.
    private static string Describe(Conn.OfflineFrame frame)
    {
        var v1 = frame.V1;
        if (v1 is null) return "OfflineFrame(no v1)";
        if (v1.Type != Conn.V1Frame.Types.FrameType.PayloadTransfer || v1.PayloadTransfer is null)
            return $"{v1.Type} {TraceJson(v1)}";
        var t = v1.PayloadTransfer;
        var chunk = t.PayloadChunk;
        return $"PayloadTransfer {t.PacketType} header={(t.PayloadHeader is null ? "none" : TraceJson(t.PayloadHeader))} " +
               (chunk is null ? $"control={t.ControlMessage?.Event}" : $"offset={chunk.Offset} len={chunk.Body.Length} flags={chunk.Flags}");
    }

    public async ValueTask<byte[]> ReadPlainAsync(CancellationToken ct)
    {
        var data = await FrameIO.ReadAsync(_stream, ct).ConfigureAwait(false);
        Trace("<-", $"plain frame {data.Length} bytes");
        return data;
    }

    public async Task WritePlainAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await FrameIO.WriteAsync(_stream, data, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
        Trace("->", $"plain frame {data.Length} bytes");
    }

    public Task WritePlainAsync(IMessage message, CancellationToken ct) => WritePlainAsync(message.ToByteArray(), ct);

    public async Task<Conn.OfflineFrame> ReadPlainOfflineAsync(CancellationToken ct) =>
        ParseOffline(await ReadPlainAsync(ct).ConfigureAwait(false));

    public void EnableEncryption(SecureChannel channel)
    {
        _channel = channel;
        _keepAlive = Task.Run(KeepAliveLoopAsync);
    }

    public async Task SendOfflineAsync(Conn.OfflineFrame frame, CancellationToken ct)
    {
        var channel = _channel ?? throw new InvalidOperationException("Encryption is not enabled.");
        // Encrypt and write under one lock so the sequence number order matches the wire order.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await FrameIO.WriteAsync(_stream, channel.Encrypt(frame.ToByteArray()), ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
        Trace("->", Describe(frame));
    }

    public async Task<Conn.OfflineFrame> ReadOfflineAsync(CancellationToken ct)
    {
        var channel = _channel ?? throw new InvalidOperationException("Encryption is not enabled.");
        var secure = await FrameIO.ReadAsync(_stream, ct).ConfigureAwait(false);
        var frame = ParseOffline(channel.Decrypt(secure));
        Trace("<-", Describe(frame));
        return frame;
    }

    /// <summary>Sends a Quick Share frame as a BYTES payload: the whole message, then an empty LAST_CHUNK.</summary>
    public async Task SendSharingAsync(Share.Frame frame, CancellationToken ct)
    {
        Trace("->", $"Sharing {frame.V1?.Type} {TraceJson(frame)}");
        var data = frame.ToByteArray();
        var header = OfflineFrames.BytesHeader(NextPayloadId(), data.Length);
        await SendOfflineAsync(OfflineFrames.PayloadChunk(header, 0, data, last: false), ct).ConfigureAwait(false);
        await SendOfflineAsync(OfflineFrames.PayloadChunk(header, data.Length, [], last: true), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads until a complete Quick Share frame arrives. Keep-alives and bandwidth-upgrade frames are skipped;
    /// a Disconnection frame raises <see cref="PeerDisconnectedException"/>. FILE payload chunks are passed to
    /// <paramref name="onFileChunk"/> (null means they are not expected); when it returns true reading stops and
    /// null is returned.
    /// </summary>
    public async Task<Share.Frame?> ReadSharingAsync(
        Func<Conn.PayloadTransferFrame, Task<bool>>? onFileChunk, CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReadOfflineAsync(ct).ConfigureAwait(false);
            var v1 = frame.V1;
            switch (v1?.Type)
            {
                case Conn.V1Frame.Types.FrameType.Disconnection:
                    throw new PeerDisconnectedException("Peer sent a Disconnection frame.");

                case Conn.V1Frame.Types.FrameType.PayloadTransfer:
                    var transfer = v1.PayloadTransfer;
                    var header = transfer.PayloadHeader;
                    if (header.Type == Conn.PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.Bytes)
                    {
                        var complete = AppendBytesChunk(transfer);
                        if (complete is not null)
                        {
                            try
                            {
                                var sharing = Share.Frame.Parser.ParseFrom(complete);
                                Trace("<-", $"Sharing {sharing.V1?.Type} {TraceJson(sharing)}");
                                return sharing;
                            }
                            catch (InvalidProtocolBufferException e)
                            {
                                throw new InvalidDataException("Bad Quick Share frame: " + e.Message);
                            }
                        }
                    }
                    else if (header.Type == Conn.PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.File)
                    {
                        if (onFileChunk is null)
                            throw new InvalidDataException("Unexpected FILE payload.");
                        if (await onFileChunk(transfer).ConfigureAwait(false)) return null;
                    }
                    break;

                // KeepAlive, BandwidthUpgradeNegotiation and anything else: not needed on Wi-Fi LAN without upgrade.
            }
        }
    }

    /// <summary>Reads and discards frames until the peer disconnects, closes the stream or the timeout passes.</summary>
    public async Task WaitForPeerDisconnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var frame = await ReadOfflineAsync(cts.Token).ConfigureAwait(false);
                if (frame.V1?.Type == Conn.V1Frame.Types.FrameType.Disconnection) return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidDataException or SecureChannelException) { }
    }

    private byte[]? AppendBytesChunk(Conn.PayloadTransferFrame transfer)
    {
        var header = transfer.PayloadHeader;
        var chunk = transfer.PayloadChunk;
        if (header.TotalSize > FrameIO.MaxFrameLength)
            throw new InvalidDataException("BYTES payload too large.");

        if (!_bytesPayloads.TryGetValue(header.Id, out var buffer))
            _bytesPayloads[header.Id] = buffer = new MemoryStream();
        if (chunk.Offset != buffer.Length)
            throw new InvalidDataException($"Unexpected BYTES chunk offset {chunk.Offset}, expected {buffer.Length}.");
        chunk.Body.WriteTo(buffer);

        if ((chunk.Flags & (int)Conn.PayloadTransferFrame.Types.PayloadChunk.Types.Flags.LastChunk) == 0) return null;
        _bytesPayloads.Remove(header.Id);
        return buffer.ToArray();
    }

    private long NextPayloadId() => _idSource.NextInt64(long.MinValue, long.MaxValue);

    private static Conn.OfflineFrame ParseOffline(byte[] data)
    {
        try { return Conn.OfflineFrame.Parser.ParseFrom(data); }
        catch (InvalidProtocolBufferException e) { throw new InvalidDataException("Bad offline frame: " + e.Message); }
    }

    private async Task KeepAliveLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                await SendOfflineAsync(OfflineFrames.KeepAlive(ack: false), _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_keepAlive is not null)
        {
            try { await _keepAlive.ConfigureAwait(false); } catch (Exception) { }
        }
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
