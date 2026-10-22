using System.Security.Cryptography;
using Google.Protobuf;
using SideBar.Nearby.Protocol.Sharing;
using SideBar.Nearby.Wire;

namespace SideBar.Nearby.Session;

/// <summary>
/// Extension point for "own devices" mode: everything that depends on Google certificates goes through here.
/// The anonymous implementation (EVERYONE mode) uses random data, which Android accepts.
/// </summary>
public interface IShareIdentity
{
    /// <summary>Endpoint info for the mDNS TXT record and for ConnectionRequestFrame.endpoint_info.</summary>
    EndpointInfo CreateEndpointInfo(string deviceName, DeviceKind kind);

    /// <summary>Our PairedKeyEncryption frame; may sign the UKEY2 auth string with a certificate key.</summary>
    PairedKeyEncryptionFrame CreatePairedKeyEncryption(ReadOnlySpan<byte> authString);

    /// <summary>Verifies the peer's PairedKeyEncryption frame and returns the status to send back.</summary>
    PairedKeyResultFrame.Types.Status VerifyPairedKey(PairedKeyEncryptionFrame remote, ReadOnlySpan<byte> authString);
}

public sealed class AnonymousIdentity : IShareIdentity
{
    public EndpointInfo CreateEndpointInfo(string deviceName, DeviceKind kind) =>
        EndpointInfo.CreateAnonymous(deviceName, kind);

    public PairedKeyEncryptionFrame CreatePairedKeyEncryption(ReadOnlySpan<byte> authString) => new()
    {
        SecretIdHash = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(6)),
        SignedData = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(72)),
    };

    public PairedKeyResultFrame.Types.Status VerifyPairedKey(PairedKeyEncryptionFrame remote, ReadOnlySpan<byte> authString) =>
        PairedKeyResultFrame.Types.Status.Unable;
}
