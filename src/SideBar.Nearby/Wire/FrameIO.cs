using System.Buffers.Binary;

namespace SideBar.Nearby.Wire;

/// <summary>TCP framing: every protobuf message is prefixed with a 4-byte big-endian length.</summary>
public static class FrameIO
{
    public const int MaxFrameLength = 5 * 1024 * 1024;

    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, payload.Length);
        payload.CopyTo(buf.AsSpan(4));
        return buf;
    }

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        // Single write so a header and its body are never split by another writer.
        await stream.WriteAsync(Frame(payload.Span), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrameLength)
            throw new InvalidDataException($"Frame length {length} is out of range.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }
}
