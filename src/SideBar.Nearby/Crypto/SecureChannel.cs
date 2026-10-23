using System.Security.Cryptography;
using Google.Protobuf;
using SideBar.Nearby.Protocol.SecureGcm;
using SideBar.Nearby.Protocol.SecureMsg;

namespace SideBar.Nearby.Crypto;

public sealed class SecureChannelException(string message) : Exception(message);

/// <summary>
/// D2D keys derived from the UKEY2 next secret plus SecureMessage (AES-256-CBC + HMAC-SHA256) wrapping of
/// DeviceToDeviceMessage. Each direction has an independent sequence number starting at 1.
/// </summary>
public sealed class SecureChannel
{
    private static readonly byte[] D2dSalt = Convert.FromHexString("82AA55A0D397F88346CA1CEE8D3909B95F13FA7DEB1D4AB38376B8256DA85510");
    private static readonly byte[] SecureMessageSalt = Convert.FromHexString("BF9D2A53C63616D75DB0A7165B91C1EF73E537F2427405FA23610A4BE657642E");
    private static readonly byte[] Info = "client"u8.ToArray();
    private static readonly byte[] InfoServer = "server"u8.ToArray();
    private static readonly byte[] EncInfo = "ENC:2"u8.ToArray();
    private static readonly byte[] SigInfo = "SIG:1"u8.ToArray();

    private readonly byte[] _encKey, _sendHmacKey, _decKey, _recvHmacKey;
    private int _sendSeq, _recvSeq;
    private readonly byte[] _publicMetadata = new GcmMetadata
    {
        Type = Protocol.SecureGcm.Type.DeviceToDeviceMessage,
        Version = 1,
    }.ToByteArray();

    private SecureChannel(byte[] encKey, byte[] sendHmacKey, byte[] decKey, byte[] recvHmacKey)
    {
        _encKey = encKey;
        _sendHmacKey = sendHmacKey;
        _decKey = decKey;
        _recvHmacKey = recvHmacKey;
    }

    /// <summary>Channel for the UKEY2 initiator (sender): encrypts with the client keys, decrypts with the server keys.</summary>
    public static SecureChannel ForClient(byte[] nextSecret) => Create(nextSecret, isClient: true);

    /// <summary>Channel for the UKEY2 responder (receiver).</summary>
    public static SecureChannel ForServer(byte[] nextSecret) => Create(nextSecret, isClient: false);

    private static SecureChannel Create(byte[] nextSecret, bool isClient)
    {
        var d2dClient = HKDF.DeriveKey(HashAlgorithmName.SHA256, nextSecret, 32, D2dSalt, Info);
        var d2dServer = HKDF.DeriveKey(HashAlgorithmName.SHA256, nextSecret, 32, D2dSalt, InfoServer);

        byte[] Derive(byte[] d2d, byte[] info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, d2d, 32, SecureMessageSalt, info);

        var clientEnc = Derive(d2dClient, EncInfo);
        var clientSig = Derive(d2dClient, SigInfo);
        var serverEnc = Derive(d2dServer, EncInfo);
        var serverSig = Derive(d2dServer, SigInfo);

        return isClient
            ? new SecureChannel(clientEnc, clientSig, serverEnc, serverSig)
            : new SecureChannel(serverEnc, serverSig, clientEnc, clientSig);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> payload) => Encrypt(payload, RandomNumberGenerator.GetBytes(16));

    internal byte[] Encrypt(ReadOnlySpan<byte> payload, byte[] iv)
    {
        var d2d = new DeviceToDeviceMessage
        {
            SequenceNumber = ++_sendSeq,
            Message = ByteString.CopyFrom(payload),
        }.ToByteArray();

        using var aes = Aes.Create();
        aes.Key = _encKey;
        var body = aes.EncryptCbc(d2d, iv, PaddingMode.PKCS7);

        var headerAndBody = new HeaderAndBody
        {
            Header = new Header
            {
                EncryptionScheme = EncScheme.Aes256Cbc,
                SignatureScheme = SigScheme.HmacSha256,
                Iv = ByteString.CopyFrom(iv),
                PublicMetadata = ByteString.CopyFrom(_publicMetadata),
            },
            Body = ByteString.CopyFrom(body),
        }.ToByteArray();

        return new SecureMessage
        {
            HeaderAndBody = ByteString.CopyFrom(headerAndBody),
            Signature = ByteString.CopyFrom(HMACSHA256.HashData(_sendHmacKey, headerAndBody)),
        }.ToByteArray();
    }

    /// <summary>Verifies the HMAC, decrypts, checks the sequence number and returns the inner payload.</summary>
    public byte[] Decrypt(byte[] secureMessage)
    {
        SecureMessage message;
        HeaderAndBody headerAndBody;
        DeviceToDeviceMessage d2d;
        try
        {
            message = SecureMessage.Parser.ParseFrom(secureMessage);
            if (!CryptographicOperations.FixedTimeEquals(
                    HMACSHA256.HashData(_recvHmacKey, message.HeaderAndBody.Span), message.Signature.Span))
                throw new SecureChannelException("HMAC mismatch.");

            headerAndBody = HeaderAndBody.Parser.ParseFrom(message.HeaderAndBody);
            if (headerAndBody.Header.EncryptionScheme != EncScheme.Aes256Cbc ||
                headerAndBody.Header.SignatureScheme != SigScheme.HmacSha256)
                throw new SecureChannelException("Unsupported encryption or signature scheme.");

            using var aes = Aes.Create();
            aes.Key = _decKey;
            var plain = aes.DecryptCbc(headerAndBody.Body.Span, headerAndBody.Header.Iv.Span, PaddingMode.PKCS7);
            d2d = DeviceToDeviceMessage.Parser.ParseFrom(plain);
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or CryptographicException or ArgumentException)
        {
            throw new SecureChannelException("Malformed secure message: " + e.Message);
        }

        if (d2d.SequenceNumber != _recvSeq + 1)
            throw new SecureChannelException($"Unexpected sequence number {d2d.SequenceNumber}, expected {_recvSeq + 1}.");
        _recvSeq++;
        return d2d.Message.ToByteArray();
    }
}
