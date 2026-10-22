using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using SideBar.Nearby.Protocol.SecureGcm;
using SideBar.Nearby.Protocol.SecureMsg;

namespace SideBar.Nearby.Crypto;

public sealed class Ukey2Exception(string message) : Exception(message);

/// <summary>Output of a UKEY2 handshake: auth string (for the PIN) and next protocol secret (for D2D keys).</summary>
public sealed record Ukey2Result(byte[] AuthString, byte[] NextSecret);

internal static class Ukey2Common
{
    public const string NextProtocol = "AES_256_CBC-HMAC_SHA256";
    private static readonly byte[] AuthLabel = Encoding.ASCII.GetBytes("UKEY2 v1 auth");
    private static readonly byte[] NextLabel = Encoding.ASCII.GetBytes("UKEY2 v1 next");

    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Public key as GenericPublicKey with X/Y encoded like Java BigInteger.toByteArray (signed, big-endian).</summary>
    public static byte[] EncodePublicKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return new GenericPublicKey
        {
            Type = PublicKeyType.EcP256,
            EcP256PublicKey = new EcP256PublicKey
            {
                X = ByteString.CopyFrom(ToSignedBigEndian(q.X!)),
                Y = ByteString.CopyFrom(ToSignedBigEndian(q.Y!)),
            },
        }.ToByteArray();
    }

    public static ECDiffieHellmanPublicKey DecodePublicKey(ReadOnlySpan<byte> genericPublicKey)
    {
        GenericPublicKey key;
        try { key = GenericPublicKey.Parser.ParseFrom(genericPublicKey); }
        catch (InvalidProtocolBufferException e) { throw new Ukey2Exception("Bad public key message: " + e.Message); }
        if (key.Type != PublicKeyType.EcP256 || key.EcP256PublicKey is null)
            throw new Ukey2Exception("Public key is not EC P-256.");

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = ToFixed32(key.EcP256PublicKey.X.Span),
                Y = ToFixed32(key.EcP256PublicKey.Y.Span),
            },
        };
        try
        {
            using var ecdh = ECDiffieHellman.Create(parameters);
            return ecdh.PublicKey;
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
        {
            throw new Ukey2Exception("Invalid EC point: " + e.Message);
        }
    }

    public static Ukey2Result Derive(ECDiffieHellman privateKey, ECDiffieHellmanPublicKey peer, byte[] clientInit, byte[] serverInit)
    {
        var shared = SHA256.HashData(privateKey.DeriveRawSecretAgreement(peer));
        var info = new byte[clientInit.Length + serverInit.Length];
        clientInit.CopyTo(info, 0);
        serverInit.CopyTo(info, clientInit.Length);

        var auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, AuthLabel, info);
        var next = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, NextLabel, info);
        return new Ukey2Result(auth, next);
    }

    public static byte[] Wrap(Ukey2Message.Types.Type type, IMessage data) =>
        new Ukey2Message { MessageType = type, MessageData = data.ToByteString() }.ToByteArray();

    public static Ukey2Message ParseMessage(byte[] raw)
    {
        try { return Ukey2Message.Parser.ParseFrom(raw); }
        catch (InvalidProtocolBufferException e) { throw new Ukey2Exception("Bad UKEY2 message: " + e.Message); }
    }

    public static byte[] ToSignedBigEndian(byte[] unsigned)
    {
        var i = 0;
        while (i < unsigned.Length - 1 && unsigned[i] == 0) i++;
        var trimmed = unsigned.AsSpan(i);
        if ((trimmed[0] & 0x80) == 0) return trimmed.ToArray();
        var result = new byte[trimmed.Length + 1];
        trimmed.CopyTo(result.AsSpan(1));
        return result;
    }

    public static byte[] ToFixed32(ReadOnlySpan<byte> value)
    {
        while (value.Length > 32 && value[0] == 0) value = value[1..];
        if (value.Length > 32) throw new Ukey2Exception("EC coordinate longer than 32 bytes.");
        var result = new byte[32];
        value.CopyTo(result.AsSpan(32 - value.Length));
        return result;
    }
}

/// <summary>Initiator side of UKEY2 (P256_SHA512, next protocol AES_256_CBC-HMAC_SHA256). Used by the sender.</summary>
public sealed class Ukey2Client : IDisposable
{
    private readonly ECDiffieHellman _key = Ukey2Common.NewKey();
    private byte[]? _clientInit;
    private byte[]? _clientFinish;

    /// <summary>Returns the serialized ClientInit message (UKEY2 message wrapper included).</summary>
    public byte[] CreateClientInit()
    {
        var finish = Ukey2Common.Wrap(Ukey2Message.Types.Type.ClientFinish,
            new Ukey2ClientFinished { PublicKey = ByteString.CopyFrom(Ukey2Common.EncodePublicKey(_key)) });
        _clientFinish = finish;

        var init = new Ukey2ClientInit
        {
            Version = 1,
            Random = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            NextProtocol = Ukey2Common.NextProtocol,
        };
        init.CipherCommitments.Add(new Ukey2ClientInit.Types.CipherCommitment
        {
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            Commitment = ByteString.CopyFrom(SHA512.HashData(finish)),
        });
        return _clientInit = Ukey2Common.Wrap(Ukey2Message.Types.Type.ClientInit, init);
    }

