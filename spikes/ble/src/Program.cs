using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

// Usage: Ble info | Ble watch [seconds] [--identify] | Ble publish [seconds] [options]
//   watch --identify: match FEF3 (Nearby Connections) bodies against the official app's public certificates in memory;
//                     prints only counts, body length, match offset, RSSI, timings and the matched peer's device_name.
//   publish options: --type notify|silent  --ext|--legacy  --cert 0|1  --tx HEX  --static
//                    --hash random|selfshare  --raw HEX (full service data after the UUID, overrides everything)
//                    --short (Chromium 5-byte form: FC128E meta tx)  --flags HEX (last byte of the 24-byte form)
//                    --uuidlist (also list FE2C as a 16-bit service UUID, like Chromium's advertiser)
// FastInit V1 layout (sharing/internal/api/fast_init_ble_beacon.h, 24 bytes after the FE2C UUID, same as rquickshare blea.rs):
//   FC 12 8E | meta[version(3) type(3) uwb(1) sender_cert(1)] | adjusted_tx_power | uwb_metadata(1) uwb_address(8) | salt(1) | secret_id_hash(8) | flags(1)
// Never prints device addresses or names; only section types, UUIDs of interest, lengths and counters.
const ushort FastInitUuid = 0xFE2C;   // Fast Initiation (Fast Pair UUID + model id FC128E)
const ushort NearbyConnUuid = 0xFEF3; // Nearby Connections BLE advertisement (Copresence)
const byte ServiceData16 = 0x16;

string cmd = args.Length > 0 ? args[0] : "info";
int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 20;

var adapter = await BluetoothAdapter.GetDefaultAsync();
if (adapter is null) { Console.WriteLine("no bluetooth adapter"); return 1; }

if (cmd == "info")
{
    Console.WriteLine($"LowEnergySupported={adapter.IsLowEnergySupported} CentralRole={adapter.IsCentralRoleSupported} PeripheralRole={adapter.IsPeripheralRoleSupported}");
    Console.WriteLine($"ExtendedAdvertisingSupported={adapter.IsExtendedAdvertisingSupported} MaxAdvertisementDataLength={adapter.MaxAdvertisementDataLength} AdvertisementOffloadSupported={adapter.IsAdvertisementOffloadSupported}");
    Console.WriteLine($"ClassicSupported={adapter.IsClassicSupported} LowEnergySecureConnections={adapter.AreLowEnergySecureConnectionsSupported}");
    return 0;
}

if (cmd == "watch")
{
    int total = 0, withSd = 0, fe2c = 0, fef3 = 0;
    var types = new Dictionary<byte, int>();
    var fe2cShapes = new Dictionary<string, int>();
    var identify = Array.IndexOf(args, "--identify") >= 0;
    var certs = identify ? PeerCerts.Load() : null;
    if (certs is not null)
        foreach (var p in certs.Peers)
            Console.WriteLine($"peer cert #{p.Index}: device={p.DeviceName ?? "?"} validNow={p.ValidNow} hoursLeft={p.HoursLeft:F1}");
    var started = System.Diagnostics.Stopwatch.StartNew();
    var fef3Results = new Dictionary<string, (PeerCert? Peer, int Offset)>(); // body -> match, memory only
    var fef3Shapes = new Dictionary<string, (int Count, short RssiMin, short RssiMax, double FirstSeconds)>();
    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
    watcher.AllowExtendedAdvertisements = true;
    watcher.Received += (_, e) =>
    {
        Interlocked.Increment(ref total);
        bool sd = false;
        lock (types)
            foreach (var sec in e.Advertisement.DataSections)
            {
                types[sec.DataType] = types.GetValueOrDefault(sec.DataType) + 1;
                if (sec.DataType != ServiceData16 || sec.Data.Length < 2) continue;
                sd = true;
                var b = sec.Data.ToArray();
                ushort uuid = (ushort)(b[0] | (b[1] << 8));
                if (uuid == FastInitUuid)
                {
                    fe2c++;
                    string shape = $"len={b.Length - 2} head={Convert.ToHexString(b, 2, Math.Min(5, b.Length - 2))} ext={e.AdvertisementType}";
                    // Fast Init bodies (FC128E...) carry no identifiers (salt and hash are ephemeral): print them whole.
                    if (b.Length >= 5 && b[2] == 0xFC && b[3] == 0x12 && b[4] == 0x8E)
                        shape += $" body={Convert.ToHexString(b, 2, b.Length - 2)} rssi={e.RawSignalStrengthInDBm}";
                    fe2cShapes[shape] = fe2cShapes.GetValueOrDefault(shape) + 1;
                }
                else if (uuid == NearbyConnUuid)
                {
                    fef3++;
                    if (certs is null) continue;
                    var body = b.AsSpan(2);
                    var key = Convert.ToHexString(body);
                    if (!fef3Results.TryGetValue(key, out var r)) fef3Results[key] = r = Match(certs, body);
                    string shape = $"len={body.Length} {e.AdvertisementType} " +
                                   (r.Peer is null ? "own-account=no" : $"own-account=yes cert=#{r.Peer.Index} device={r.Peer.DeviceName ?? "?"} offset={r.Offset}");
                    var rssi = e.RawSignalStrengthInDBm;
                    fef3Shapes[shape] = fef3Shapes.TryGetValue(shape, out var v)
                        ? (v.Count + 1, Math.Min(v.RssiMin, rssi), Math.Max(v.RssiMax, rssi), v.FirstSeconds)
                        : (1, rssi, rssi, started.Elapsed.TotalSeconds);
                }
            }
        if (sd) Interlocked.Increment(ref withSd);
    };
    watcher.Stopped += (_, e) => Console.WriteLine($"watcher stopped: {e.Error}");
    watcher.Start();
    Console.WriteLine($"watching {seconds}s (status={watcher.Status})");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    watcher.Stop();
    Console.WriteLine($"advertisements={total} withServiceData16={withSd} FE2C={fe2c} FEF3={fef3}");
    Console.WriteLine($"section types seen: {string.Join(", ", types.OrderBy(k => k.Key).Select(k => $"0x{k.Key:X2}x{k.Value}"))}");
    foreach (var (shape, n) in fe2cShapes) Console.WriteLine($"  FE2C x{n}: {shape}");
    foreach (var (shape, v) in fef3Shapes)
        Console.WriteLine($"  FEF3 x{v.Count}: {shape} rssi=[{v.RssiMin}..{v.RssiMax}] first={v.FirstSeconds:F1}s");
    if (certs is not null) Console.WriteLine($"FEF3 distinct bodies={fef3Results.Count} matched={fef3Results.Values.Count(r => r.Peer is not null)}");
    return 0;
}

