using System.Net.Sockets;
using SideBar.Nearby.Crypto;

namespace SideBar.Nearby.Session;

/// <summary>The peer was silent for longer than the allowed time.</summary>
public sealed class SessionTimeoutException(string message) : Exception(message);

/// <summary>The peer cancelled the transfer (Cancel frame or PAYLOAD_CANCELED).</summary>
public sealed class PeerCancelledException(string message) : Exception(message);

/// <summary>A local I/O failure whose message is safe to show: it never contains file names or paths.</summary>
public sealed class SafeIoException(string message) : IOException(message);

internal static class SessionErrors
{
    /// <summary>Maps an exception to a result. Details never carry file names or paths, so I/O messages are replaced.</summary>
    public static TransferResult ToResult(Exception e, IReadOnlyList<string>? files = null) => e switch
    {
        PeerDisconnectedException or PeerCancelledException => new(TransferStatus.CancelledByPeer, files ?? [], e.Message),
        SessionTimeoutException => new(TransferStatus.TimedOut, files ?? [], e.Message),
        InvalidDataException or Ukey2Exception or SecureChannelException => new(TransferStatus.ProtocolError, files ?? [], e.Message),
        _ => new(TransferStatus.Failed, files ?? [], IsTransportError(e) ? e.Message : "I/O error."),
    };

    public static bool IsHandled(Exception e) =>
        e is IOException or UnauthorizedAccessException or InvalidDataException or Ukey2Exception
            or SecureChannelException or PeerCancelledException or SessionTimeoutException;

    private static bool IsTransportError(Exception e) =>
        e is EndOfStreamException or ObjectDisposedException or SafeIoException || e.InnerException is SocketException;
}
