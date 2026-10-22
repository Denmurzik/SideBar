using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

// Usage: Ble info | Ble watch [seconds] | Ble publish [seconds] [options]
//   publish options: --type notify|silent  --ext|--legacy  --cert 0|1  --tx HEX  --static
//                    --hash random|selfshare  --raw HEX (full service data after the UUID, overrides everything)
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
                else if (uuid == NearbyConnUuid) fef3++;
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

    byte[] body;
    if (Has("--raw")) body = Convert.FromHexString(Opt("--raw", ""));
    else if (Has("--static"))
        // Verbatim beacon that rquickshare broadcasts (copied from a real Android sender).
        body = [252, 18, 142, 1, 66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 191, 45, 91, 160, 225, 216, 117, 36, 202, 0];
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
        body = [0xFC, 0x12, 0x8E, meta, tx, .. new byte[9], .. salt, .. hash, 0x00];
    }

    byte[] payload = [(byte)(FastInitUuid & 0xff), (byte)(FastInitUuid >> 8), .. body];
    var adv = new BluetoothLEAdvertisement();
    adv.DataSections.Add(new BluetoothLEAdvertisementDataSection(ServiceData16, payload.AsBuffer()));
    var pub = new BluetoothLEAdvertisementPublisher(adv) { UseExtendedAdvertisement = extended };
    pub.StatusChanged += (_, e) => Console.WriteLine($"publisher status={e.Status} error={e.Error}");
    try { pub.Start(); }
    catch (Exception ex) { Console.WriteLine($"Start threw {ex.GetType().Name}: 0x{ex.HResult:X8}"); return 2; }
    Console.WriteLine($"publisher started, status={pub.Status}, {(extended ? "extended" : "legacy")}, bodyLen={body.Length}, meta=0x{(body.Length > 3 ? body[3] : 0):X2}, " +
                      $"{(Has("--static") ? "static" : Has("--raw") ? "raw" : $"type={(silent ? "silent" : "notify")} cert={cert} hash={hashMode}")}");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    Console.WriteLine($"status before stop={pub.Status}");
    pub.Stop();
    await Task.Delay(500);
    Console.WriteLine($"status after stop={pub.Status}");
    return 0;
}

Console.WriteLine("unknown command");
return 64;
