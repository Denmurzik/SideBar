using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SideBar.Core.Nearby;

namespace SideBar.Nearby.Discovery;

/// <summary>Which local IPv4 interface Quick Share may use, and how Windows classifies its network.</summary>
public static partial class NetworkInfo
{
    /// <summary>One adapter reduced to what the selection needs; Index/Address are null when it has no IPv4.</summary>
    internal sealed record AdapterInfo(string Id, OperationalStatus Status, NetworkInterfaceType Type, int? Index, IPAddress? Address, bool HasGateway);

    /// <summary>Adapters are described lazily so that one throwing adapter (Teredo, ISATAP, VPN) is skipped, not fatal.</summary>
    internal static IEnumerable<Func<AdapterInfo>> SystemAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces().Select<NetworkInterface, Func<AdapterInfo>>(nic => () =>
        {
            if (!nic.Supports(NetworkInterfaceComponent.IPv4))
                return new AdapterInfo(nic.Id, nic.OperationalStatus, nic.NetworkInterfaceType, null, null, false);
            var props = nic.GetIPProperties();
            var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            return new AdapterInfo(nic.Id, nic.OperationalStatus, nic.NetworkInterfaceType, props.GetIPv4Properties().Index, v4, hasGateway);
        });

    /// <summary>IPv4 address and index of the interface Windows would use to reach the internet (best metric); null when none.
    /// Tunnels and virtual adapters without a route are never returned.</summary>
    public static (IPAddress Address, int InterfaceIndex)? DefaultRouteInterface() =>
        DefaultRouteInterface(SystemAdapters(), BestInterfaceIndex);

    internal static (IPAddress Address, int InterfaceIndex)? DefaultRouteInterface(IEnumerable<Func<AdapterInfo>> adapters, Func<int?> bestIndex)
    {
        var candidates = new List<AdapterInfo>();
        try
        {
            foreach (var describe in adapters)
            {
                try
                {
                    var a = describe();
                    if (a.Status == OperationalStatus.Up && a.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                        && a.Index is not null && a.Address is not null)
                        candidates.Add(a);
                }
                catch (Exception) { }
            }
        }
        catch (NetworkInformationException) { }

        int? best = null;
        try { best = bestIndex(); } catch (Exception) { }

        var pick = (best is { } i ? candidates.FirstOrDefault(c => c.Index == i) : null) ?? candidates.FirstOrDefault(c => c.HasGateway);
        return pick is null ? null : (pick.Address!, pick.Index!.Value);
    }

    private static unsafe int? BestInterfaceIndex()
    {
        // sockaddr_in to 8.8.8.8; only the routing table is consulted, nothing is sent.
        byte* sa = stackalloc byte[16];
        sa[0] = 2; sa[4] = 8; sa[5] = 8; sa[6] = 8; sa[7] = 8;
        return GetBestInterfaceEx(sa, out var index) == 0 ? (int)index : null;
    }

    /// <summary>Windows network category of the adapter; any failure (COM, no match) yields Unknown, i.e. do not listen.</summary>
    public static NetworkCategory CategoryOf(int interfaceIndex) => CategoryOf(interfaceIndex, SystemAdapters());

    internal static NetworkCategory CategoryOf(int interfaceIndex, IEnumerable<Func<AdapterInfo>> adapters)
    {
        try
        {
            var adapterId = AdapterGuid(interfaceIndex, adapters);
            return adapterId is null ? NetworkCategory.Unknown : QueryCategory(adapterId.Value);
        }
        catch (Exception)
        {
            return NetworkCategory.Unknown;
        }
    }

    internal static Guid? AdapterGuid(int interfaceIndex, IEnumerable<Func<AdapterInfo>> adapters)
    {
        foreach (var describe in adapters)
        {
            try
            {
                var a = describe();
                if (a.Index == interfaceIndex && Guid.TryParse(a.Id, out var id)) return id;
            }
            catch (Exception) { }
        }
        return null;
    }

    private static NetworkCategory QueryCategory(Guid adapterId)
    {
        // S_OK / S_FALSE must be balanced; RPC_E_CHANGED_MODE (thread is already STA) is fine, just do not uninitialize.
        var initialized = CoInitializeEx(0, 0) >= 0;
        try { return QueryCategoryCore(adapterId); }
        finally { if (initialized) CoUninitialize(); }
    }

    private static NetworkCategory QueryCategoryCore(Guid adapterId)
    {

        var clsid = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
        var iid = new Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B");
        if (CoCreateInstance(in clsid, 0, 1 /* CLSCTX_INPROC_SERVER */ | 4 /* LOCAL_SERVER */, in iid, out var ptr) < 0 || ptr == 0)
            return NetworkCategory.Unknown;

        var manager = (INetworkListManager)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.UniqueInstance);
        Marshal.Release(ptr);

        var connections = manager.GetNetworkConnections();
        while (true)
        {
            connections.Next(1, out var connection, out var fetched);
            if (fetched == 0 || connection is null) return NetworkCategory.Unknown;
            if (connection.GetAdapterId() != adapterId) continue;
            return connection.GetNetwork().GetCategory() switch
            {
                0 => NetworkCategory.Public,
                1 => NetworkCategory.Private,
                2 => NetworkCategory.Domain,
                _ => NetworkCategory.Unknown,
            };
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetBestInterfaceEx(byte* destination, out uint index);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    // IDispatch occupies vtable slots 3-6; the placeholders keep the layout right.
    [GeneratedComInterface, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface INetworkListManager
    {
        void DispatchSlot0(); void DispatchSlot1(); void DispatchSlot2(); void DispatchSlot3();
        IEnumNetworks GetNetworks(int flags);
        INetwork GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [GeneratedComInterface, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface IEnumNetworks { }

    [GeneratedComInterface, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface IEnumNetworkConnections
    {
        void DispatchSlot0(); void DispatchSlot1(); void DispatchSlot2(); void DispatchSlot3();
        void GetNewEnum(out nint enumerator);
        void Next(uint count, out INetworkConnection? connection, out uint fetched);
    }

    [GeneratedComInterface, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface INetworkConnection
    {
        void DispatchSlot0(); void DispatchSlot1(); void DispatchSlot2(); void DispatchSlot3();
        INetwork GetNetwork();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [GeneratedComInterface, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    internal partial interface INetwork
    {
        void DispatchSlot0(); void DispatchSlot1(); void DispatchSlot2(); void DispatchSlot3();
        nint GetName();
        void SetName(nint name);
        nint GetDescription();
        void SetDescription(nint description);
        Guid GetNetworkId();
        int GetDomainType();
        IEnumNetworkConnections GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint a, out uint b, out uint c, out uint d);
        [return: MarshalAs(UnmanagedType.Bool)] bool IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsConnected();
        int GetConnectivity();
        int GetCategory();
    }
}
