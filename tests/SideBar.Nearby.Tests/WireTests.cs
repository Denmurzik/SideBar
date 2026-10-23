using SideBar.Nearby.Discovery;
using SideBar.Nearby.Wire;
using Xunit;

namespace SideBar.Nearby.Tests;

public class FrameIoTests
{
    [Fact]
    public void Frame_PrefixesBigEndianLength()
    {
        var framed = FrameIO.Frame([0xAA, 0xBB, 0xCC]);
        Assert.Equal(new byte[] { 0, 0, 0, 3, 0xAA, 0xBB, 0xCC }, framed);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsSeveralFrames()
    {
        var ms = new MemoryStream();
        await FrameIO.WriteAsync(ms, new byte[] { 1, 2, 3 });
        await FrameIO.WriteAsync(ms, ReadOnlyMemory<byte>.Empty);
        await FrameIO.WriteAsync(ms, new byte[70_000]);
        ms.Position = 0;

        Assert.Equal(new byte[] { 1, 2, 3 }, await FrameIO.ReadAsync(ms));
        Assert.Empty(await FrameIO.ReadAsync(ms));
        Assert.Equal(70_000, (await FrameIO.ReadAsync(ms)).Length);
    }

    [Fact]
    public async Task Read_RejectsOversizedLength()
    {
        var ms = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameIO.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_RejectsNegativeLength()
    {
        var ms = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FrameIO.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_ThrowsOnTruncatedBody()
    {
        var ms = new MemoryStream([0, 0, 0, 5, 1, 2]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await FrameIO.ReadAsync(ms));
    }
}

public class EndpointInfoTests
{
    [Fact]
    public void Encode_PutsVersionVisibilityAndTypeInTheFirstByte()
    {
        var info = new EndpointInfo { Version = 1, Kind = DeviceKind.Laptop, Name = "PC" };
        Assert.Equal((1 << 5) | (3 << 1), info.Encode()[0]);

        var hidden = new EndpointInfo { Kind = DeviceKind.Phone, Hidden = true };
        Assert.Equal(0x10 | (1 << 1), hidden.Encode()[0]);
    }

    [Fact]
    public void Encode_MatchesDocumentedLayout()
    {
        var metadata = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var info = new EndpointInfo { Kind = DeviceKind.Laptop, Name = "Den", Metadata = metadata };

        var expected = new List<byte> { 0x06 };
        expected.AddRange(metadata);
        expected.AddRange([3, (byte)'D', (byte)'e', (byte)'n']);
        Assert.Equal(expected, info.Encode());
    }

    [Fact]
    public void RoundTrip_VisibleWithTlvs()
    {
        var info = new EndpointInfo
        {
            Kind = DeviceKind.Tablet,
            Name = "Планшет",
            Metadata = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            Tlvs = [new EndpointTlv(EndpointTlv.VendorId, [1]), new EndpointTlv(EndpointTlv.QrCode, [9, 8, 7])],
        };

        Assert.True(EndpointInfo.TryDecode(info.Encode(), out var decoded));
        Assert.Equal(DeviceKind.Tablet, decoded!.Kind);
        Assert.False(decoded.Hidden);
        Assert.Equal("Планшет", decoded.Name);
        Assert.Equal(info.Metadata, decoded.Metadata);
        Assert.Equal(2, decoded.Tlvs.Count);
        Assert.Equal(EndpointTlv.QrCode, decoded.Tlvs[1].Type);
        Assert.Equal(new byte[] { 9, 8, 7 }, decoded.Tlvs[1].Value);
    }

    [Fact]
    public void RoundTrip_HiddenHasNoName()
    {
        var info = new EndpointInfo { Kind = DeviceKind.Phone, Hidden = true, Name = "ignored" };
        Assert.True(EndpointInfo.TryDecode(info.Encode(), out var decoded));
        Assert.True(decoded!.Hidden);
        Assert.Null(decoded.Name);
    }

    [Fact]
    public void Base64Url_RoundTrip()
    {
        var info = EndpointInfo.CreateAnonymous("Pixel 9", DeviceKind.Phone);
        var text = info.ToBase64Url();
        Assert.DoesNotContain('=', text);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.True(EndpointInfo.TryFromBase64Url(text, out var decoded));
        Assert.Equal("Pixel 9", decoded!.Name);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x06, 1, 2, 3 })]
    public void TryDecode_RejectsShortInput(byte[] data) =>
        Assert.False(EndpointInfo.TryDecode(data, out _));

    [Fact]
    public void TryDecode_RejectsNameLongerThanData()
    {
        var data = new byte[17 + 1];
        data[17] = 200;
        Assert.False(EndpointInfo.TryDecode(data, out _));
    }

    [Fact]
    public void TryDecode_RejectsTruncatedTlv()
    {
        var data = new EndpointInfo { Name = "x", Tlvs = [new EndpointTlv(1, [1, 2, 3])] }.Encode();
        Assert.False(EndpointInfo.TryDecode(data.AsSpan(0, data.Length - 1), out _));
    }

    [Fact]
    public void Encode_TruncatesLongNamesOnACharacterBoundary()
    {
        var info = new EndpointInfo { Name = new string('ы', 200) };
        Assert.True(EndpointInfo.TryDecode(info.Encode(), out var decoded));
        Assert.Equal(127, decoded!.Name!.Length);
    }

    [Fact]
    public void TryFromBase64Url_RejectsGarbage() =>
        Assert.False(EndpointInfo.TryFromBase64Url("!!!not base64!!!", out _));
}

public class MdnsNameTests
{
    [Fact]
    public void Encode_MatchesKnownVector()
    {
        // 0x23 'A' 'B' 'C' 'D' FC 9F 5E 00 00
        Assert.Equal("I0FCQ0T8n14AAA", MdnsName.Encode("ABCD"));
    }

