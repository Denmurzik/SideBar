using Google.Protobuf;
using Conn = SideBar.Nearby.Protocol.Connections;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Session;

/// <summary>Builders for Nearby Connections offline frames (transport layer).</summary>
internal static class OfflineFrames
{
    public const int PayloadChunkSize = 512 * 1024;

    private static Conn.OfflineFrame Wrap(Conn.V1Frame.Types.FrameType type, Action<Conn.V1Frame> fill)
    {
        var v1 = new Conn.V1Frame { Type = type };
        fill(v1);
        return new Conn.OfflineFrame { Version = Conn.OfflineFrame.Types.Version.V1, V1 = v1 };
    }

    public static Conn.OfflineFrame ConnectionRequest(string endpointId, string endpointName, byte[] endpointInfo) =>
        Wrap(Conn.V1Frame.Types.FrameType.ConnectionRequest, v1 =>
        {
            v1.ConnectionRequest = new Conn.ConnectionRequestFrame
            {
                EndpointId = endpointId,
                EndpointName = ByteString.CopyFromUtf8(endpointName),
                EndpointInfo = ByteString.CopyFrom(endpointInfo),
            };
            v1.ConnectionRequest.Mediums.Add(Conn.ConnectionRequestFrame.Types.Medium.WifiLan);
        });

    public static Conn.OfflineFrame ConnectionResponse(bool accept) =>
        Wrap(Conn.V1Frame.Types.FrameType.ConnectionResponse, v1 =>
            v1.ConnectionResponse = new Conn.ConnectionResponseFrame
            {
                Response = accept
                    ? Conn.ConnectionResponseFrame.Types.ResponseStatus.Accept
                    : Conn.ConnectionResponseFrame.Types.ResponseStatus.Reject,
                OsInfo = new Conn.OsInfo { Type = Conn.OsInfo.Types.OsType.Windows },
            });

    public static Conn.OfflineFrame KeepAlive(bool ack) =>
        Wrap(Conn.V1Frame.Types.FrameType.KeepAlive, v1 => v1.KeepAlive = new Conn.KeepAliveFrame { Ack = ack });

    public static Conn.OfflineFrame Disconnection() =>
        Wrap(Conn.V1Frame.Types.FrameType.Disconnection, v1 => v1.Disconnection = new Conn.DisconnectionFrame());

    public static Conn.OfflineFrame PayloadChunk(
        Conn.PayloadTransferFrame.Types.PayloadHeader header, long offset, ReadOnlySpan<byte> body, bool last)
    {
        var chunk = new Conn.PayloadTransferFrame.Types.PayloadChunk
        {
            Offset = offset,
            Flags = last ? (int)Conn.PayloadTransferFrame.Types.PayloadChunk.Types.Flags.LastChunk : 0,
            Body = ByteString.CopyFrom(body),
        };
        return Wrap(Conn.V1Frame.Types.FrameType.PayloadTransfer, v1 =>
            v1.PayloadTransfer = new Conn.PayloadTransferFrame
            {
                PacketType = Conn.PayloadTransferFrame.Types.PacketType.Data,
                PayloadHeader = header,
                PayloadChunk = chunk,
            });
    }

    public static Conn.PayloadTransferFrame.Types.PayloadHeader BytesHeader(long id, int totalSize) => new()
    {
        Id = id,
        Type = Conn.PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.Bytes,
        TotalSize = totalSize,
        IsSensitive = false,
    };

    public static Conn.PayloadTransferFrame.Types.PayloadHeader FileHeader(long id, long totalSize, string fileName) => new()
    {
        Id = id,
        Type = Conn.PayloadTransferFrame.Types.PayloadHeader.Types.PayloadType.File,
        TotalSize = totalSize,
        IsSensitive = false,
        FileName = fileName,
    };
}

/// <summary>Builders for Quick Share (application layer) frames carried inside BYTES payloads.</summary>
internal static class SharingFrames
{
    private static Share.Frame Wrap(Share.V1Frame.Types.FrameType type, Action<Share.V1Frame> fill)
    {
        var v1 = new Share.V1Frame { Type = type };
        fill(v1);
        return new Share.Frame { Version = Share.Frame.Types.Version.V1, V1 = v1 };
    }

    public static Share.Frame PairedKeyEncryption(Share.PairedKeyEncryptionFrame frame) =>
        Wrap(Share.V1Frame.Types.FrameType.PairedKeyEncryption, v1 => v1.PairedKeyEncryption = frame);

    public static Share.Frame PairedKeyResult(Share.PairedKeyResultFrame.Types.Status status) =>
        Wrap(Share.V1Frame.Types.FrameType.PairedKeyResult,
            v1 => v1.PairedKeyResult = new Share.PairedKeyResultFrame { Status = status });

    public static Share.Frame Introduction(IEnumerable<Share.FileMetadata> files, IEnumerable<Share.TextMetadata>? texts = null) =>
        Wrap(Share.V1Frame.Types.FrameType.Introduction, v1 =>
        {
            v1.Introduction = new Share.IntroductionFrame();
            v1.Introduction.FileMetadata.AddRange(files);
            if (texts is not null) v1.Introduction.TextMetadata.AddRange(texts);
        });

    public static Share.Frame Response(Share.ConnectionResponseFrame.Types.Status status) =>
        Wrap(Share.V1Frame.Types.FrameType.Response,
            v1 => v1.ConnectionResponse = new Share.ConnectionResponseFrame { Status = status });

    public static Share.Frame Cancel() => Wrap(Share.V1Frame.Types.FrameType.Cancel, _ => { });
}
