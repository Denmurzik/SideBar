using System.Security.Cryptography;
using Google.Protobuf;
using SideBar.Nearby.Crypto;
using Xunit;

namespace SideBar.Nearby.Tests;

public class PinTests
{
    // Reference values computed independently with the Chromium algorithm (signed bytes, base 31, modulo 9973).
    [Fact]
    public void KnownVectors()
    {
        Assert.Equal("2946", Pin.FromAuthString([1, 2, 3]));
        Assert.Equal("0865", Pin.FromAuthString([0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Equal("5095", Pin.FromAuthString(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
        Assert.Equal("3809", Pin.FromAuthString([0x80, 0x7F]));
        Assert.Equal("4065", Pin.FromAuthString(Enumerable.Repeat((byte)0xFF, 10).ToArray()));
    }

    [Fact]
    public void EmptyInputGivesZeros() => Assert.Equal("0000", Pin.FromAuthString([]));

    [Fact]
    public void AlwaysFourDigits()
    {
        for (var i = 0; i < 200; i++)
        {
            var pin = Pin.FromAuthString(RandomNumberGenerator.GetBytes(32));
            Assert.Equal(4, pin.Length);
            Assert.All(pin, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }
}

public class HkdfTests
{
    // RFC 5869, test case 1: guards the argument order (ikm, outputLength, salt, info) used throughout.
    [Fact]
    public void Rfc5869TestCase1()
    {
        var ikm = Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");

        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42, salt, info);

        Assert.Equal(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Convert.ToHexStringLower(okm));
    }

    [Fact]
    public void D2dConstantsAreSha256OfTheirNames()
    {
        Assert.Equal("82aa55a0d397f88346ca1cee8d3909b95f13fa7deb1d4ab38376b8256da85510",
            Convert.ToHexStringLower(SHA256.HashData("D2D"u8)));
        Assert.Equal("bf9d2a53c63616d75db0a7165b91c1ef73e537f2427405fa23610a4be657642e",
            Convert.ToHexStringLower(SHA256.HashData("SecureMessage"u8)));
    }
}

public class Ukey2Tests
{
    private static (Ukey2Result Client, Ukey2Result Server) Handshake()
    {
        using var client = new Ukey2Client();
        using var server = new Ukey2Server();

        var clientInit = client.CreateClientInit();
        var serverInit = server.ProcessClientInit(clientInit);
        var (clientFinish, clientResult) = client.ProcessServerInit(serverInit);
        var serverResult = server.ProcessClientFinish(clientFinish);
        return (clientResult, serverResult);
    }

    [Fact]
    public void BothSidesDeriveTheSameSecrets()
    {
        var (client, server) = Handshake();

        Assert.Equal(32, client.AuthString.Length);
        Assert.Equal(32, client.NextSecret.Length);
        Assert.Equal(client.AuthString, server.AuthString);
        Assert.Equal(client.NextSecret, server.NextSecret);
        Assert.NotEqual(client.AuthString, client.NextSecret);
        Assert.Equal(Pin.FromAuthString(client.AuthString), Pin.FromAuthString(server.AuthString));
    }

    [Fact]
    public void EachHandshakeProducesFreshSecrets()
    {
        Assert.NotEqual(Handshake().Client.NextSecret, Handshake().Client.NextSecret);
    }

    [Fact]
    public void ServerRejectsClientFinishThatDoesNotMatchTheCommitment()
    {
        using var client = new Ukey2Client();
        using var other = new Ukey2Client();
        using var server = new Ukey2Server();

        var serverInit = server.ProcessClientInit(client.CreateClientInit());
        other.CreateClientInit();
        var (otherFinish, _) = other.ProcessServerInit(serverInit);

        Assert.Throws<Ukey2Exception>(() => server.ProcessClientFinish(otherFinish));
    }

    [Fact]
    public void ServerRejectsGarbage()
    {
        using var server = new Ukey2Server();
        Assert.Throws<Ukey2Exception>(() => server.ProcessClientInit([0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void ClientRejectsAnUnexpectedMessageType()
    {
        using var client = new Ukey2Client();
        using var server = new Ukey2Server();
        var clientInit = client.CreateClientInit();
        // A ClientInit echoed back instead of a ServerInit.
        Assert.Throws<Ukey2Exception>(() => client.ProcessServerInit(clientInit));
        _ = server;
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02 }, new byte[] { 0x01, 0x02 })]
    [InlineData(new byte[] { 0x80, 0x01 }, new byte[] { 0x00, 0x80, 0x01 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x7F }, new byte[] { 0x7F })]
    [InlineData(new byte[] { 0x00, 0x80 }, new byte[] { 0x00, 0x80 })]
    public void SignedBigEndianMatchesJavaBigIntegerToByteArray(byte[] unsigned, byte[] expected) =>
        Assert.Equal(expected, Ukey2Common.ToSignedBigEndian(unsigned));

    [Fact]
    public void Fixed32PadsAndStripsSignByte()
    {
        var padded = Ukey2Common.ToFixed32([0x01, 0x02]);
        Assert.Equal(32, padded.Length);
        Assert.Equal(new byte[] { 0x01, 0x02 }, padded[30..]);
        Assert.All(padded[..30], b => Assert.Equal(0, b));

        var withSign = new byte[33];
        withSign[1] = 0x80;
        Assert.Equal(0x80, Ukey2Common.ToFixed32(withSign)[0]);

        Assert.Throws<Ukey2Exception>(() => Ukey2Common.ToFixed32(Enumerable.Repeat((byte)1, 33).ToArray()));
    }

    [Fact]
    public void PublicKeyEncodingSurvivesManyKeys()
    {
        // Random keys regularly have a leading zero byte or a high bit in X/Y; the codec must handle both.
        for (var i = 0; i < 200; i++)
        {
            using var key = Ukey2Common.NewKey();
            var encoded = Ukey2Common.EncodePublicKey(key);
            using var peer = Ukey2Common.NewKey();
            var decoded = Ukey2Common.DecodePublicKey(encoded);
            Assert.Equal(
                Convert.ToHexString(key.DeriveRawSecretAgreement(peer.PublicKey)),
                Convert.ToHexString(peer.DeriveRawSecretAgreement(decoded)));
        }
    }

    [Fact]
    public void DecodePublicKeyRejectsPointsNotOnTheCurve()
    {
        var bad = new SideBar.Nearby.Protocol.SecureMsg.GenericPublicKey
        {
            Type = SideBar.Nearby.Protocol.SecureMsg.PublicKeyType.EcP256,
            EcP256PublicKey = new SideBar.Nearby.Protocol.SecureMsg.EcP256PublicKey
            {
                X = Google.Protobuf.ByteString.CopyFrom([1]),
                Y = Google.Protobuf.ByteString.CopyFrom([1]),
            },
        }.ToByteArray();
        Assert.Throws<Ukey2Exception>(() => Ukey2Common.DecodePublicKey(bad));
    }
}

public class SecureChannelTests
{
    private static (SecureChannel Client, SecureChannel Server) Pair()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        return (SecureChannel.ForClient(secret), SecureChannel.ForServer(secret));
    }

    [Fact]
    public void RoundTripBothDirections()
    {
        var (client, server) = Pair();

        Assert.Equal("hello"u8.ToArray(), server.Decrypt(client.Encrypt("hello"u8)));
        Assert.Equal("world"u8.ToArray(), client.Decrypt(server.Encrypt("world"u8)));
        Assert.Equal(new byte[1000], server.Decrypt(client.Encrypt(new byte[1000])));
        Assert.Empty(server.Decrypt(client.Encrypt([])));
    }

    [Fact]
    public void LargePayloadRoundTrip()
    {
        var (client, server) = Pair();
        var data = RandomNumberGenerator.GetBytes(600 * 1024);
        Assert.Equal(data, server.Decrypt(client.Encrypt(data)));
    }

    [Fact]
    public void DirectionsUseDifferentKeys()
    {
        var (client, _) = Pair();
        var other = SecureChannel.ForClient(RandomNumberGenerator.GetBytes(32));
        // A message from the client must not be readable by another client-side channel (wrong key and wrong direction).
        Assert.Throws<SecureChannelException>(() => other.Decrypt(client.Encrypt("x"u8)));
        var (c2, _) = Pair();
        Assert.Throws<SecureChannelException>(() => c2.Decrypt(c2.Encrypt("x"u8)));
    }

    [Fact]
    public void TamperedMessageIsRejected()
    {
        var (client, server) = Pair();
        var message = client.Encrypt("secret"u8);
        message[message.Length / 2] ^= 0x01;
        Assert.Throws<SecureChannelException>(() => server.Decrypt(message));
    }

    [Fact]
    public void ReplayAndReorderingAreRejected()
    {
        var (client, server) = Pair();
        var first = client.Encrypt("1"u8);
        var second = client.Encrypt("2"u8);

        Assert.Throws<SecureChannelException>(() => server.Decrypt(second));
        server.Decrypt(first);
        Assert.Throws<SecureChannelException>(() => server.Decrypt(first));
        server.Decrypt(second);
    }

    [Fact]
    public void GarbageIsRejected()
    {
        var (_, server) = Pair();
        Assert.Throws<SecureChannelException>(() => server.Decrypt([1, 2, 3, 4]));
    }

    // Independent re-implementation of the documented wire format (NearDrop PROTOCOL.md), to catch structural mistakes.
    [Fact]
    public void DecryptsAMessageBuiltByHandFromTheDocumentedFormat()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var d2dSalt = SHA256.HashData("D2D"u8);
        var smSalt = SHA256.HashData("SecureMessage"u8);
        var d2dClient = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, d2dSalt, "client"u8.ToArray());
        var encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, d2dClient, 32, smSalt, "ENC:2"u8.ToArray());
        var sigKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, d2dClient, 32, smSalt, "SIG:1"u8.ToArray());

        var iv = RandomNumberGenerator.GetBytes(16);
        var inner = new byte[] { 9, 8, 7 };
        var d2d = new SideBar.Nearby.Protocol.SecureGcm.DeviceToDeviceMessage
        {
            SequenceNumber = 1,
            Message = Google.Protobuf.ByteString.CopyFrom(inner),
        }.ToByteArray();
        using var aes = Aes.Create();
        aes.Key = encKey;
        var body = aes.EncryptCbc(d2d, iv, PaddingMode.PKCS7);

        var headerAndBody = new SideBar.Nearby.Protocol.SecureMsg.HeaderAndBody
        {
            Header = new SideBar.Nearby.Protocol.SecureMsg.Header
            {
                SignatureScheme = SideBar.Nearby.Protocol.SecureMsg.SigScheme.HmacSha256,
                EncryptionScheme = SideBar.Nearby.Protocol.SecureMsg.EncScheme.Aes256Cbc,
                Iv = Google.Protobuf.ByteString.CopyFrom(iv),
            },
            Body = Google.Protobuf.ByteString.CopyFrom(body),
        }.ToByteArray();
        var message = new SideBar.Nearby.Protocol.SecureMsg.SecureMessage
        {
            HeaderAndBody = Google.Protobuf.ByteString.CopyFrom(headerAndBody),
            Signature = Google.Protobuf.ByteString.CopyFrom(HMACSHA256.HashData(sigKey, headerAndBody)),
        }.ToByteArray();

        // The client key is what a server decrypts with.
        Assert.Equal(inner, SecureChannel.ForServer(secret).Decrypt(message));
    }

    [Fact]
    public void FixedIvMakesEncryptionDeterministic()
    {
        var secret = new byte[32];
        var iv = new byte[16];
        Assert.Equal(
            SecureChannel.ForClient(secret).Encrypt("abc"u8, iv),
            SecureChannel.ForClient(secret).Encrypt("abc"u8, iv));
    }
}
