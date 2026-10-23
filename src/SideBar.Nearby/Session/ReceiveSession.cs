using SideBar.Nearby.Crypto;
using SideBar.Nearby.Wire;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Session;

/// <summary>Server (receiving) side of a Quick Share transfer over an accepted stream.</summary>
public static class ReceiveSession
{
    public static async Task<TransferResult> ReceiveAsync(
        Stream stream,
        SessionOptions options,
        string outputDirectory,
        Func<IncomingTransfer, CancellationToken, Task<bool>> consent,
        ITransferObserver? observer = null,
        CancellationToken ct = default)
    {
        observer ??= new NullObserver();
        var identity = options.ResolvedIdentity;
        Directory.CreateDirectory(outputDirectory);
        var incoming = new Dictionary<long, IncomingTarget>();

        await using var connection = new NearbyConnection(stream, "receive");
        try
        {
            // 1. ConnectionRequest and UKEY2 ClientInit (plaintext).
            var request = await connection.ReadPlainOfflineAsync(ct);
            if (request.V1?.Type != Conn.V1Frame.Types.FrameType.ConnectionRequest)
                throw new InvalidDataException("Expected ConnectionRequest.");
            var senderName = DescribeSender(request.V1.ConnectionRequest);
            var remoteInfo = request.V1.ConnectionRequest.EndpointInfo.IsEmpty ? null
                : EndpointInfo.TryDecode(request.V1.ConnectionRequest.EndpointInfo.Span, out var decoded) ? decoded : null;

            using var ukey = new Ukey2Server();
            var serverInit = ukey.ProcessClientInit(await connection.ReadPlainAsync(ct));
            await connection.WritePlainAsync(serverInit, ct);

            // 2. ClientFinish -> keys; client ConnectionResponse, then ours.
            var handshake = ukey.ProcessClientFinish(await connection.ReadPlainAsync(ct));
            var pin = Pin.FromAuthString(handshake.AuthString);
            observer.PinReady(pin);

            var clientResponse = await connection.ReadPlainOfflineAsync(ct);
            if (clientResponse.V1?.Type != Conn.V1Frame.Types.FrameType.ConnectionResponse)
                throw new InvalidDataException("Expected ConnectionResponse.");
            await connection.WritePlainAsync(OfflineFrames.ConnectionResponse(accept: true), ct);

            connection.EnableEncryption(SecureChannel.ForServer(handshake.NextSecret));
            await connection.SendSharingAsync(SharingFrames.PairedKeyEncryption(
                identity.CreatePairedKeyEncryption(handshake.AuthString, PairedKeyRole.Receiver, remoteInfo)), ct);

            // 3. Paired key, Introduction, consent, file chunks.
            var accepted = false;
            var saved = new List<string>();
            long totalSize = 0, receivedTotal = 0;

            async Task<bool> OnFileChunk(Conn.PayloadTransferFrame transfer)
            {
                if (!accepted || !incoming.TryGetValue(transfer.PayloadHeader.Id, out var target))
                    throw new InvalidDataException("FILE payload that was not announced or accepted.");
                var chunk = transfer.PayloadChunk;
                if (chunk.Offset != target.Written)
                    throw new InvalidDataException($"Unexpected file chunk offset {chunk.Offset}, expected {target.Written}.");
                await target.Stream.WriteAsync(chunk.Body.Memory, ct);
                target.Written += chunk.Body.Length;
                receivedTotal += chunk.Body.Length;
                observer.Progress(new TransferProgress(target.Name, target.Written, target.Size, receivedTotal, totalSize));

                if ((chunk.Flags & (int)Conn.PayloadTransferFrame.Types.PayloadChunk.Types.Flags.LastChunk) != 0)
                {
                    await target.Stream.DisposeAsync();
                    if (target.Written != target.Size)
                        throw new InvalidDataException($"File {target.Name} ended at {target.Written} of {target.Size} bytes.");
                    target.Done = true;
                    saved.Add(target.Path);
                }
                return incoming.Values.All(t => t.Done);
            }

            try
            {
                while (true)
                {
                    var frame = await connection.ReadSharingAsync(OnFileChunk, ct);
                    if (frame is null)
                    {
                        try { await connection.SendOfflineAsync(OfflineFrames.Disconnection(), ct); }
                        catch (IOException) { }
                        await WaitForPeerCloseAsync(connection, ct);
                        return new TransferResult(TransferStatus.Completed, saved);
                    }
                    var v1 = frame.V1;
                    switch (v1?.Type)
                    {
                        case Share.V1Frame.Types.FrameType.PairedKeyEncryption:
                            var status = identity.VerifyPairedKey(v1.PairedKeyEncryption, handshake.AuthString, PairedKeyRole.Receiver, remoteInfo);
                            await connection.SendSharingAsync(SharingFrames.PairedKeyResult(status), ct);
                            break;

                        case Share.V1Frame.Types.FrameType.Cancel:
                            return new TransferResult(TransferStatus.CancelledByPeer, saved);

                        case Share.V1Frame.Types.FrameType.Introduction:
                            var files = v1.Introduction.FileMetadata;
                            if (files.Count == 0)
                            {
                                await connection.SendSharingAsync(SharingFrames.Response(
                                    Share.ConnectionResponseFrame.Types.Status.UnsupportedAttachmentType), ct);
                                return new TransferResult(TransferStatus.Failed, saved, "Introduction has no files.");
                            }

                            var offer = new IncomingTransfer(pin, senderName,
                                files.Select(f => new IncomingFile(f.Name, f.Size, f.MimeType)).ToList());
                            observer.Status("Waiting for the user's decision...");
                            if (!await consent(offer, ct))
                            {
                                await connection.SendSharingAsync(SharingFrames.Response(
                                    Share.ConnectionResponseFrame.Types.Status.Reject), ct);
                                return new TransferResult(TransferStatus.RejectedByPeer, saved, "Rejected locally.");
                            }

                            foreach (var f in files)
                            {
                                var target = IncomingTarget.Create(outputDirectory, f);
                                incoming[f.PayloadId] = target;
                                totalSize += f.Size;
                            }
                            accepted = true;
                            await connection.SendSharingAsync(SharingFrames.Response(
                                Share.ConnectionResponseFrame.Types.Status.Accept), ct);
                            break;
                    }
                }
            }
            finally
            {
                foreach (var target in incoming.Values) await target.DisposeAsync();
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
    }

    /// <summary>The sender usually follows the last chunk with a Disconnection frame; give it a moment, then finish anyway.</summary>
    private static async Task WaitForPeerCloseAsync(NearbyConnection connection, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await connection.ReadSharingAsync(null, timeout.Token); }
        catch (Exception e) when (e is PeerDisconnectedException or IOException or OperationCanceledException or InvalidDataException) { }
    }

    private static string? DescribeSender(Conn.ConnectionRequestFrame request)
    {
        if (!request.EndpointInfo.IsEmpty
            && EndpointInfo.TryDecode(request.EndpointInfo.Span, out var info) && info?.Name is { Length: > 0 } name)
            return name;
        return request.EndpointName.IsEmpty ? null : request.EndpointName.ToStringUtf8();
    }

    private sealed class NullObserver : ITransferObserver;

    private sealed class IncomingTarget : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required long Size { get; init; }
        public required FileStream Stream { get; init; }
        public long Written { get; set; }
        public bool Done { get; set; }

        public static IncomingTarget Create(string directory, Share.FileMetadata metadata)
        {
            var path = UniquePath(directory, SanitizeFileName(metadata.Name));
            return new IncomingTarget
            {
                Name = metadata.Name,
                Path = path,
                Size = metadata.Size,
                Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true),
            };
        }

        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }

    /// <summary>Strips any directory part and characters Windows rejects; the sender is untrusted.</summary>
    internal static string SanitizeFileName(string name)
    {
        var leaf = name.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(leaf.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    internal static string UniquePath(string directory, string fileName)
    {
        var path = System.IO.Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            path = System.IO.Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }
}
