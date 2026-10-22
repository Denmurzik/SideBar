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
        var entries = files.Select(OutgoingFile.Create).ToList();

        await using var connection = new NearbyConnection(stream);
        try
        {
            // 1. ConnectionRequest + UKEY2 ClientInit (plaintext).
            var info = identity.CreateEndpointInfo(options.DeviceName, options.DeviceKind);
            await connection.WritePlainAsync(
                OfflineFrames.ConnectionRequest(MdnsName.NewEndpointId(), options.DeviceName, info.Encode()), ct);

            using var ukey = new Ukey2Client();
            await connection.WritePlainAsync(ukey.CreateClientInit(), ct);

            // 2. ServerInit -> ClientFinish + our ConnectionResponse.
            var serverInit = await connection.ReadPlainAsync(ct);
            var (clientFinish, handshake) = ukey.ProcessServerInit(serverInit);
            await connection.WritePlainAsync(clientFinish, ct);
            await connection.WritePlainAsync(OfflineFrames.ConnectionResponse(accept: true), ct);

            var pin = Pin.FromAuthString(handshake.AuthString);
            observer.PinReady(pin);

            // 3. Peer ConnectionResponse (plaintext), then everything is encrypted.
            var response = await connection.ReadPlainOfflineAsync(ct);
            if (response.V1?.ConnectionResponse?.Response != Conn.ConnectionResponseFrame.Types.ResponseStatus.Accept)
                return new TransferResult(TransferStatus.RejectedByPeer, [], "Peer rejected the connection.");
            connection.EnableEncryption(SecureChannel.ForClient(handshake.NextSecret));

            // 4. Paired key exchange, then Introduction, then wait for the user's decision.
            await connection.SendSharingAsync(SharingFrames.PairedKeyEncryption(
                identity.CreatePairedKeyEncryption(handshake.AuthString, PairedKeyRole.Sender)), ct);

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
                        gotKeyEncryption = true;
                        var status = identity.VerifyPairedKey(v1.PairedKeyEncryption, handshake.AuthString, PairedKeyRole.Sender);
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
                        return await HandleResponseAsync(connection, v1.ConnectionResponse.Status, entries, observer, ct);
                }

                if (!introduced && gotKeyEncryption && gotKeyResult)
                {
                    introduced = true;
                    await connection.SendSharingAsync(SharingFrames.Introduction(entries.Select(e => e.Metadata)), ct);
                    observer.Status("Introduction sent, waiting for the receiver to accept...");
                }
            }
        }
        catch (PeerDisconnectedException e)
        {
            return new TransferResult(TransferStatus.CancelledByPeer, [], e.Message);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Ukey2Exception or SecureChannelException)
        {
            return new TransferResult(TransferStatus.Failed, [], e.Message);
        }
        finally
        {
            foreach (var entry in entries) entry.Dispose();
        }
    }

    private static async Task<TransferResult> HandleResponseAsync(
        NearbyConnection connection,
        Share.ConnectionResponseFrame.Types.Status status,
        List<OutgoingFile> entries,
        ITransferObserver observer,
        CancellationToken ct)
    {
        if (status != Share.ConnectionResponseFrame.Types.Status.Accept)
            return new TransferResult(TransferStatus.RejectedByPeer, [], $"Receiver answered {status}.");

        observer.Status("Accepted, sending...");
        var total = entries.Sum(e => e.Size);
        long sentTotal = 0;
        foreach (var entry in entries)
        {
            var header = OfflineFrames.FileHeader(entry.Metadata.PayloadId, entry.Size, entry.Metadata.Name);
            var buffer = new byte[OfflineFrames.PayloadChunkSize];
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
                throw new IOException($"File {entry.Metadata.Name} changed size while sending.");
            await connection.SendOfflineAsync(OfflineFrames.PayloadChunk(header, sent, [], last: true), ct);
        }

        await connection.SendOfflineAsync(OfflineFrames.Disconnection(), ct);
        return new TransferResult(TransferStatus.Completed, entries.Select(e => e.Metadata.Name).ToList());
    }

    private sealed class NullObserver : ITransferObserver;

    private sealed class OutgoingFile : IDisposable
    {
        public required Share.FileMetadata Metadata { get; init; }
        public required FileStream Stream { get; init; }
        public long Size => Metadata.Size;

        public static OutgoingFile Create(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
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
                    PayloadId = Random.Shared.NextInt64(long.MinValue, long.MaxValue),
                    Id = Random.Shared.NextInt64(long.MinValue, long.MaxValue),
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
