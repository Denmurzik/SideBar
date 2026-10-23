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

    // BYTES payloads that carry raw content (text) instead of a Quick Share frame: announced ids, then the finished ones.
    private readonly HashSet<long> _rawExpected = new();
    private readonly HashSet<long> _rawDone = new();
    private Func<long, byte[], bool>? _onRawBytes;

    private readonly string _role;

    /// <summary>True once a write failed: the stream may be mid-frame and must not be written to again.</summary>
    public bool WriteFaulted { get; private set; }
    private readonly SessionOptions _options;

    /// <param name="role">"send" or "receive": prefix of the <see cref="NearbyLog"/> event kinds.</param>
    public NearbyConnection(Stream stream, string role, SessionOptions options)
    {
        _stream = stream;
        _role = role;
        _options = options;
    }

    // Whitelist logging: frame kind, size and enum names only, never payload bytes, names or keys.
    private void Log(string direction, string kind, long? size = null, string? value = null) =>
        NearbyLog.Write(new NearbyLogEvent($"{_role}.{direction}.{kind}", size, value));

    private void LogOffline(string direction, Conn.OfflineFrame frame)
    {
        var v1 = frame.V1;
        if (v1 is null) { Log(direction, "NoV1"); return; }
        if (v1.Type != Conn.V1Frame.Types.FrameType.PayloadTransfer || v1.PayloadTransfer is null)
        {
            Log(direction, v1.Type.ToString());
            return;
        }
        var chunk = v1.PayloadTransfer.PayloadChunk;
        if (chunk is null)
            Log(direction, "PayloadTransfer", null, v1.PayloadTransfer.ControlMessage?.Event.ToString());
        else
            Log(direction, "PayloadTransfer", chunk.Body.Length,
                (chunk.Flags & (int)Conn.PayloadTransferFrame.Types.PayloadChunk.Types.Flags.LastChunk) != 0 ? "LastChunk" : "Chunk");
    }

    /// <summary>
    /// Announces BYTES payloads whose content is raw (not a Quick Share frame). A finished one goes to
    /// <paramref name="onPayload"/>; when it returns true, <see cref="ReadSharingAsync"/> stops and returns null.
    /// </summary>
    public void ExpectRawBytes(IEnumerable<long> payloadIds, Func<long, byte[], bool> onPayload)
    {
        foreach (var id in payloadIds) _rawExpected.Add(id);
        _onRawBytes = onPayload;
    }

    public async ValueTask<byte[]> ReadPlainAsync(CancellationToken ct)
    {
        var data = await FrameIO.ReadAsync(_stream, ct).ConfigureAwait(false);
        Log("rx", "Plain", data.Length);
        return data;
    }

    public async Task WritePlainAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await FrameIO.WriteAsync(_stream, data, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
        Log("tx", "Plain", data.Length);
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
        catch (Exception)
        {
            // A write that failed or was cancelled may have left half a frame on the wire.
            WriteFaulted = true;
            throw;
        }
        finally { _writeLock.Release(); }
        LogOffline("tx", frame);
    }

    public async Task<Conn.OfflineFrame> ReadOfflineAsync(CancellationToken ct)
    {
        var channel = _channel ?? throw new InvalidOperationException("Encryption is not enabled.");
        // The idle timer restarts for every frame, so a keep-alive from the peer counts as life.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_options.IdleTimeout);
        byte[] secure;
        try { secure = await FrameIO.ReadAsync(_stream, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SessionTimeoutException("The peer was silent for too long.");
        }
        var frame = ParseOffline(channel.Decrypt(secure));
        LogOffline("rx", frame);
        return frame;
    }

    /// <summary>Sends a Quick Share frame as a BYTES payload: the whole message, then an empty LAST_CHUNK.</summary>
    public async Task SendSharingAsync(Share.Frame frame, CancellationToken ct)
    {
        Log("tx", "Sharing", null, frame.V1?.Type.ToString());
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
                    var transfer = v1.PayloadTransfer ?? throw new InvalidDataException("PayloadTransfer frame without a body.");
                    if (transfer.PacketType == Conn.PayloadTransferFrame.Types.PacketType.Control)
                    {
                        var control = transfer.ControlMessage ?? throw new InvalidDataException("CONTROL frame without a message.");
                        if (control.Event == Conn.PayloadTransferFrame.Types.ControlMessage.Types.EventType.PayloadCanceled)
                            throw new PeerCancelledException("Peer cancelled the payload.");
                        break; // acks and PAYLOAD_ERROR notifications need no action.
                    }
                    var header = transfer.PayloadHeader ?? throw new InvalidDataException("Payload frame without a header.");
                    if (transfer.PayloadChunk is null)
                        throw new InvalidDataException("Payload frame without a chunk.");
                    if (header.Type == Conn.PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.Bytes)
                    {
                        if (_rawDone.Contains(header.Id))
                            throw new InvalidDataException("BYTES payload id repeated.");
                        var complete = AppendBytesChunk(transfer);
                        if (complete is not null)
                        {
                            if (_rawExpected.Remove(header.Id))
                            {
                                _rawDone.Add(header.Id);
                                if (_onRawBytes!(header.Id, complete)) return null;
                                break;
                            }
                            try
                            {
                                var sharing = Share.Frame.Parser.ParseFrom(complete);
                                Log("rx", "Sharing", null, sharing.V1?.Type.ToString());
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

    private byte[]? AppendBytesChunk(Conn.PayloadTransferFrame transfer)
    {
        var header = transfer.PayloadHeader;
        var chunk = transfer.PayloadChunk;
        if (header.TotalSize < 0 || header.TotalSize > _options.MaxBytesPayload)
            throw new InvalidDataException("BYTES payload too large.");

        if (!_bytesPayloads.TryGetValue(header.Id, out var buffer))
        {
            if (_bytesPayloads.Count >= _options.MaxConcurrentBytesPayloads)
                throw new InvalidDataException("Too many BYTES payloads in flight.");
            _bytesPayloads[header.Id] = buffer = new MemoryStream();
        }
        if (chunk.Offset != buffer.Length)
            throw new InvalidDataException($"Unexpected BYTES chunk offset {chunk.Offset}, expected {buffer.Length}.");
        if (buffer.Length + chunk.Body.Length > _options.MaxBytesPayload)
            throw new InvalidDataException("BYTES payload too large.");
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
