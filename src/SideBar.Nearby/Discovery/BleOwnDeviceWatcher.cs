// BLE-presence своих устройств: реклама Nearby Connections (секция Service Data 0x16, UUID 0xFEF3) опознаётся той же
// CertificateIdentity, что и mDNS-путь. Нужна для заблокированного телефона: он гасит mDNS, но продолжает слать эту
// рекламу (docs/research/ble-locked-phone.md §2.2, перебор смещений доказан в E2 на реальном Pixel 9).
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;
using SideBar.Nearby.Identity;
using SideBar.Nearby.Wire;

namespace SideBar.Nearby.Discovery;

/// <summary>Своё устройство, увиденное по BLE-рекламе, а не по mDNS.</summary>
public sealed record BlePresence(string Key, string? DeviceName);

/// <summary>
/// Слушает BLE-рекламу Nearby Connections и поднимает <see cref="Found"/>/<see cref="Lost"/> по устройствам,
/// опознанным через <paramref name="identity"/> (читается заново при каждой рекламе, чтобы переживать перезагрузку
/// сертификатов). <see cref="Lost"/> поднимается, если устройство не прислало рекламу 15 секунд — рекламный интервал
/// по E2 даёт кадр намного чаще (первый кадр на 3,1 с, виден многократно за 60 с).
/// </summary>
public sealed class BleOwnDeviceWatcher(Func<CertificateIdentity?> identity) : IDisposable
{
    /// <summary>UUID секции Service Data Nearby Connections (Copresence), как в spikes/ble.</summary>
    public const ushort NearbyConnectionsUuid = 0xFEF3;
    private const byte ServiceData16 = 0x16;
    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, Timer> _presence = new(StringComparer.Ordinal);
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    public event Action<BlePresence>? Found;
    public event Action<string>? Lost;

    public void Start()
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AllowExtendedAdvertisements = true;
        watcher.Received += OnReceived;
        lock (_gate)
        {
            if (_disposed) return;
            _watcher = watcher;
        }
        watcher.Start();
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (identity() is not { } id) return;
        foreach (var section in args.Advertisement.DataSections)
        {
            if (section.DataType != ServiceData16) continue;
            if (TryParseAdvertisement(id, section.Data.ToArray(), out var key, out var name)) Touch(key, name);
        }
    }

    /// <summary>
    /// Тело секции 0x16: UUID (2 байта, little-endian) + полезная нагрузка. Раскладка обёртки вокруг 16-байтового
    /// endpoint info (salt(2) + зашифрованный ключ метаданных(14)) на Android не документирована
    /// (ble-locked-phone.md §2.2), поэтому, как и E2, перебираются все смещения, с которых может начинаться этот
    /// 16-байтовый кусок (один ведущий байт обёртки перед ним на каждом смещении).
    /// </summary>
    internal static bool TryParseAdvertisement(CertificateIdentity identity, byte[] serviceData, out string key, out string? deviceName)
    {
        key = "";
        deviceName = null;
        if (serviceData.Length < 2) return false;
        if ((ushort)(serviceData[0] | (serviceData[1] << 8)) != NearbyConnectionsUuid) return false;

        var body = serviceData.AsSpan(2);
        for (var o = 0; o + 1 + EndpointInfo.MetadataLength <= body.Length; o++)
        {
            var candidate = new EndpointInfo { Metadata = body.Slice(o + 1, EndpointInfo.MetadataLength).ToArray() };
            if (!identity.Identify(candidate)) continue;
            if (identity.TryGetPeerKey(candidate) is not { } foundKey) continue;
            key = foundKey;
            deviceName = identity.TryGetPeerName(candidate);
            return true;
        }
        return false;
    }

    private void Touch(string key, string? name)
    {
        bool isNew;
        lock (_gate)
        {
            if (_disposed) return;
            if (_presence.TryGetValue(key, out var existing)) { existing.Change(PresenceTimeout, Timeout.InfiniteTimeSpan); isNew = false; }
            else { _presence[key] = new Timer(_ => OnTimeout(key), null, PresenceTimeout, Timeout.InfiniteTimeSpan); isNew = true; }
        }
        if (isNew) Found?.Invoke(new BlePresence(key, name));
    }

    private void OnTimeout(string key)
    {
        lock (_gate) { if (!_presence.Remove(key, out var t)) return; t.Dispose(); }
        Lost?.Invoke(key);
    }

    public void Dispose()
    {
        BluetoothLEAdvertisementWatcher? watcher;
        List<Timer> timers;
        lock (_gate)
        {
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            timers = [.. _presence.Values];
            _presence.Clear();
        }
        if (watcher is not null)
        {
            watcher.Received -= OnReceived;
            try { watcher.Stop(); }
            catch (Exception e) { NearbyLog.Write(new NearbyLogEvent("ble.watch.stop-failed", Value: e.GetType().Name)); }
        }
        foreach (var t in timers) t.Dispose();
    }
}
