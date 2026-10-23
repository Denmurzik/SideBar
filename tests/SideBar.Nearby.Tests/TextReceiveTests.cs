using System.Text;
using SideBar.Nearby.Session;
using Share = SideBar.Nearby.Protocol.Sharing;

namespace SideBar.Nearby.Tests;

/// <summary>Receiving texts and links (Introduction.text_metadata + BYTES payload) and declining Wi-Fi credentials.</summary>
public sealed class TextReceiveTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "sidebar-nearby-text-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_out, recursive: true); } catch (IOException) { }
    }

    private static CancellationToken Token(int seconds = 30) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private Task<TransferResult> Receive(Link link, CancellationToken ct, Action<IncomingTransfer>? seen = null) =>
        ReceiveSession.ReceiveAsync(link.Server, new SessionOptions("Receiver"), _out,
            (offer, _) => { seen?.Invoke(offer); return Task.FromResult(true); }, null, ct);

    private static Share.TextMetadata Text(long payloadId, long size, Share.TextMetadata.Types.Type type = Share.TextMetadata.Types.Type.Text) =>
        new() { TextTitle = "preview", Type = type, PayloadId = payloadId, Size = size, Id = payloadId + 500 };

    private static Share.Frame Introduction(IEnumerable<Share.FileMetadata> files, IEnumerable<Share.TextMetadata> texts,
        int wifiNetworks = 0)
    {
        var frame = SharingFrames.Introduction(files, texts);
        for (var i = 0; i < wifiNetworks; i++)
            frame.V1.Introduction.WifiCredentialsMetadata.Add(new Share.WifiCredentialsMetadata { Ssid = "net" + i });
        return frame;
    }

    private static Task SendBytes(FakeSender peer, long payloadId, byte[] body, CancellationToken ct)
    {
        var header = OfflineFrames.BytesHeader(payloadId, body.Length);
        return peer.SendOfflineAsync(OfflineFrames.PayloadChunk(header, 0, body, last: true), ct);
    }

    private async Task<TransferResult> RoundTrip(string text, TextKind kind, Action<IncomingTransfer>? seen = null)
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct, seen);
        var send = await SendSession.SendTextAsync(link.Client, new SessionOptions("Sender"), text, kind, null, ct);
        Assert.Equal(TransferStatus.Completed, send.Status);
        return await receive;
    }

    [Fact]
    public async Task Text_share_is_received_verbatim()
    {
        IncomingTransfer? offer = null;
        var result = await RoundTrip("hello, world\nsecond line", TextKind.Text, o => offer = o);

        Assert.Equal(TransferStatus.Completed, result.Status);
        var text = Assert.Single(result.ReceivedTexts);
        Assert.Equal(new ReceivedText(TextKind.Text, "hello, world\nsecond line"), text);
        Assert.Empty(result.Files);
        Assert.Empty(offer!.Files);
        var announced = Assert.Single(offer.Texts);
        Assert.Equal(new IncomingText(TextKind.Text, Encoding.UTF8.GetByteCount("hello, world\nsecond line")), announced);
        Assert.False(offer.HasWifiCredentials);
        Assert.DoesNotContain("hello", result.Detail ?? "");
    }

    [Fact]
    public async Task Url_share_kind_is_url()
    {
        var result = await RoundTrip("https://example.com/a?b=c", TextKind.Url);

        Assert.Equal(new ReceivedText(TextKind.Url, "https://example.com/a?b=c"), Assert.Single(result.ReceivedTexts));
    }

    [Theory]
    [InlineData(Share.TextMetadata.Types.Type.Address, TextKind.Address)]
    [InlineData(Share.TextMetadata.Types.Type.PhoneNumber, TextKind.Phone)]
    [InlineData(Share.TextMetadata.Types.Type.Unknown, TextKind.Text)]
    public async Task Other_types_map_to_their_kind(Share.TextMetadata.Types.Type wire, TextKind expected)
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.SendSharingAsync(Introduction([], [Text(9, 3, wire)]), ct);
        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Accept, await peer.ReadResponseAsync(ct));
        await SendBytes(peer, 9, "abc"u8.ToArray(), ct);

        var result = await receive;
        Assert.Equal(new ReceivedText(expected, "abc"), Assert.Single(result.ReceivedTexts));
    }

    [Fact]
    public async Task Mixed_files_and_text()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.SendSharingAsync(Introduction([TestFrames.File(7, 4, "a.bin")], [Text(9, 5, Share.TextMetadata.Types.Type.Url)], wifiNetworks: 1), ct);
        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Accept, await peer.ReadResponseAsync(ct));
        await SendBytes(peer, 9, "a.b/c"u8.ToArray(), ct);
        await peer.SendOfflineAsync(TestFrames.FileChunk(7, 4, 0, [1, 2, 3, 4], false), ct);
        await peer.SendOfflineAsync(TestFrames.FileChunk(7, 4, 4, [], true), ct);

        var result = await receive;
        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Equal(new ReceivedText(TextKind.Url, "a.b/c"), Assert.Single(result.ReceivedTexts));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Assert.Single(result.Files)));
    }

    [Fact]
    public async Task Session_waits_for_the_text_after_the_files_are_done()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.SendSharingAsync(Introduction([TestFrames.File(7, 0, "z.bin")], [Text(9, 2)]), ct);
        await peer.ReadResponseAsync(ct);
        await peer.SendOfflineAsync(TestFrames.FileChunk(7, 0, 0, [], true), ct);
        await Task.Delay(300, ct);
        Assert.False(receive.IsCompleted);
        await SendBytes(peer, 9, "hi"u8.ToArray(), ct);

        Assert.Equal("hi", Assert.Single((await receive).ReceivedTexts).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Text_over_64KiB_is_protocol_error(bool announcedInIntroduction)
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        const int tooBig = 64 * 1024 + 1;
        await peer.SendSharingAsync(Introduction([], [Text(9, announcedInIntroduction ? tooBig : 10)]), ct);
        if (!announcedInIntroduction)
        {
            Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Accept, await peer.ReadResponseAsync(ct));
            await SendBytes(peer, 9, new byte[tooBig], ct);
        }

        var result = await receive;
        Assert.Equal(TransferStatus.ProtocolError, result.Status);
        Assert.Empty(result.ReceivedTexts);
    }

    [Fact]
    public async Task Repeated_text_payload_id_is_protocol_error()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.SendSharingAsync(Introduction([], [Text(9, 1), Text(9, 1)]), ct);

        Assert.Equal(TransferStatus.ProtocolError, (await receive).Status);
    }

    [Fact]
    public async Task Wifi_only_is_rejected()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var asked = false;
        var receive = ReceiveSession.ReceiveAsync(link.Server, new SessionOptions("Receiver"), _out,
            (_, _) => { asked = true; return Task.FromResult(true); }, null, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.SendSharingAsync(Introduction([], [], wifiNetworks: 1), ct);

        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Reject, await peer.ReadResponseAsync(ct));
        var result = await receive;
        Assert.Equal(TransferStatus.RejectedByPeer, result.Status);
        Assert.Equal("wifi-unsupported", result.Detail);
        Assert.False(asked);
    }

    [Fact]
    public async Task Mixed_wifi_and_text_ignores_wifi_but_tells_the_user()
    {
        IncomingTransfer? offer = null;
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct, o => offer = o);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.SendSharingAsync(Introduction([], [Text(9, 1)], wifiNetworks: 2), ct);
        Assert.Equal(Share.ConnectionResponseFrame.Types.Status.Accept, await peer.ReadResponseAsync(ct));
        await SendBytes(peer, 9, "x"u8.ToArray(), ct);

        Assert.Equal(TransferStatus.Completed, (await receive).Status);
        Assert.True(offer!.HasWifiCredentials);
    }

    [Fact]
    public async Task Unicode_text_round_trips()
    {
        const string text = "Привет, мир! é你好 \U0001F600 é ‮RLO";
        var result = await RoundTrip(text, TextKind.Text);

        Assert.Equal(text, Assert.Single(result.ReceivedTexts).Value);
    }

    [Fact]
    public async Task Long_text_with_a_surrogate_pair_at_the_title_cut_round_trips()
    {
        // 31 chars then an emoji: a naive title cut at 32 would split the surrogate pair.
        var text = new string('a', 31) + "\U0001F600" + new string('b', 100);
        var result = await RoundTrip(text, TextKind.Text);

        Assert.Equal(text, Assert.Single(result.ReceivedTexts).Value);
    }

    [Fact]
    public async Task Invalid_utf8_is_replaced_not_an_error()
    {
        var ct = Token();
        using var link = await Link.OpenAsync(ct);
        var receive = Receive(link, ct);
        await using var peer = await FakeSender.ConnectAsync(link.Client, ct);
        await peer.SendSharingAsync(Introduction([], [Text(9, 3)]), ct);
        await peer.ReadResponseAsync(ct);
        await SendBytes(peer, 9, [0x61, 0xFF, 0x62], ct);

        var result = await receive;
        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Equal("a�b", Assert.Single(result.ReceivedTexts).Value);
    }

    [Fact]
    public async Task Sender_rejects_a_text_over_the_limit()
    {
        var result = await SendSession.SendTextAsync(new MemoryStream(), new SessionOptions("S"),
            new string('x', 64 * 1024 + 1), TextKind.Text, null, Token());

        Assert.Equal(TransferStatus.ProtocolError, result.Status);
        Assert.DoesNotContain("xxx", result.Detail ?? "");
    }
}
