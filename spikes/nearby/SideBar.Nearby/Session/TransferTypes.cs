using SideBar.Nearby.Wire;

namespace SideBar.Nearby.Session;

public enum TransferStatus
{
    Completed,
    RejectedByPeer,
    CancelledByPeer,
    Failed,
}

public sealed record TransferResult(TransferStatus Status, IReadOnlyList<string> Files, string? Detail = null);

public sealed record TransferProgress(string FileName, long FileBytes, long FileTotal, long TotalBytes, long TotalSize);

public interface ITransferObserver
{
    /// <summary>The 4-digit PIN both devices show; available as soon as UKEY2 finishes.</summary>
    void PinReady(string pin) { }

    void Status(string message) { }

    void Progress(TransferProgress progress) { }
}

public sealed record SessionOptions(
    string DeviceName,
    DeviceKind DeviceKind = DeviceKind.Laptop,
    IShareIdentity? Identity = null,
    Action<string>? Trace = null)
{
    /// <summary>How long the sender waits for the receiver to finish and close before it disconnects itself.</summary>
    public TimeSpan FinishTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public IShareIdentity ResolvedIdentity => Identity ?? new AnonymousIdentity();
}

public sealed record IncomingFile(string Name, long Size, string MimeType);

public sealed record IncomingTransfer(string Pin, string? SenderName, IReadOnlyList<IncomingFile> Files);