if (cmd == "publish")
{
    string Opt(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    bool Has(string name) => Array.IndexOf(args, name) >= 0;

    var silent = Opt("--type", "notify") == "silent";
    var cert = Opt("--cert", "1") == "1";
    var tx = Convert.ToByte(Opt("--tx", "42"), 16);
    var extended = Has("--ext");
    var hashMode = Opt("--hash", "random");
    // Last byte: require_bt_advertising | self_only_advertising | unused(6); bit order is not documented.
    var flags = Convert.ToByte(Opt("--flags", "00"), 16);

    byte[] body;
    if (Has("--raw")) body = Convert.FromHexString(Opt("--raw", ""));
    else if (Has("--static"))
        // Verbatim beacon that rquickshare broadcasts (copied from a real Android sender).
        body = [252, 18, 142, 1, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 191, 45, 91, 160, 225, 216, 117, 36, 202, 0];
    else if (Has("--short"))
        // chrome/browser/nearby_sharing/fast_initiation/fast_initiation_advertiser.cc GenerateFastInitV1Metadata:
        // version << 5 | type << 2 (uwb and sender_cert bits left empty), then -kAdjustedTxPower (66).
        body = [0xFC, 0x12, 0x8E, (byte)((silent ? 1 : 0) << 2), tx];
    else
    {
        byte meta = (byte)((0 << 5) | ((silent ? 1 : 0) << 2) | (0 << 1) | (cert ? 1 : 0));
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1);
        var hash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        if (hashMode == "selfshare")
        {
            // Hypothesis (not documented in open sources): HKDF-SHA256(ikm = salt, salt = SELF_SHARE secret_key, 8 bytes).
            var identity = SideBar.Nearby.Session.OfficialCertIdentity.Load();
            hash = identity.DeriveFastInitHash(salt, 8) ?? throw new InvalidOperationException("no SELF_SHARE certificate");
        }
        body = [0xFC, 0x12, 0x8E, meta, tx, .. new byte[9], .. salt, .. hash, flags];
    }

    byte[] payload = [(byte)(FastInitUuid & 0xff), (byte)(FastInitUuid >> 8), .. body];
    var adv = new BluetoothLEAdvertisement();
    adv.DataSections.Add(new BluetoothLEAdvertisementDataSection(ServiceData16, payload.AsBuffer()));
    if (Has("--uuidlist"))
        // 0x03 is reserved by WinRT; the property may be refused, Start() reports it below.
        try { adv.ServiceUuids.Add(BluetoothUuidHelper.FromShortId(FastInitUuid)); }
        catch (Exception ex) { Console.WriteLine($"ServiceUuids.Add threw {ex.GetType().Name}: 0x{ex.HResult:X8}"); return 2; }
    var pub = new BluetoothLEAdvertisementPublisher(adv) { UseExtendedAdvertisement = extended };
    pub.StatusChanged += (_, e) => Console.WriteLine($"publisher status={e.Status} error={e.Error}");
    try { pub.Start(); }
    catch (Exception ex) { Console.WriteLine($"Start threw {ex.GetType().Name}: 0x{ex.HResult:X8}"); return 2; }
    Console.WriteLine($"publisher started, status={pub.Status}, {(extended ? "extended" : "legacy")}, bodyLen={body.Length}, meta=0x{(body.Length > 3 ? body[3] : 0):X2}, " +
                      $"{(Has("--static") ? "static" : Has("--raw") ? "raw" : Has("--short") ? $"short type={(silent ? "silent" : "notify")}" : $"type={(silent ? "silent" : "notify")} cert={cert} hash={hashMode} flags=0x{flags:X2}")}" +
                      $"{(Has("--uuidlist") ? ", uuidlist" : "")}");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    Console.WriteLine($"status before stop={pub.Status}");
    pub.Stop();
    await Task.Delay(500);
    Console.WriteLine($"status after stop={pub.Status}");
    return 0;
}

Console.WriteLine("unknown command");
return 64;

// The FEF3 framing on Android is not known exactly (BLE v1 vs the v2 medium wrapper), so try every offset where a
// 17-byte endpoint info (header + salt(2) + encrypted metadata key(14)) could start.
static (PeerCert? Peer, int Offset) Match(PeerCerts certs, ReadOnlySpan<byte> body)
{
    for (int o = 0; o + 17 <= body.Length; o++)
        if (certs.Identify(body.Slice(o + 1, 16)) is { } peer) return (peer, o);
    return (null, -1);
}
