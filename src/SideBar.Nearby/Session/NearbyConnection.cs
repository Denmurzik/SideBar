using System.Net;
using Google.Protobuf;
using SideBar.Nearby.Crypto;
using SideBar.Nearby.Wire;
using Bwu = SideBar.Nearby.Protocol.Connections.BandwidthUpgradeNegotiationFrame.Types.EventType;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Session;

public sealed class PeerDisconnectedException(string message) : IOException(message);

/// <summary>
/// One Nearby Connections link over a stream: plaintext frames before UKEY2 completes, encrypted offline frames after.
/// Writes are serialized, a keep-alive frame goes out every 10 seconds once encryption is on. When allowed, the peer
/// can move the link to a Wi-Fi LAN socket mid-session (bandwidth upgrade) under the same encryption.
/// </summary>
internal sealed class NearbyConnection : IAsyncDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpgradeStepTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PriorChannelGoodbyeTimeout = TimeSpan.FromSeconds(2);

    // While the prior channel drains, writes are held up to this much; beyond it writers wait for the new channel.
    private const int HeldWriteLimit = 2 * OfflineFrames.PayloadChunkSize;

    private Stream _stream;
    private bool _ownsStream;
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

    // Bandwidth upgrade. We only ever react: the side that accepted the connection (the phone) offers the path.
    private string? _localEndpointId;
    private Func<IPEndPoint, CancellationToken, Task<Stream>>? _connectUpgrade;
    private bool _upgraded;

    // An offer that came before AllowBandwidthUpgrade (the peer is not checked yet); acted on once it is allowed.
    private Conn.BandwidthUpgradeNegotiationFrame? _deferredOffer;

    // Set from our LAST_WRITE_TO_PRIOR_CHANNEL until the peer's SAFE_TO_CLOSE_PRIOR_CHANNEL (the prior channel drains).
    private Stream? _upgradeStream;
    private List<Conn.OfflineFrame>? _held;
    private long _heldBytes;
    private TaskCompletionSource? _resumed;

    private readonly string _role;

    /// <summary>True once a write failed: the stream may be mid-frame and must not be written to again.</summary>
    public bool WriteFaulted { get; private set; }
    private readonly SessionOptions _options;

    /// <param name="role">"send" or "receive": prefix of the <see cref="NearbyLog"/> event kinds.</param>
    /// <param name="stream">Owned by the caller; after a bandwidth upgrade it is closed here and the new socket is ours.</param>
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
            Log(direction, v1.Type.ToString(), null, v1.BandwidthUpgradeNegotiation?.EventType.ToString());
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

    /// <summary>
    /// Accepts the peer's bandwidth-upgrade offers to Wi-Fi LAN (others are declined). Until this call an offer is only
    /// remembered (the latest one), so call it once the peer is trusted; the next read acts on a remembered offer.
    /// </summary>
    /// <param name="localEndpointId">Our endpoint id from the ConnectionRequest; it introduces us on the new socket.</param>
    /// <param name="connect">Opens the TCP connection to the offered address.</param>
    public void AllowBandwidthUpgrade(string localEndpointId, Func<IPEndPoint, CancellationToken, Task<Stream>> connect)
    {
        _localEndpointId = localEndpointId;
        _connectUpgrade = connect;
    }

    public async Task SendOfflineAsync(Conn.OfflineFrame frame, CancellationToken ct)
    {
        if (_channel is null) throw new InvalidOperationException("Encryption is not enabled.");
        while (true)
        {
            Task? resumed = null;
            // Encrypt and write under one lock so the sequence number order matches the wire order.
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_held is null)
                    await WriteLockedAsync(frame, ct).ConfigureAwait(false);
                else if (_heldBytes < HeldWriteLimit)
                {
                    // Queued, not awaited: the reader that completes the upgrade may be this very caller's next step.
                    _held.Add(frame);
                    _heldBytes += frame.CalculateSize();
                }
                else
                    resumed = _resumed!.Task;
            }
            finally { _writeLock.Release(); }
            if (resumed is null) break;
            await resumed.WaitAsync(ct).ConfigureAwait(false);
        }
        LogOffline("tx", frame);
    }

    private async Task WriteLockedAsync(Conn.OfflineFrame frame, CancellationToken ct)
    {
        try { await FrameIO.WriteAsync(_stream, _channel!.Encrypt(frame.ToByteArray()), ct).ConfigureAwait(false); }
        catch (Exception)
        {
            // A write that failed or was cancelled may have left half a frame on the wire.
            WriteFaulted = true;
            throw;
        }
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
    /// Reads until a complete Quick Share frame arrives. Keep-alives are skipped, bandwidth-upgrade frames are handled
    /// here (see <see cref="AllowBandwidthUpgrade"/>); a Disconnection frame raises <see cref="PeerDisconnectedException"/>. FILE payload chunks are passed to
    /// <paramref name="onFileChunk"/> (null means they are not expected); when it returns true reading stops and
    /// null is returned.
    /// </summary>
    public async Task<Share.Frame?> ReadSharingAsync(
        Func<Conn.PayloadTransferFrame, Task<bool>>? onFileChunk, CancellationToken ct)
    {
        if (_deferredOffer is { } offer && _connectUpgrade is not null)
        {
            _deferredOffer = null;
            await OnBandwidthUpgradeAsync(offer, ct).ConfigureAwait(false);
        }
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

                case Conn.V1Frame.Types.FrameType.BandwidthUpgradeNegotiation:
                    if (v1.BandwidthUpgradeNegotiation is { } upgrade)
                        await OnBandwidthUpgradeAsync(upgrade, ct).ConfigureAwait(false);
                    break;

                // KeepAlive and anything else need no action.
            }
        }
    }

    private async Task OnBandwidthUpgradeAsync(Conn.BandwidthUpgradeNegotiationFrame frame, CancellationToken ct)
    {
        switch (frame.EventType)
        {
            case Bwu.UpgradePathAvailable when _connectUpgrade is not null && !_upgraded && _upgradeStream is null:
                await TryUpgradeAsync(frame.UpgradePathInfo, ct).ConfigureAwait(false);
                break;

            case Bwu.UpgradePathAvailable when _connectUpgrade is null:
                _deferredOffer = frame;
                break;

            // The peer will not write to the prior channel again: we have read all of it.
            case Bwu.LastWriteToPriorChannel when _upgradeStream is not null:
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try { await WriteLockedAsync(OfflineFrames.BandwidthUpgrade(Bwu.SafeToClosePriorChannel), ct).ConfigureAwait(false); }
                finally { _writeLock.Release(); }
                Log("tx", "BandwidthUpgradeNegotiation", null, nameof(Bwu.SafeToClosePriorChannel));
                break;

            // The peer has read all we wrote to the prior channel.
            case Bwu.SafeToClosePriorChannel when _upgradeStream is not null:
                await SwapStreamAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Opens the offered socket and introduces us on it (plaintext: the peer does not know yet whose socket it is), then
    /// writes our last frame to the prior channel. Any failure up to that point leaves the session where it is.
    /// </summary>
    private async Task TryUpgradeAsync(Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo? info, CancellationToken ct)
    {
        var endpoint = WifiLanEndpoint(info);
        if (endpoint is null)
        {
            Log("bwu", "Declined", null, info?.Medium.ToString());
            await SendOfflineAsync(OfflineFrames.BandwidthUpgradeFailure(info), ct).ConfigureAwait(false);
            return;
        }

        Stream? stream = null;
        try
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(UpgradeStepTimeout);
            stream = await _connectUpgrade!(endpoint, step.Token).ConfigureAwait(false);
            await FrameIO.WriteAsync(stream, OfflineFrames.BandwidthUpgradeClientIntroduction(_localEndpointId!).ToByteArray(), step.Token)
                .ConfigureAwait(false);
            if (info!.SupportsClientIntroductionAck)
            {
                step.CancelAfter(UpgradeStepTimeout);
                var ack = ParseOffline(await FrameIO.ReadAsync(stream, step.Token).ConfigureAwait(false));
                if (ack.V1?.BandwidthUpgradeNegotiation?.EventType != Bwu.ClientIntroductionAck)
                    throw new InvalidDataException("Expected CLIENT_INTRODUCTION_ACK.");
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            // Not a transfer failure: the peer learns the path did not work and the session stays where it is.
            Log("bwu", "Failed", null, e.GetType().Name);
            await SendOfflineAsync(OfflineFrames.BandwidthUpgradeFailure(info), ct).ConfigureAwait(false);
            return;
        }
        catch (Exception)
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var committed = false;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WriteLockedAsync(OfflineFrames.BandwidthUpgrade(Bwu.LastWriteToPriorChannel), ct).ConfigureAwait(false);
                // From here on nothing else goes to the prior channel except our SAFE_TO_CLOSE answer.
                _upgradeStream = stream;
                _held = [];
                _heldBytes = 0;
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                committed = true;
            }
            finally { _writeLock.Release(); }
        }
        finally
        {
            if (!committed) await stream.DisposeAsync().ConfigureAwait(false);
        }
        Log("tx", "BandwidthUpgradeNegotiation", null, nameof(Bwu.LastWriteToPriorChannel));
    }

    private static IPEndPoint? WifiLanEndpoint(Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo? info)
    {
        if (info is null || info.Medium != Conn.BandwidthUpgradeNegotiationFrame.Types.UpgradePathInfo.Types.Medium.WifiLan
            || info.WifiLanSocket is not { } socket)
            return null;
        if (socket.IpAddress.Length is not (4 or 16) || socket.WifiPort is <= 0 or > IPEndPoint.MaxPort) return null;
        return new IPEndPoint(new IPAddress(socket.IpAddress.Span), socket.WifiPort);
    }

    /// <summary>
    /// Closes the drained prior channel and moves the session, with the same <see cref="SecureChannel"/> and its sequence
    /// numbers, to the upgraded socket; the writes held meanwhile go out there first, in order.
    /// </summary>
    private async Task SwapStreamAsync(CancellationToken ct)
    {
        await ClosePriorChannelAsync(_stream).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _stream = _upgradeStream!;
            _ownsStream = true;
            _upgradeStream = null;
            _upgraded = true;
            var held = _held!;
            _held = null;
            foreach (var frame in held) await WriteLockedAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _resumed!.TrySetResult();
            _writeLock.Release();
        }
        Log("bwu", "Upgraded");
    }

    // As the reference implementation does: an unencrypted Disconnection (it must not use up a sequence number), then a
    // short wait for the peer's, so neither side closes before the other has read SAFE_TO_CLOSE (an Android Bluetooth
    // socket loses unread data when the other end closes).
    private static async Task ClosePriorChannelAsync(Stream prior)
    {
        using var limit = new CancellationTokenSource(PriorChannelGoodbyeTimeout);
        try { await GoodbyeAsync(prior, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false); }
        catch (Exception) { }
        await prior.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task GoodbyeAsync(Stream prior, CancellationToken ct)
    {
        await FrameIO.WriteAsync(prior, OfflineFrames.Disconnection().ToByteArray(), ct).ConfigureAwait(false);
        await FrameIO.ReadAsync(prior, ct).ConfigureAwait(false);
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
        if (_upgradeStream is not null) await _upgradeStream.DisposeAsync().ConfigureAwait(false);
        if (_ownsStream) await _stream.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
