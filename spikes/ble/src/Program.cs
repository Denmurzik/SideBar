using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

// Usage: Ble info | Ble watch [seconds] | Ble publish [seconds] [notify|silent]
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
    bool silent = args.Length > 2 && args[2] == "silent";
    // FastInit V1: model id FC128E, [version(3)|type(3)|uwb(1)|cert(1)], adjusted tx power. Hypothesis, see docs/research/ble.md.
    byte meta = (byte)((0 << 5) | ((silent ? 1 : 0) << 2));
    byte[] payload = [(byte)(FastInitUuid & 0xff), (byte)(FastInitUuid >> 8), 0xFC, 0x12, 0x8E, meta, 0x00];
    var adv = new BluetoothLEAdvertisement();
    adv.DataSections.Add(new BluetoothLEAdvertisementDataSection(ServiceData16, payload.AsBuffer()));
    var pub = new BluetoothLEAdvertisementPublisher(adv) { UseExtendedAdvertisement = false };
    pub.StatusChanged += (_, e) => Console.WriteLine($"publisher status={e.Status} error={e.Error}");
    try { pub.Start(); }
    catch (Exception ex) { Console.WriteLine($"Start threw {ex.GetType().Name}: 0x{ex.HResult:X8}"); return 2; }
    Console.WriteLine($"publisher started, status={pub.Status}, type={(silent ? "silent" : "notify")}");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    Console.WriteLine($"status before stop={pub.Status}");
    pub.Stop();
    await Task.Delay(500);
    Console.WriteLine($"status after stop={pub.Status}");
    return 0;
}

Console.WriteLine("unknown command");
return 64;
