// Bluetooth Classic RFCOMM transport, ported from spikes/ble/src/Rfcomm.cs (hand-verified on a locked Pixel 9, no
// pairing). Connects to the Nearby Sharing service of a device identified only by its Bluetooth MAC (from a
// certificate, see Identity/CertificateIdentity.cs). Never logs the MAC (docs/research/ble-locked-phone.md).
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace SideBar.Nearby.Transport;

/// <summary>Bluetooth Classic RFCOMM client for the Nearby Sharing service, no pairing required.</summary>
public static class RfcommClient
{
    // UUID.nameUUIDFromBytes("NearbySharing") = MD5-based UUIDv3 (docs/research/ble-locked-phone.md §2.3).
    public static readonly Guid NearbySharingServiceId = Guid.Parse("a82efa21-ae5c-3dde-9bbc-f16da7b16c5a");

    /// <summary>
    /// Connects over RFCOMM to the Nearby Sharing service of the device at <paramref name="mac6"/> (big-endian: the
    /// most significant byte first). Throws <see cref="IOException"/> on any lookup or connection failure.
    /// </summary>
    public static async Task<Stream> ConnectAsync(byte[] mac6, CancellationToken ct)
    {
        ulong address = 0;
        foreach (var b in mac6) address = (address << 8) | b;

        BluetoothDevice? device;
        try { device = await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException("Bluetooth device lookup failed", ex);
        }
        if (device is null) throw new IOException("Bluetooth device not found");

        RfcommDeviceServicesResult servicesResult;
        try
        {
            servicesResult = await device.GetRfcommServicesForIdAsync(
                RfcommServiceId.FromUuid(NearbySharingServiceId), BluetoothCacheMode.Uncached).AsTask(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException("Bluetooth RFCOMM service lookup failed", ex);
        }
        if (servicesResult.Error != BluetoothError.Success || servicesResult.Services.Count == 0)
            throw new IOException("Bluetooth Nearby Sharing service not available");
        var service = servicesResult.Services[0];

        var socket = new StreamSocket();
        try
        {
            await socket.ConnectAsync(
                service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.PlainSocket).AsTask(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            throw new IOException("Bluetooth RFCOMM connect failed", ex);
        }
        return new DuplexSocketStream(socket);
    }

    /// <summary>Combines a StreamSocket's one-way WinRT streams into one <see cref="Stream"/> for NearbyConnection.</summary>
    private sealed class DuplexSocketStream : Stream
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
}
