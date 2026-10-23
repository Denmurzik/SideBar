using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SideBar.Nearby.Wire;
using Native = SideBar.Nearby.Discovery.DnsSdNative;

namespace SideBar.Nearby.Discovery;

public sealed record DiscoveredEndpoint(
    string InstanceName,
    string EndpointId,
    string DisplayName,
    DeviceKind Kind,
    IPAddress Address,
    int Port,
    EndpointInfo Info);

internal sealed record ResolvedService(string InstanceName, string Host, IReadOnlyList<IPAddress> Addresses, int Port,
    IReadOnlyDictionary<string, string> Txt);

/// <summary>Browses for Quick Share services on the local network through the Windows DNS-SD API.</summary>
public sealed class QuickShareBrowser : IDisposable
{
    private readonly Channel<(string Name, bool Alive)> _events = Channel.CreateUnbounded<(string, bool)>();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    // Request, query string and cancel handle stay allocated until Dispose: the API may keep referring to them.
    private nint _cancel;
    private nint _request;
    private nint _query;
    private nint _contextHandle;
    private Task? _pump;

    public event Action<DiscoveredEndpoint>? Found;
    public event Action<string>? Lost;

    public void Start()
    {
        if (_pump is not null) throw new InvalidOperationException("Already started.");
        var status = BeginBrowse();
        if (status != Native.StatusRequestPending && status != Native.StatusSuccess)
            throw new InvalidOperationException($"DnsServiceBrowse failed: {status}");
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    private unsafe uint BeginBrowse()
    {
        _contextHandle = GCHandle.ToIntPtr(GCHandle.Alloc(_events));
        _query = Marshal.StringToHGlobalUni(MdnsName.ServiceTypeLocal);
        _cancel = (nint)NativeMemory.AllocZeroed((nuint)sizeof(Native.ServiceCancel));
        var request = (Native.QueryRequest*)NativeMemory.AllocZeroed((nuint)sizeof(Native.QueryRequest));
        _request = (nint)request;
        request->Version = Native.RequestVersion1;
        request->QueryName = _query;
        request->Callback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, nint, void>)&OnBrowse;
        request->Context = _contextHandle;
        return Native.DnsServiceBrowse(request, (Native.ServiceCancel*)_cancel);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe void OnBrowse(uint status, nint context, nint records)
    {
        try
        {
            if (records == 0) return;
            var channel = (Channel<(string, bool)>)GCHandle.FromIntPtr(context).Target!;
            for (var rec = records; rec != 0; rec = ((Native.RecordHeader*)rec)->Next)
            {
                var header = (Native.RecordHeader*)rec;
                if (header->Type != Native.TypePtr) continue;
                var name = Marshal.PtrToStringUni(*(nint*)(rec + Native.DataOffset));
                if (name is not null) channel.Writer.TryWrite((name, header->Ttl != 0));
            }
        }
        catch (Exception) { }
        finally
        {
            if (records != 0) Native.DnsRecordListFree(records, Native.FreeRecordList);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (name, alive) in _events.Reader.ReadAllAsync(ct))
            {
                if (!alive)
                {
                    if (Forget(name)) Lost?.Invoke(name);
                    continue;
                }
                bool isNew;
                lock (_seen) isNew = _seen.Add(name);
                if (!isNew) continue;
                _ = ResolveAndRaiseAsync(name, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool Forget(string name)
    {
        lock (_seen) return _seen.Remove(name);
    }

    private async Task ResolveAndRaiseAsync(string fullName, CancellationToken ct)
    {
        try
        {
            var service = await DnsSdResolver.ResolveAsync(fullName, TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            if (service is null) { Forget(fullName); return; }
            var endpoint = DiscoveryParsing.ToEndpoint(service);
            if (endpoint is not null) Found?.Invoke(endpoint);
        }
        catch (OperationCanceledException) { }
    }

    public unsafe void Dispose()
    {
        _cts.Cancel();
        if (_cancel != 0)
        {
            Native.DnsServiceBrowseCancel((Native.ServiceCancel*)_cancel);
            NativeMemory.Free((void*)_cancel);
            _cancel = 0;
        }
        if (_request != 0) { NativeMemory.Free((void*)_request); _request = 0; }
        if (_query != 0) { Marshal.FreeHGlobal(_query); _query = 0; }
        if (_contextHandle != 0)
        {
            GCHandle.FromIntPtr(_contextHandle).Free();
            _contextHandle = 0;
        }
        _cts.Dispose();
    }
}

internal static class DiscoveryParsing
{
    public static DiscoveredEndpoint? ToEndpoint(ResolvedService service)
    {
        if (!MdnsName.TryGetInstanceLabel(service.InstanceName, out var label)) return null;
        if (!MdnsName.TryDecode(label, out var endpointId)) return null;
        if (service.Addresses.Count == 0) return null;
        if (!service.Txt.TryGetValue(MdnsName.TxtKeyEndpointInfo, out var n)) return null;
        if (!EndpointInfo.TryFromBase64Url(n, out var info) || info is null) return null;

        return new DiscoveredEndpoint(
            service.InstanceName, endpointId, info.Name ?? "(hidden)", info.Kind,
            service.Addresses[0], service.Port, info);
    }
}

internal static class DnsSdResolver
{
    private sealed class State
    {
        public readonly TaskCompletionSource<ResolvedService?> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public nint Cancel;
        public nint Request;
        public nint Query;
        public GCHandle Handle;
    }

    public static async Task<ResolvedService?> ResolveAsync(string fullName, TimeSpan timeout, CancellationToken ct)
    {
        var state = Begin(fullName, out var started);
        if (!started) return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var _ = timeoutCts.Token.Register(() => CancelResolve(state));
            return await state.Tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // The callback runs exactly once (also after a cancel) and has completed the TCS by now.
            Release(state);
        }
    }

    private static unsafe State Begin(string fullName, out bool started)
    {
        var state = new State();
        state.Handle = GCHandle.Alloc(state);
        state.Query = Marshal.StringToHGlobalUni(fullName);
        state.Cancel = (nint)NativeMemory.AllocZeroed((nuint)sizeof(Native.ServiceCancel));
        var request = (Native.QueryRequest*)NativeMemory.AllocZeroed((nuint)sizeof(Native.QueryRequest));
        state.Request = (nint)request;
        request->Version = Native.RequestVersion1;
        request->QueryName = state.Query;
        request->Callback = (nint)(delegate* unmanaged[Stdcall]<uint, nint, Native.ServiceInstance*, void>)&OnResolved;
        request->Context = GCHandle.ToIntPtr(state.Handle);

        started = Native.DnsServiceResolve(request, (Native.ServiceCancel*)state.Cancel) == Native.StatusRequestPending;
        if (!started) Release(state);
        return state;
    }

    private static unsafe void CancelResolve(State state) =>
        Native.DnsServiceResolveCancel((Native.ServiceCancel*)state.Cancel);

    private static unsafe void Release(State state)
    {
        if (state.Cancel != 0) { NativeMemory.Free((void*)state.Cancel); state.Cancel = 0; }
        if (state.Request != 0) { NativeMemory.Free((void*)state.Request); state.Request = 0; }
        if (state.Query != 0) { Marshal.FreeHGlobal(state.Query); state.Query = 0; }
        if (state.Handle.IsAllocated) state.Handle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe void OnResolved(uint status, nint context, Native.ServiceInstance* instance)
    {
        var state = (State)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (status != Native.StatusSuccess || instance is null)
            {
                state.Tcs.TrySetResult(null);
                return;
            }

            var addresses = new List<IPAddress>();
            if (instance->Ip4Address != 0)
            {
                var bytes = new byte[4];
                Marshal.Copy(instance->Ip4Address, bytes, 0, 4);
                addresses.Add(new IPAddress(bytes));
            }

            var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < instance->PropertyCount; i++)
            {
                var key = Marshal.PtrToStringUni(((nint*)instance->Keys)[i]);
                var valuePtr = instance->Values == 0 ? 0 : ((nint*)instance->Values)[i];
                if (key is not null) txt[key] = valuePtr == 0 ? string.Empty : Marshal.PtrToStringUni(valuePtr) ?? string.Empty;
            }

            state.Tcs.TrySetResult(new ResolvedService(
                Marshal.PtrToStringUni(instance->InstanceName) ?? string.Empty,
                Marshal.PtrToStringUni(instance->HostName) ?? string.Empty,
                addresses, instance->Port, txt));
        }
        catch (Exception) { state.Tcs.TrySetResult(null); }
        finally
        {
            if (instance is not null) Native.DnsServiceFreeInstance(instance);
        }
    }
}
