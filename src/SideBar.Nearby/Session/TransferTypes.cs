using System.Net;
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

    /// <summary>Receiving: the peer was not verified in a mode that needs it. Sending: <see cref="SessionOptions.RequireVerifiedPeer"/> was not met.</summary>
    public const string Unverified = "unverified";
}

public sealed record TransferResult(TransferStatus Status, IReadOnlyList<string> Files, string? Detail = null)
{
    /// <summary>Texts and links received in a completed transfer; their values are never logged or put into <see cref="Detail"/>.</summary>
    public IReadOnlyList<ReceivedText> ReceivedTexts { get; init; } = [];

    /// <summary>Sending: the receiver proved to be one of the user's own devices (our VerifyPairedKey returned Success for a key received before our Introduction).</summary>
    public bool PeerVerified { get; init; }
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

    /// <summary>A received file was committed under its final name (receive only).</summary>
    void FileSaved(string path) { }
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

    /// <summary>Receiving: an incoming transfer from a peer that is not <see cref="IncomingTransfer.Verified"/> is rejected without asking.</summary>
    public bool RejectUnverified { get; init; }

    /// <summary>
    /// Sending to a device that must be one of ours: if its PairedKey did not verify before our Introduction, nothing is sent
    /// (Cancel instead) and the result is Failed with <see cref="TransferDetails.Unverified"/>. Guards against a replayed advertisement.
    /// </summary>
    public bool RequireVerifiedPeer { get; init; }

    public int MaxFilesPerIntroduction { get; init; } = 100;

    /// <summary>Largest BYTES payload (negotiation messages are tiny).</summary>
    public int MaxBytesPayload { get; init; } = 64 * 1024;

    public int MaxConcurrentBytesPayloads { get; init; } = 8;

    /// <summary>Free bytes on the volume of the given directory; injectable for tests (default: <see cref="DriveInfo"/>).</summary>
    public Func<string, long>? FreeBytes { get; init; }

    /// <summary>
    /// Sending: opens the Wi-Fi LAN socket the receiver offers in a bandwidth upgrade (normally
    /// <see cref="SendSession.ConnectWifiLanAsync"/>). Null (the default) ignores such offers: set it only for a slow
    /// transport such as Bluetooth, a session already on Wi-Fi LAN has no use for it. Offers are acted on only after
    /// the paired-key exchange, and only from a peer whose key verified (whatever <see cref="RequireVerifiedPeer"/> says).
    /// </summary>
    public Func<IPEndPoint, CancellationToken, Task<Stream>>? ConnectWifiLanUpgradeAsync { get; init; }

    public IShareIdentity ResolvedIdentity => Identity ?? new AnonymousIdentity();
}

public sealed record IncomingFile(string Name, long Size, string MimeType);

public sealed record IncomingTransfer(string Pin, string? SenderName, IReadOnlyList<IncomingFile> Files)
{
    /// <summary>The sender's PairedKey was verified against one of our known certificates before the Introduction.</summary>
    public bool Verified { get; init; }

    /// <summary>Device name from the peer's certificate; set only when <see cref="Verified"/> (never the advertised name).</summary>
    public string? VerifiedName { get; init; }

    public IReadOnlyList<IncomingText> Texts { get; init; } = [];

    /// <summary>The Introduction also carries Wi-Fi credentials; they are ignored (never accepted).</summary>
    public bool HasWifiCredentials { get; init; }
}
