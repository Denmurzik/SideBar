using System.Net.Sockets;

namespace SideBar.Core.Nearby;

/// <summary>Tells a stopped listener (the accept loop must end) from a failed accept of one connection (the loop must go on).</summary>
public static class AcceptFailure
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(5);

    /// <summary>Delay before the next accept after <paramref name="failures"/> failures in a row (1 = first): 100 ms doubling to a 2 s cap.</summary>
    public static TimeSpan Backoff(int failures)
    {
        if (failures <= 1) return RetryDelay;
        double ms = RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 10));
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxRetryDelay.TotalMilliseconds));
    }

    public static bool ListenerStopped(Exception e) => e switch
    {
        ObjectDisposedException or OperationCanceledException => true,
        InvalidOperationException => true,   // listener not started
        SocketException s => s.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted
            or SocketError.NotSocket or SocketError.Shutdown,
        _ => false,
    };
}
