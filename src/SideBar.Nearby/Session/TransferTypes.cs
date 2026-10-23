using SideBar.Nearby.Wire;

namespace SideBar.Nearby.Session;

public enum TransferStatus
{
    Completed,
    RejectedByPeer,
    CancelledByPeer,
    /// <summary>Handshake, peer silence or the user's decision took longer than allowed.</summary>
    TimedOut,
    /// <summary>The peer sent something that violates the protocol or our limits.</summary>
    ProtocolError,
    /// <summary>Transport or local I/O failure.</summary>
    Failed,
}

/// <summary>Values of <see cref="TransferResult.Detail"/> that callers branch on.</summary>
public static class TransferDetails
{
    public const string NotEnoughSpace = "not-enough-space";
    public const string WifiUnsupported = "wifi-unsupported";
}

public sealed record TransferResult(TransferStatus Status, IReadOnlyList<string> Files, string? Detail = null)
{
    /// <summary>Texts and links received in a completed transfer; their values are never logged or put into <see cref="Detail"/>.</summary>
    public IReadOnlyList<ReceivedText> ReceivedTexts { get; init; } = [];
}

public enum TextKind { Text, Url, Address, Phone }

public sealed record ReceivedText(TextKind Kind, string Value);

/// <summary>A text announced in the Introduction; the content (and its preview title) is deliberately not exposed here.</summary>
public sealed record IncomingText(TextKind Kind, long Size);

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
    IShareIdentity? Identity = null)
{
    /// <summary>Endpoint info of the peer (from mDNS when sending), used to find its public certificate.</summary>
    public EndpointInfo? RemoteEndpointInfo { get; init; }

    /// <summary>How long the sender waits for the receiver to finish and close before it disconnects itself.</summary>
    public TimeSpan FinishTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Whole plaintext handshake (up to the encrypted channel); an accepted socket that stays silent is dropped.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>No frame of any kind (keep-alives count) received from the peer for this long ends the session.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the receiver waits for the user to accept; on expiry the transfer is rejected.</summary>
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxFilesPerIntroduction { get; init; } = 100;

    /// <summary>Largest BYTES payload (negotiation messages are tiny).</summary>
    public int MaxBytesPayload { get; init; } = 64 * 1024;

    public int MaxConcurrentBytesPayloads { get; init; } = 8;

    /// <summary>Free bytes on the volume of the given directory; injectable for tests (default: <see cref="DriveInfo"/>).</summary>
    public Func<string, long>? FreeBytes { get; init; }

    public IShareIdentity ResolvedIdentity => Identity ?? new AnonymousIdentity();
}

public sealed record IncomingFile(string Name, long Size, string MimeType);

public sealed record IncomingTransfer(string Pin, string? SenderName, IReadOnlyList<IncomingFile> Files)
{
    /// <summary>Whether the sender's identity was verified through a paired certificate; always false until M4.</summary>
    public bool Verified { get; init; }

    public IReadOnlyList<IncomingText> Texts { get; init; } = [];

    /// <summary>The Introduction also carries Wi-Fi credentials; they are ignored (never accepted).</summary>
    public bool HasWifiCredentials { get; init; }
}
