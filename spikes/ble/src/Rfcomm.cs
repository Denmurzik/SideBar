using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

// Bluetooth Classic RFCOMM client for the "rfcomm" command (docs/research/ble-locked-phone.md, E3).
// Connects to the Nearby Sharing service of a device identified only by its Bluetooth MAC (from a certificate,
// §2.3-§2.4), no pairing. Never prints the MAC itself, same redaction rule as the rest of this spike.
static class RfcommClient
{
    // UUID.nameUUIDFromBytes("NearbySharing") = MD5-based UUIDv3, computed in docs/research/ble-locked-phone.md §2.3.
    public static readonly Guid NearbySharingServiceId = Guid.Parse("a82efa21-ae5c-3dde-9bbc-f16da7b16c5a");

    public static async Task<(StreamSocket? Socket, string Status)> ConnectAsync(byte[] mac6)
    {
        if (mac6.Length != 6) return (null, "bad MAC length");
        ulong address = 0;
        foreach (var b in mac6) address = (address << 8) | b;

        BluetoothDevice? device;
        try { device = await BluetoothDevice.FromBluetoothAddressAsync(address); }
        catch (Exception ex) { return (null, $"FromBluetoothAddressAsync threw {ex.GetType().Name}: 0x{ex.HResult:X8}"); }
        if (device is null) return (null, "device not found (FromBluetoothAddressAsync returned null)");

        RfcommDeviceServicesResult servicesResult;
        try
        {
            servicesResult = await device.GetRfcommServicesForIdAsync(
                RfcommServiceId.FromUuid(NearbySharingServiceId), BluetoothCacheMode.Uncached);
        }
        catch (Exception ex) { return (null, $"GetRfcommServicesForIdAsync threw {ex.GetType().Name}: 0x{ex.HResult:X8}"); }
        if (servicesResult.Error != BluetoothError.Success) return (null, $"GetRfcommServicesForIdAsync error={servicesResult.Error}");
        if (servicesResult.Services.Count == 0) return (null, "service not found (0 services advertised)");
        var service = servicesResult.Services[0];

        var socket = new StreamSocket();
        try { await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.PlainSocket); }
        catch (Exception ex)
        {
            socket.Dispose();
            return (null, $"StreamSocket.ConnectAsync threw {ex.GetType().Name}: 0x{ex.HResult:X8}");
        }
        return (socket, "connected");
    }
}

/// <summary>Combines a StreamSocket's one-way WinRT streams into one .NET Stream for NearbyConnection.</summary>
sealed class DuplexSocketStream : Stream
{
    private readonly StreamSocket _socket;
    private readonly Stream _in;
    private readonly Stream _out;

    public DuplexSocketStream(StreamSocket socket)
    {
        _socket = socket;
        _in = socket.InputStream.AsStreamForRead();
        _out = socket.OutputStream.AsStreamForWrite();
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _out.Flush();
    public override Task FlushAsync(CancellationToken ct) => _out.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _in.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _in.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _out.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _out.WriteAsync(buffer, ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _in.Dispose(); _out.Dispose(); _socket.Dispose(); }
        base.Dispose(disposing);
    }
}
