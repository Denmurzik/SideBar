using System.Net;
using System.Net.Sockets;
using SideBar.Nearby;
using SideBar.Nearby.Discovery;
using SideBar.Nearby.Session;
using SideBar.Nearby.Wire;

const string Usage = """
    nearby discover [--timeout SEC] [--include-local]
    nearby send <file>... [--timeout SEC] [--to INDEX|NAME] [--name DEVICE_NAME] [--include-local]
    nearby receive [--out DIR] [--yes] [--name DEVICE_NAME] [--reannounce SEC]
    (--trace: print frame kinds and sizes in both directions, no payload content)
    """;

if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }

var command = args[0];
var rest = args.Skip(1).ToList();
var options = new CliOptions(rest);
var deviceName = options.Value("--name") ?? Environment.MachineName;

var session = new SessionOptions(deviceName);
if (options.Flag("--trace"))
    NearbyLog.Log = e => Console.WriteLine($"[trace] {e.Kind}{(e.Size is null ? "" : " " + e.Size)}{(e.Value is null ? "" : " " + e.Value)}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return command switch
    {
        "discover" => await Discover(options, cts.Token),
        "send" => await Send(options, session, cts.Token),
        "receive" => await Receive(options, session, cts.Token),
        _ => Fail(Usage),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 130;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static async Task<List<DiscoveredEndpoint>> BrowseAsync(
    TimeSpan timeout, bool includeLocal, CancellationToken ct)
{
    var found = new List<DiscoveredEndpoint>();
    using var browser = new QuickShareBrowser();
    var ownSkipped = 0;
    browser.Found += found0 =>
    {
        var endpoint = found0;
        if (!includeLocal && QuickShareAdvertiser.IsLocalAddress(endpoint.Address))
        {
            Interlocked.Increment(ref ownSkipped);
            return;
        }
        lock (found)
        {
            if (found.Any(e => e.InstanceName == endpoint.InstanceName)) return;
            found.Add(endpoint);
            Console.WriteLine($"  [{found.Count}] {endpoint.DisplayName}  ({endpoint.Kind}, {endpoint.Address}:{endpoint.Port}, id {endpoint.EndpointId})");
        }
    };
    browser.Start();
    Console.WriteLine($"Searching for Quick Share devices for {timeout.TotalSeconds:0} s (Windows DNS-SD)...");
    try { await Task.Delay(timeout, ct); }
    catch (OperationCanceledException) { }
    if (ownSkipped > 0) Console.WriteLine($"Ignored {ownSkipped} service(s) of this machine (use --include-local to show them).");
    lock (found) return [.. found];
}

static async Task<int> Discover(CliOptions options, CancellationToken ct)
{
    var found = await BrowseAsync(TimeSpan.FromSeconds(options.Int("--timeout", 10)), options.Flag("--include-local"), ct);
    Console.WriteLine($"Found: {found.Count}");
    return found.Count > 0 ? 0 : 1;
}

static async Task<int> Send(CliOptions options, SessionOptions session, CancellationToken ct)
{
    var files = options.Positional.Select(Path.GetFullPath).ToList();
    if (files.Count == 0 || files.Any(f => !File.Exists(f))) return Fail("Specify existing files to send.");

    var found = await BrowseAsync(TimeSpan.FromSeconds(options.Int("--timeout", 10)), options.Flag("--include-local"), ct);
    if (found.Count == 0) return Fail("No devices found.");

    DiscoveredEndpoint? target = null;
    var to = options.Value("--to");
    if (to is not null)
        target = int.TryParse(to, out var index) && index >= 1 && index <= found.Count
            ? found[index - 1]
            : found.FirstOrDefault(e => e.DisplayName.Contains(to, StringComparison.OrdinalIgnoreCase));
    else if (found.Count == 1)
        target = found[0];
    else
    {
        Console.Write("Send to number: ");
        if (int.TryParse(Console.ReadLine(), out var chosen) && chosen >= 1 && chosen <= found.Count)
            target = found[chosen - 1];
    }
    if (target is null) return Fail("Device not chosen.");

    Console.WriteLine($"Connecting to {target.DisplayName} at {target.Address}:{target.Port}...");
    using var client = new TcpClient { NoDelay = true };
    await client.ConnectAsync(target.Address, target.Port, ct);

    var result = await SendSession.SendFilesAsync(client.GetStream(), session with { RemoteEndpointInfo = target.Info }, files, new ConsoleObserver(), ct);
    Console.WriteLine();
    Console.WriteLine($"Result: {result.Status}{(result.Detail is null ? "" : " - " + result.Detail)}");
    return result.Status == TransferStatus.Completed ? 0 : 1;
}

static async Task<int> Receive(CliOptions options, SessionOptions session, CancellationToken ct)
{
    var outDir = Path.GetFullPath(options.Value("--out") ?? "out");
    var autoAccept = options.Flag("--yes");
    var reannounce = options.Int("--reannounce", 30);

    if (NetworkInfo.DefaultRouteInterface() is not { } lan) return Fail("No IPv4 interface with a default route.");
    var listener = new TcpListener(lan.Address, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var info = session.ResolvedIdentity.CreateEndpointInfo(session.DeviceName, session.DeviceKind);
    using var advertiser = new QuickShareAdvertiser(MdnsName.NewEndpointId(), info.Encode(), port, lan.Address, lan.InterfaceIndex);
    await advertiser.RegisterAsync(ct);
    AppDomain.CurrentDomain.ProcessExit += (_, _) => advertiser.Dispose();
    Console.WriteLine($"Advertising '{session.DeviceName}' as {advertiser.InstanceName} on {advertiser.Address}:{port}");
    Console.WriteLine($"Files go to {outDir}. On the phone: Quick Share -> send a file -> pick this PC. Ctrl+C to stop.");
    if (reannounce > 0) Console.WriteLine($"The service is re-announced every {reannounce} s (Android can miss services registered before it started searching).");

    if (reannounce > 0)
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(reannounce));
            while (await timer.WaitForNextTickAsync(ct))
                try { await advertiser.ReannounceAsync(ct); }
                catch (Exception) { }
        }, ct);

    while (!ct.IsCancellationRequested)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        client.NoDelay = true;
        Console.WriteLine($"Incoming connection from {client.Client.RemoteEndPoint}");
        var result = await ReceiveSession.ReceiveAsync(
            client.GetStream(), session, outDir,
            (offer, token) => AskConsent(offer, autoAccept, token),
            new ConsoleObserver(), ct);
        Console.WriteLine();
        Console.WriteLine($"Result: {result.Status}{(result.Detail is null ? "" : " - " + result.Detail)}");
        foreach (var file in result.Files) Console.WriteLine($"  saved {file}");
    }
    return 0;
}

