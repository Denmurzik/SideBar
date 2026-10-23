using System.Net;
using System.Net.NetworkInformation;
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
    private readonly IPAddress _address;
    private readonly uint _interfaceIndex;

    private nint _instance;
    private nint _request;
    private nint _contextHandle;
    private nint _cancel;   // DNS_SERVICE_CANCEL of the pending DnsServiceRegister; lives as long as the request
    private nint[] _strings = [];

    /// <param name="address">LAN address to announce, from <see cref="NetworkInfo.DefaultRouteInterface"/>.</param>
    /// <param name="interfaceIndex">Index of the interface owning <paramref name="address"/>; the service is registered only there.</param>
    public QuickShareAdvertiser(string endpointId, byte[] endpointInfo, int port, IPAddress address, int interfaceIndex)
    {
        _endpointId = endpointId;
        _endpointInfo = endpointInfo;
        _port = port;
        _address = address;
        _interfaceIndex = (uint)interfaceIndex;
    }

    public string InstanceName => MdnsName.Encode(_endpointId);
    public IPAddress Address => _address;

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
            {
                // the callback has run, nothing is pending any more
                Cleanup();
                throw new InvalidOperationException($"mDNS registration failed: {result}");
            }
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // Still pending: cancel it. DNS-SD may call back later with the request, instance and context, so
            // none of them is freed here; they are abandoned on purpose (a few hundred bytes) and logged.
            AbandonPending();
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
        _address.TryWriteBytes(new Span<byte>(&ip4, 4), out _);
        uint* ip4Ptr = &ip4;

        var instance = Native.DnsServiceConstructInstance(serviceName, hostName, ip4Ptr, 0, (ushort)_port, 0, 0, 1, keys, values);
        if (instance is null) throw new InvalidOperationException("DnsServiceConstructInstance failed.");
        _instance = (nint)instance;

        var tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contextHandle = GCHandle.ToIntPtr(GCHandle.Alloc(tcs));
        _cancel = (nint)NativeMemory.AllocZeroed((nuint)sizeof(Native.ServiceCancel));
        var request = (Native.RegisterRequest*)NativeMemory.AllocZeroed((nuint)sizeof(Native.RegisterRequest));
        _request = (nint)request;
        request->Version = Native.RequestVersion1;
        request->InterfaceIndex = _interfaceIndex;
        request->ServiceInstance = _instance;
        request->Callback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnRegistered;
        request->Context = _contextHandle;

        status = Native.DnsServiceRegister(request, (Native.ServiceCancel*)_cancel);
        return tcs.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnRegistered(uint status, nint context, nint instance)
    {
        try { ((TaskCompletionSource<uint>)GCHandle.FromIntPtr(context).Target!).TrySetResult(status); }
        catch (Exception) { }
    }

    /// <summary>Cancels a pending register and leaves its native state to the OS: it is never freed while a callback can still arrive.</summary>
    private unsafe void AbandonPending()
    {
        uint cancelStatus = 0;
        if (_cancel != 0)
        {
            try { cancelStatus = Native.DnsServiceRegisterCancel((Native.ServiceCancel*)_cancel); }
            catch (Exception) { cancelStatus = uint.MaxValue; }
        }
        NearbyLog.Write(new NearbyLogEvent("advertise.register.abandoned", null, $"cancel={cancelStatus}"));
        // the memory stays allocated (deliberate leak); only our references are dropped
        _instance = 0;
        _request = 0;
        _contextHandle = 0;
        _cancel = 0;
        _strings = [];
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
            if (status == Native.StatusRequestPending && !done.Task.Wait(TimeSpan.FromSeconds(2)))
            {
                // the callback may still arrive and reads the context handle: leave request, instance and handle alone
                NearbyLog.Write(new NearbyLogEvent("advertise.deregister.abandoned"));
                _instance = 0;
                _request = 0;
                _cancel = 0;
                _strings = [];
                if (_contextHandle != 0) { GCHandle.FromIntPtr(_contextHandle).Free(); _contextHandle = 0; }
                return;
            }
            handle.Free();
        }
        Cleanup();
    }

    private unsafe void Cleanup()
    {
        if (_instance != 0) { Native.DnsServiceFreeInstance((Native.ServiceInstance*)_instance); _instance = 0; }
        if (_request != 0) { NativeMemory.Free((void*)_request); _request = 0; }
        if (_cancel != 0) { NativeMemory.Free((void*)_cancel); _cancel = 0; }
        if (_contextHandle != 0) { GCHandle.FromIntPtr(_contextHandle).Free(); _contextHandle = 0; }
        foreach (var s in _strings) Marshal.FreeHGlobal(s);
        _strings = [];
    }

    public void Dispose() => Unregister();

    /// <summary>True when the address belongs to one of this machine's adapters (our own announcements).</summary>
    public static bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(address)));
}
