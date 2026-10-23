using System.Net.Sockets;

namespace SideBar.Core.Nearby;

/// <summary>Tells a stopped listener (the accept loop must end) from a failed accept of one connection (the loop must go on).</summary>
public static class AcceptFailure
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static bool ListenerStopped(Exception e) => e switch
    {
        ObjectDisposedException or OperationCanceledException => true,
        InvalidOperationException => true,   // listener not started
        SocketException s => s.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted
            or SocketError.NotSocket or SocketError.Shutdown,
        _ => false,
    };
}