static Task<bool> AskConsent(IncomingTransfer offer, bool autoAccept, CancellationToken ct)
{
    Console.WriteLine($"{offer.SenderName ?? "Unknown device"} wants to send {offer.Files.Count} file(s), PIN {offer.Pin}:");
    foreach (var f in offer.Files) Console.WriteLine($"  {f.Name}  {f.Size:N0} bytes  {f.MimeType}");
    if (autoAccept) return Task.FromResult(true);
    Console.Write("Accept? [y/N] ");
    return Task.Run(() => string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase), ct);
}

internal sealed class ConsoleObserver : ITransferObserver
{
    private int _lastPercent = -1;

    public void PinReady(string pin) => Console.WriteLine($"PIN: {pin}  (it must match the one on the phone)");

    public void Status(string message) => Console.WriteLine(message);

    public void Progress(TransferProgress p)
    {
        var percent = p.TotalSize == 0 ? 100 : (int)(p.TotalBytes * 100 / p.TotalSize);
        if (percent == _lastPercent) return;
        _lastPercent = percent;
        Console.Write($"\r{p.FileName}: {percent}% ({p.TotalBytes:N0} / {p.TotalSize:N0} bytes)   ");
    }
}

internal sealed class CliOptions
{
    private static readonly HashSet<string> Flags = ["--yes", "--trace", "--include-local"];
    private readonly Dictionary<string, string> _values = new();
    private readonly HashSet<string> _flags = [];
    public List<string> Positional { get; } = [];

    public CliOptions(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) Positional.Add(a);
            else if (Flags.Contains(a)) _flags.Add(a);
            else if (i + 1 < args.Count) _values[a] = args[++i];
        }
    }

    public string? Value(string name) => _values.GetValueOrDefault(name);
    public bool Flag(string name) => _flags.Contains(name);
    public int Int(string name, int fallback) => int.TryParse(Value(name), out var v) ? v : fallback;
}
