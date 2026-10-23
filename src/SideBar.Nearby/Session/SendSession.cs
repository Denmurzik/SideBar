using SideBar.Nearby.Crypto;
using SideBar.Nearby.Wire;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Session;

/// <summary>Client (sending) side of a Quick Share transfer over an already connected stream.</summary>
public static class SendSession
{
    public static async Task<TransferResult> SendFilesAsync(
        Stream stream,
        SessionOptions options,
        IReadOnlyList<string> files,
        ITransferObserver? observer = null,
        CancellationToken ct = default)
    {
        observer ??= new NullObserver();
        var identity = options.ResolvedIdentity;
        var entries = new List<OutgoingFile>();
        var encrypted = false;

        await using var connection = new NearbyConnection(stream, "send", options);
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(options.HandshakeTimeout);
        var hct = handshakeCts.Token;
        try
        {
            // Files are opened inside the try so that a failure on the Nth one still releases the first N-1.
            foreach (var path in files) entries.Add(OutgoingFile.Create(path));

            // 1. ConnectionRequest + UKEY2 ClientInit (plaintext).
            var info = identity.CreateEndpointInfo(options.DeviceName, options.DeviceKind);
            await connection.WritePlainAsync(
                OfflineFrames.ConnectionRequest(MdnsName.NewEndpointId(), options.DeviceName, info.Encode()), hct);

            using var ukey = new Ukey2Client();
            await connection.WritePlainAsync(ukey.CreateClientInit(), hct);

            // 2. ServerInit -> ClientFinish + our ConnectionResponse.
            var serverInit = await connection.ReadPlainAsync(hct);
            var (clientFinish, handshake) = ukey.ProcessServerInit(serverInit);
            await connection.WritePlainAsync(clientFinish, hct);
            await connection.WritePlainAsync(OfflineFrames.ConnectionResponse(accept: true), hct);

            var pin = Pin.FromAuthString(handshake.AuthString);
            observer.PinReady(pin);

            // 3. Peer ConnectionResponse (plaintext), then everything is encrypted.
            var response = await connection.ReadPlainOfflineAsync(hct);
            if (response.V1?.ConnectionResponse?.Response != Conn.ConnectionResponseFrame.Types.ResponseStatus.Accept)
                return new TransferResult(TransferStatus.RejectedByPeer, [], "Peer rejected the connection.");
            handshakeCts.CancelAfter(Timeout.InfiniteTimeSpan);
            connection.EnableEncryption(SecureChannel.ForClient(handshake.NextSecret));
            encrypted = true;

            // 4. Paired key exchange, then Introduction, then wait for the user's decision.
            await connection.SendSharingAsync(SharingFrames.PairedKeyEncryption(
                identity.CreatePairedKeyEncryption(handshake.AuthString, PairedKeyRole.Sender, options.RemoteEndpointInfo)), ct);

            var introduced = false;
            var gotKeyEncryption = false;
            var gotKeyResult = false;
            while (true)
            {
                var frame = (await connection.ReadSharingAsync(null, ct))!;
                var v1 = frame.V1;
                switch (v1?.Type)
                {
                    case Share.V1Frame.Types.FrameType.PairedKeyEncryption:
                        if (v1.PairedKeyEncryption is null)
                            throw new InvalidDataException("PairedKeyEncryption frame without a body.");
                        gotKeyEncryption = true;
                        var status = identity.VerifyPairedKey(v1.PairedKeyEncryption, handshake.AuthString, PairedKeyRole.Sender, options.RemoteEndpointInfo);
                        await connection.SendSharingAsync(SharingFrames.PairedKeyResult(status), ct);
                        break;

                    case Share.V1Frame.Types.FrameType.PairedKeyResult:
                        gotKeyResult = true;
                        break;

                    case Share.V1Frame.Types.FrameType.Cancel:
                        return new TransferResult(TransferStatus.CancelledByPeer, []);

                    case Share.V1Frame.Types.FrameType.Response:
                        if (!introduced)
                            throw new InvalidDataException("Response before Introduction.");
                        if (v1.ConnectionResponse is null)
                            throw new InvalidDataException("Response frame without a body.");
                        return await HandleResponseAsync(connection, v1.ConnectionResponse.Status, entries, observer, options, ct);
                }

                if (!introduced && gotKeyEncryption && gotKeyResult)
                {
                    introduced = true;
                    await connection.SendSharingAsync(SharingFrames.Introduction(entries.Select(e => e.Metadata)), ct);
                    observer.Status("Introduction sent, waiting for the receiver to accept...");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user cancelled: tell the phone instead of just dropping the socket.
            if (encrypted && !connection.WriteFaulted) await TrySendCancelAsync(connection);
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TransferResult(TransferStatus.TimedOut, [], "Handshake timed out.");
        }
        catch (Exception e) when (SessionErrors.IsHandled(e))
        {
            return SessionErrors.ToResult(e);
        }
        finally
        {
            foreach (var entry in entries) entry.Dispose();
        }
    }

    private static async Task TrySendCancelAsync(NearbyConnection connection)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await connection.SendSharingAsync(SharingFrames.Cancel(), limit.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    /// <summary>What the background reader learned about the peer while we were sending.</summary>
    private sealed class PeerWatch
    {
        public volatile TransferResult? Abort;
        public volatile bool SendFinished;
    }

    private static async Task<TransferResult> HandleResponseAsync(
        NearbyConnection connection,
        Share.ConnectionResponseFrame.Types.Status status,
        List<OutgoingFile> entries,
        ITransferObserver observer,
        SessionOptions options,
        CancellationToken ct)
    {
        if (status != Share.ConnectionResponseFrame.Types.Status.Accept)
            return new TransferResult(TransferStatus.RejectedByPeer, [], $"Receiver answered {status}.");

        observer.Status("Accepted, sending...");

        // The peer may cancel or vanish mid-transfer. Reading is done by one task for the whole phase (a read must
        // never be cancelled half-way through a frame), and it cancels the sending when something goes wrong.
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = new PeerWatch();
        var reader = Task.Run(() => WatchPeerAsync(connection, watch, sendCts, readerCts.Token));
        try
        {
            try { await SendAllAsync(connection, entries, observer, sendCts.Token); }
            catch (Exception e) when (!ct.IsCancellationRequested
                && (e is OperationCanceledException || SessionErrors.IsHandled(e)) && watch.Abort is not null)
            {
                return watch.Abort;
            }
            watch.SendFinished = true;

            // Do not slam the socket shut: closing with unread data makes TCP send a RST, which can discard what the
            // receiver has not processed yet (seen as "failed to send" on the phone). Let the receiver finish first.
            await Task.WhenAny(reader, Task.Delay(options.FinishTimeout, ct));
            ct.ThrowIfCancellationRequested();
            try { await connection.SendOfflineAsync(OfflineFrames.Disconnection(), ct); }
            catch (IOException) { }
            await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(2), ct));
            ct.ThrowIfCancellationRequested();
            return new TransferResult(TransferStatus.Completed, entries.Select(e => e.Metadata.Name).ToList());
        }
        finally
        {
            await readerCts.CancelAsync();
            await reader;
        }
    }

    private static async Task SendAllAsync(
        NearbyConnection connection, List<OutgoingFile> entries, ITransferObserver observer, CancellationToken ct)
    {
        var total = entries.Sum(e => e.Size);
        long sentTotal = 0;
        var buffer = new byte[OfflineFrames.PayloadChunkSize];
        foreach (var entry in entries)
        {
            var header = OfflineFrames.FileHeader(entry.Metadata.PayloadId, entry.Size, entry.Metadata.Name);
            long sent = 0;
            while (true)
            {
                var read = await entry.Stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                await connection.SendOfflineAsync(OfflineFrames.PayloadChunk(header, sent, buffer.AsSpan(0, read), last: false), ct);
                sent += read;
                sentTotal += read;
                observer.Progress(new TransferProgress(entry.Metadata.Name, sent, entry.Size, sentTotal, total));
            }
            if (sent != entry.Size)
                throw new SafeIoException("A file changed size while sending.");
            await connection.SendOfflineAsync(OfflineFrames.PayloadChunk(header, sent, [], last: true), ct);
        }
    }

    private static async Task WatchPeerAsync(
        NearbyConnection connection, PeerWatch watch, CancellationTokenSource sendCts, CancellationToken token)
    {
        void Abort(TransferResult result)
        {
            watch.Abort ??= result;
            sendCts.Cancel();
        }

        while (true)
        {
            try
            {
                var frame = await connection.ReadSharingAsync(null, token);
                // Once everything is sent, anything but the Disconnection is of no interest (as before).
                if (frame?.V1?.Type == Share.V1Frame.Types.FrameType.Cancel && !watch.SendFinished)
                {
                    Abort(new TransferResult(TransferStatus.CancelledByPeer, [], "Receiver cancelled the transfer."));
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (PeerCancelledException e) when (!watch.SendFinished)
            {
                Abort(SessionErrors.ToResult(e));
                return;
            }
            catch (PeerCancelledException) { }
            catch (Exception e) when (SessionErrors.IsHandled(e))
            {
                if (!watch.SendFinished) Abort(SessionErrors.ToResult(e));
                return;
            }
        }
    }

    private sealed class NullObserver : ITransferObserver;

    private sealed class OutgoingFile : IDisposable
    {
        public required Share.FileMetadata Metadata { get; init; }
        public required FileStream Stream { get; init; }
        public long Size => Metadata.Size;

        public static OutgoingFile Create(string path)
        {
            FileStream stream;
            try
            {
                // ReadWrite: files that another program keeps open for writing (logs, running exports) still send.
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The original message carries the path.
                throw new SafeIoException("A file to send cannot be opened.");
            }
            var name = Path.GetFileName(path);
            var mime = MimeTypes.FromFileName(name);
            return new OutgoingFile
            {
                Stream = stream,
                Metadata = new Share.FileMetadata
                {
                    Name = name,
                    Size = stream.Length,
                    MimeType = mime,
                    Id = Random.Shared.NextInt64(long.MinValue, long.MaxValue),
                    PayloadId = Random.Shared.NextInt64(long.MinValue, long.MaxValue),
                    Type = MimeTypes.ToFileType(mime, name),
                },
            };
        }

        public void Dispose() => Stream.Dispose();
    }
}

internal static class MimeTypes
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".heic"] = "image/heic",
        [".mp4"] = "video/mp4", [".mkv"] = "video/x-matroska", [".webm"] = "video/webm", [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".flac"] = "audio/flac", [".ogg"] = "audio/ogg", [".m4a"] = "audio/mp4",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".zip"] = "application/zip",
        [".apk"] = "application/vnd.android.package-archive",
    };

    public static string FromFileName(string name) =>
        Known.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");

    public static Share.FileMetadata.Types.Type ToFileType(string mime, string name) =>
        mime.StartsWith("image/") ? Share.FileMetadata.Types.Type.Image
        : mime.StartsWith("video/") ? Share.FileMetadata.Types.Type.Video
        : mime.StartsWith("audio/") ? Share.FileMetadata.Types.Type.Audio
        : name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ? Share.FileMetadata.Types.Type.App
        : Share.FileMetadata.Types.Type.Unknown;
}