    /// <summary>Validates ServerInit, returns the ClientFinish message to send and the handshake result.</summary>
    public (byte[] ClientFinish, Ukey2Result Result) ProcessServerInit(byte[] serverInitRaw)
    {
        if (_clientInit is null || _clientFinish is null) throw new InvalidOperationException("CreateClientInit first.");

        var message = Ukey2Common.ParseMessage(serverInitRaw);
        if (message.MessageType != Ukey2Message.Types.Type.ServerInit)
            throw new Ukey2Exception($"Expected ServerInit, got {message.MessageType}.");

        Ukey2ServerInit init;
        try { init = Ukey2ServerInit.Parser.ParseFrom(message.MessageData); }
        catch (InvalidProtocolBufferException e) { throw new Ukey2Exception("Bad ServerInit: " + e.Message); }

        if (init.Version != 1) throw new Ukey2Exception("ServerInit version != 1.");
        if (init.Random.Length != 32) throw new Ukey2Exception("ServerInit random must be 32 bytes.");
        if (init.HandshakeCipher != Ukey2HandshakeCipher.P256Sha512) throw new Ukey2Exception("Unsupported handshake cipher.");

        var peer = Ukey2Common.DecodePublicKey(init.PublicKey.Span);
        var result = Ukey2Common.Derive(_key, peer, _clientInit, serverInitRaw);
        return (_clientFinish, result);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Responder side of UKEY2. Used by the receiver.</summary>
public sealed class Ukey2Server : IDisposable
{
    private readonly ECDiffieHellman _key = Ukey2Common.NewKey();
    private byte[]? _clientInit;
    private byte[]? _serverInit;
    private byte[]? _commitment;

    /// <summary>Validates ClientInit and returns the serialized ServerInit message.</summary>
    public byte[] ProcessClientInit(byte[] clientInitRaw)
    {
        var message = Ukey2Common.ParseMessage(clientInitRaw);
        if (message.MessageType != Ukey2Message.Types.Type.ClientInit)
            throw new Ukey2Exception($"Expected ClientInit, got {message.MessageType}.");

        Ukey2ClientInit init;
        try { init = Ukey2ClientInit.Parser.ParseFrom(message.MessageData); }
        catch (InvalidProtocolBufferException e) { throw new Ukey2Exception("Bad ClientInit: " + e.Message); }

        if (init.Version != 1) throw new Ukey2Exception("ClientInit version != 1.");
        if (init.Random.Length != 32) throw new Ukey2Exception("ClientInit random must be 32 bytes.");
        if (init.NextProtocol != Ukey2Common.NextProtocol) throw new Ukey2Exception("Unsupported next protocol: " + init.NextProtocol);

        var commitment = init.CipherCommitments.FirstOrDefault(c => c.HandshakeCipher == Ukey2HandshakeCipher.P256Sha512)
            ?? throw new Ukey2Exception("No P256_SHA512 cipher commitment.");

        _commitment = commitment.Commitment.ToByteArray();
        _clientInit = clientInitRaw;
        return _serverInit = Ukey2Common.Wrap(Ukey2Message.Types.Type.ServerInit, new Ukey2ServerInit
        {
            Version = 1,
            Random = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            HandshakeCipher = Ukey2HandshakeCipher.P256Sha512,
            PublicKey = ByteString.CopyFrom(Ukey2Common.EncodePublicKey(_key)),
        });
    }

    public Ukey2Result ProcessClientFinish(byte[] clientFinishRaw)
    {
        if (_clientInit is null || _serverInit is null || _commitment is null)
            throw new InvalidOperationException("ProcessClientInit first.");

        var message = Ukey2Common.ParseMessage(clientFinishRaw);
        if (message.MessageType != Ukey2Message.Types.Type.ClientFinish)
            throw new Ukey2Exception($"Expected ClientFinish, got {message.MessageType}.");

        if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(clientFinishRaw), _commitment))
            throw new Ukey2Exception("ClientFinish does not match the cipher commitment.");

        Ukey2ClientFinished finish;
        try { finish = Ukey2ClientFinished.Parser.ParseFrom(message.MessageData); }
        catch (InvalidProtocolBufferException e) { throw new Ukey2Exception("Bad ClientFinish: " + e.Message); }

        var peer = Ukey2Common.DecodePublicKey(finish.PublicKey.Span);
        return Ukey2Common.Derive(_key, peer, _clientInit, _serverInit);
    }

    public void Dispose() => _key.Dispose();
}