    [Fact]
    public void TryDecode_RecoversEndpointId()
    {
        Assert.True(MdnsName.TryDecode("I0FCQ0T8n14AAA", out var id));
        Assert.Equal("ABCD", id);
    }

    [Fact]
    public void RoundTrip_RandomIds()
    {
        for (var i = 0; i < 50; i++)
        {
            var id = MdnsName.NewEndpointId();
            Assert.Equal(4, id.Length);
            Assert.All(id, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
            Assert.True(MdnsName.TryDecode(MdnsName.Encode(id), out var decoded));
            Assert.Equal(id, decoded);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-name")]
    [InlineData("AAAAAAAAAAAAAA")]
    public void TryDecode_RejectsForeignNames(string name) => Assert.False(MdnsName.TryDecode(name, out _));

    [Fact]
    public void TryGetInstanceLabel_StripsServiceType()
    {
        Assert.True(MdnsName.TryGetInstanceLabel("I0FCQ0T8n14AAA._FC9F5ED42C8A._tcp.local", out var label));
        Assert.Equal("I0FCQ0T8n14AAA", label);
        Assert.False(MdnsName.TryGetInstanceLabel("something._other._tcp.local", out _));
    }

    [Fact]
    public void ToEndpoint_BuildsFromResolvedService()
    {
        var info = EndpointInfo.CreateAnonymous("Pixel 9", DeviceKind.Phone);
        var service = new ResolvedService(
            "I0FCQ0T8n14AAA._FC9F5ED42C8A._tcp.local", "pixel.local",
            [System.Net.IPAddress.Parse("192.168.1.20")], 40123,
            new Dictionary<string, string> { ["n"] = info.ToBase64Url() });

        var endpoint = DiscoveryParsing.ToEndpoint(service);

        Assert.NotNull(endpoint);
        Assert.Equal("ABCD", endpoint.EndpointId);
        Assert.Equal("Pixel 9", endpoint.DisplayName);
        Assert.Equal(DeviceKind.Phone, endpoint.Kind);
        Assert.Equal(40123, endpoint.Port);
    }

    [Fact]
    public void ToEndpoint_SkipsServicesWithoutAddressOrInfo()
    {
        var noAddress = new ResolvedService("I0FCQ0T8n14AAA._FC9F5ED42C8A._tcp.local", "h", [], 1,
            new Dictionary<string, string> { ["n"] = EndpointInfo.CreateAnonymous("x", DeviceKind.Phone).ToBase64Url() });
        var noInfo = new ResolvedService("I0FCQ0T8n14AAA._FC9F5ED42C8A._tcp.local", "h",
            [System.Net.IPAddress.Loopback], 1, new Dictionary<string, string>());

        Assert.Null(DiscoveryParsing.ToEndpoint(noAddress));
        Assert.Null(DiscoveryParsing.ToEndpoint(noInfo));
    }
}
