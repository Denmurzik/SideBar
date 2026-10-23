using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SideBar.Nearby.Wire;
using Native = SideBar.Nearby.Discovery.DnsSdNative;

namespace SideBar.Nearby.Discovery;

/// <summary>Advertises the Quick Share Wi-Fi LAN service (EVERYONE mode) through DnsServiceRegister.</summary>
public sealed class QuickShareAdvertiser : IDisposable
{
    private readonly string _endpointId;
    private readonly byte[] _endpointInfo;
    private readonly int _port;
    private readonly IPAddress? _address;
    private readonly uint _interfaceIndex;

    private nint _instance;
    private nint _request;
    private nint _contextHandle;
    private nint[] _strings = [];

    /// <param name="address">LAN address to announce; by default the interface with a default gateway, so virtual
    /// adapters (Hyper-V, WSL, VPN) are neither announced nor used.</param>
    public QuickShareAdvertiser(string endpointId, byte[] endpointInfo, int port, IPAddress? address = null)
    {
        _endpointId = endpointId;
        _endpointInfo = endpointInfo;
        _port = port;
        var lan = address is null ? FindLanInterface() : FindInterfaceOf(address);
        _address = address ?? lan?.Address;
        _interfaceIndex = (uint)(lan?.Index ?? 0);
    }

    public string InstanceName => MdnsName.Encode(_endpointId);
    public IPAddress? Address => _address;

    public async Task RegisterAsync(CancellationToken ct = default)
    {
        if (_instance != 0) throw new InvalidOperationException("Already registered.");

        var completion = BeginRegister(out var status);
        if (status != Native.StatusRequestPending && status != Native.StatusSuccess)
        {
            Cleanup();
            throw new InvalidOperationException($"DnsServiceRegister failed: {status}");
        }

        try
        {
            var result = await completion.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (result != Native.StatusSuccess)
                throw new InvalidOperationException($"mDNS registration failed: {result}");
        }
        catch (Exception)
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>Android may miss a service that was registered before it started discovering; announce again.</summary>
    public async Task ReannounceAsync(CancellationToken ct = default)
    {
        Unregister();
        await RegisterAsync(ct).ConfigureAwait(false);
    }

    private unsafe Task<uint> BeginRegister(out uint status)
    {
        var instanceFull = $"{InstanceName}.{MdnsName.ServiceTypeLocal}";
        var host = Environment.MachineName + ".local";
        var txtValue = Base64Url.Encode(_endpointInfo);

        var serviceName = Marshal.StringToHGlobalUni(instanceFull);
        var hostName = Marshal.StringToHGlobalUni(host);
        var key = Marshal.StringToHGlobalUni(MdnsName.TxtKeyEndpointInfo);
        var value = Marshal.StringToHGlobalUni(txtValue);
        _strings = [serviceName, hostName, key, value];

        var keys = stackalloc nint[1] { key };
        var values = stackalloc nint[1] { value };
        uint ip4 = 0;
        uint* ip4Ptr = null;
        if (_address is not null)
        {
            _address.TryWriteBytes(new Span<byte>(&ip4, 4), out _);
            ip4Ptr = &ip4;
        }

        var instance = Native.DnsServiceConstructInstance(serviceName, hostName, ip4Ptr, 0, (ushort)_port, 0, 0, 1, keys, values);
        if (instance is null) throw new InvalidOperationException("DnsServiceConstructInstance failed.");
        _instance = (nint)instance;

        var tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contextHandle = GCHandle.ToIntPtr(GCHandle.Alloc(tcs));
        var request = (Native.RegisterRequest*)NativeMemory.AllocZeroed((nuint)sizeof(Native.RegisterRequest));
        _request = (nint)request;
        request->Version = Native.RequestVersion1;
        request->InterfaceIndex = _interfaceIndex;
        request->ServiceInstance = _instance;
        request->Callback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnRegistered;
        request->Context = _contextHandle;

        status = Native.DnsServiceRegister(request, null);
        return tcs.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnRegistered(uint status, nint context, nint instance)
    {
        try { ((TaskCompletionSource<uint>)GCHandle.FromIntPtr(context).Target!).TrySetResult(status); }
        catch (Exception) { }
    }

    private unsafe void Unregister()
    {
        if (_request != 0)
        {
            // Wait for the goodbye announcement so a stopped receiver does not linger in other devices' lists.
            var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = GCHandle.Alloc(done);
            var request = (Native.RegisterRequest*)_request;
            request->Context = GCHandle.ToIntPtr(handle);
            var status = Native.DnsServiceDeRegister(request, null);
            if (status == Native.StatusRequestPending) done.Task.Wait(TimeSpan.FromSeconds(2));
            handle.Free();
        }
        Cleanup();
    }

    private unsafe void Cleanup()
    {
        if (_instance != 0) { Native.DnsServiceFreeInstance((Native.ServiceInstance*)_instance); _instance = 0; }
        if (_request != 0) { NativeMemory.Free((void*)_request); _request = 0; }
        if (_contextHandle != 0) { GCHandle.FromIntPtr(_contextHandle).Free(); _contextHandle = 0; }
        foreach (var s in _strings) Marshal.FreeHGlobal(s);
        _strings = [];
    }

    public void Dispose() => Unregister();

    public sealed record LanInterface(IPAddress Address, int Index);

    /// <summary>IPv4 address and index of an operational, non-loopback adapter that has a default gateway.</summary>
    public static LanInterface? FindLanInterface()
    {
        LanInterface? fallback = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (v4 is null || props.GetIPv4Properties() is not { } v4Props) continue;
            var lan = new LanInterface(v4, v4Props.Index);
            if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                return lan;
            fallback ??= lan;
        }
        return fallback;
    }

    public static IPAddress? FindLanAddress() => FindLanInterface()?.Address;

    private static LanInterface? FindInterfaceOf(IPAddress address)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = nic.GetIPProperties();
            if (props.UnicastAddresses.Any(a => a.Address.Equals(address)) && props.GetIPv4Properties() is { } v4)
                return new LanInterface(address, v4.Index);
        }
        return null;
    }

    /// <summary>True when the address belongs to one of this machine's adapters (our own announcements).</summary>
    public static bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(address)));
}
