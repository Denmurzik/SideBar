using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;
using Xunit;

namespace SideBar.Nearby.Tests;

/// <summary>Sender and receiver sessions against each other over a loopback TCP connection.</summary>
public sealed class SessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sidebar-nearby-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _in;
    private readonly string _out;

    public SessionTests()
    {
        _in = Path.Combine(_dir, "in");
        _out = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_in);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Recorder : ITransferObserver
    {
        public string? Pin;
        public readonly List<TransferProgress> Progress = [];
        public void PinReady(string pin) => Pin = pin;
        void ITransferObserver.Progress(TransferProgress p) => Progress.Add(p);
    }

    private string MakeFile(string name, byte[] content)
    {
        var path = Path.Combine(_in, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private async Task<(TransferResult Send, TransferResult Receive, Recorder SendObs, Recorder ReceiveObs, IncomingTransfer? Offer)>
        RunAsync(IReadOnlyList<string> files, bool accept)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        IncomingTransfer? offer = null;
        var receiveObs = new Recorder();
        var receiveTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(cts.Token);
            return await ReceiveSession.ReceiveAsync(
                accepted.GetStream(), new SessionOptions("Receiver PC"), _out,
                (o, _) => { offer = o; return Task.FromResult(accept); },
                receiveObs, cts.Token);
        });

        var sendObs = new Recorder();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var send = await SendSession.SendFilesAsync(
            client.GetStream(), new SessionOptions("Sender PC"), files, sendObs, cts.Token);
        var receive = await receiveTask;
        listener.Stop();
        return (send, receive, sendObs, receiveObs, offer);
    }

    [Fact]
    public async Task TransfersFilesOfDifferentSizesIntact()
    {
        var small = RandomNumberGenerator.GetBytes(1234);
        var big = RandomNumberGenerator.GetBytes(OfflineFrames.PayloadChunkSize * 2 + 17);
        var files = new[]
        {
            MakeFile("small.bin", small),
            MakeFile("big.png", big),
            MakeFile("empty.txt", []),
        };

        var (send, receive, sendObs, receiveObs, offer) = await RunAsync(files, accept: true);

        Assert.Equal(TransferStatus.Completed, send.Status);
        Assert.Equal(TransferStatus.Completed, receive.Status);
        Assert.Equal(3, receive.Files.Count);
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(_out, "small.bin")));
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(_out, "big.png")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(_out, "empty.txt")));

        Assert.NotNull(sendObs.Pin);
        Assert.Equal(4, sendObs.Pin.Length);
        Assert.Equal(sendObs.Pin, receiveObs.Pin);
        Assert.Equal(sendObs.Pin, offer!.Pin);
        Assert.Equal("Sender PC", offer.SenderName);
        Assert.Equal(["small.bin", "big.png", "empty.txt"], offer.Files.Select(f => f.Name));
        Assert.Equal("image/png", offer.Files[1].MimeType);
        Assert.Equal(big.Length, offer.Files[1].Size);

        Assert.True(sendObs.Progress.Count >= 4);
        Assert.Equal(small.Length + big.Length, sendObs.Progress[^1].TotalBytes);
    }

    [Fact]
    public async Task RejectedTransferWritesNothing()
    {
        var (send, receive, _, _, _) = await RunAsync([MakeFile("a.bin", [1, 2, 3])], accept: false);

        Assert.Equal(TransferStatus.RejectedByPeer, send.Status);
        Assert.Equal(TransferStatus.RejectedByPeer, receive.Status);
        Assert.False(Directory.Exists(_out) && Directory.EnumerateFiles(_out).Any());
    }

    [Fact]
    public async Task ExistingFilesAreNotOverwritten()
    {
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "doc.txt"), "old");

        var (send, receive, _, _, _) = await RunAsync([MakeFile("doc.txt", "new"u8.ToArray())], accept: true);

        Assert.Equal(TransferStatus.Completed, send.Status);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_out, "doc.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_out, "doc (1).txt")));
        Assert.Equal(Path.Combine(_out, "doc (1).txt"), Assert.Single(receive.Files));
    }

    [Theory]
    [InlineData("..\\..\\evil.exe", "evil.exe")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("we:ird?.txt", "we_ird_.txt")]
    [InlineData("   ", "file")]
    [InlineData("name.", "name")]
    [InlineData("", "file")]
    public void SanitizeFileName(string input, string expected) =>
        Assert.Equal(expected, ReceiveSession.SanitizeFileName(input));

    [Fact]
    public async Task SenderDisconnectsOnlyAfterTheReceiverHasFinished()
    {
        // Static log: only this class opens sessions, and xunit runs the tests of one class serially.
        var trace = new List<NearbyLogEvent>();
        NearbyLog.Log = e => { lock (trace) trace.Add(e); };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var receive = Task.Run(async () =>
            {
                using var c = await listener.AcceptTcpClientAsync(cts.Token);
                return await ReceiveSession.ReceiveAsync(c.GetStream(), new SessionOptions("R"), _out,
                    (_, _) => Task.FromResult(true), null, cts.Token);
            });
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cts.Token);
            var send = await SendSession.SendFilesAsync(client.GetStream(),
                new SessionOptions("S"),
                [MakeFile("t.bin", RandomNumberGenerator.GetBytes(5000))], null, cts.Token);
            await receive;
            listener.Stop();

            Assert.Equal(TransferStatus.Completed, send.Status);
            var log = string.Join(" | ", trace.Select(e => $"{e.Kind}:{e.Value}"));
            var last = trace.FindLastIndex(e => e.Kind == "send.tx.PayloadTransfer" && e.Value == "LastChunk");
            var peerDisconnect = trace.FindIndex(e => e.Kind == "send.rx.Disconnection");
            var ourDisconnect = trace.FindIndex(e => e.Kind == "send.tx.Disconnection");
            Assert.True(last >= 0 && peerDisconnect > last, log);
            Assert.True(ourDisconnect == -1 || ourDisconnect > peerDisconnect, log);
        }
        finally { NearbyLog.Log = null; }
    }

    [Fact]
    public async Task SenderReportsFailureWhenTheReceiverSpeaksGarbage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rogue = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync(cts.Token);
            var s = c.GetStream();
            await FrameIO.ReadAsync(s, cts.Token);
            await FrameIO.ReadAsync(s, cts.Token);
            await FrameIO.WriteAsync(s, new byte[] { 0xFF, 0xFF, 0xFF }, cts.Token);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var result = await SendSession.SendFilesAsync(
            client.GetStream(), new SessionOptions("x"), [MakeFile("a.bin", [1])], null, cts.Token);
        await rogue;
        listener.Stop();

        Assert.Equal(TransferStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ReceiverReportsFailureWhenTheSenderDisappears()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receive = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync(cts.Token);
            return await ReceiveSession.ReceiveAsync(
                c.GetStream(), new SessionOptions("r"), _out, (_, _) => Task.FromResult(true), null, cts.Token);
        });

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            await FrameIO.WriteAsync(client.GetStream(), new byte[] { 1, 2, 3 }, cts.Token);
        }

        Assert.Equal(TransferStatus.Failed, (await receive).Status);
        listener.Stop();
    }
}

public class OutputPathTests
{
    [Fact]
    public void UniquePathAddsCounter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sidebar-nearby-unique-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(Path.Combine(dir, "a.txt"), ReceiveSession.UniquePath(dir, "a.txt"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "");
            File.WriteAllText(Path.Combine(dir, "a (1).txt"), "");
            Assert.Equal(Path.Combine(dir, "a (2).txt"), ReceiveSession.UniquePath(dir, "a.txt"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
