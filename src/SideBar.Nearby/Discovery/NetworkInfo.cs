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
    /// <summary>IPv4 address and index of an operational, non-loopback adapter that has a default gateway; null when none.
    /// Virtual adapters without a gateway (Hyper-V, WSL, VPN) are never returned.</summary>
    public static (IPAddress Address, int InterfaceIndex)? DefaultRouteInterface()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (v4 is null || props.GetIPv4Properties() is not { } v4Props) continue;
                if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                    return (v4, v4Props.Index);
            }
        }
        catch (NetworkInformationException) { }
        return null;
    }

    /// <summary>Windows network category of the adapter; any failure (COM, no match) yields Unknown, i.e. do not listen.</summary>
    public static NetworkCategory CategoryOf(int interfaceIndex)
    {
        try
        {
            var adapterId = AdapterGuid(interfaceIndex);
            return adapterId is null ? NetworkCategory.Unknown : QueryCategory(adapterId.Value);
        }
        catch (Exception)
        {
            return NetworkCategory.Unknown;
        }
    }

    private static Guid? AdapterGuid(int interfaceIndex)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var v4 = nic.GetIPProperties().GetIPv4Properties();
            if (v4 is not null && v4.Index == interfaceIndex && Guid.TryParse(nic.Id, out var id)) return id;
        }
        return null;
    }

    private static NetworkCategory QueryCategory(Guid adapterId)
    {
        _ = CoInitializeEx(0, 0); // COINIT_MULTITHREADED; the result is irrelevant (already initialized is fine)

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
