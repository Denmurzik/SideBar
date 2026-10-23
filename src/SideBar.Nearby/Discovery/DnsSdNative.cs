using System.Runtime.InteropServices;

namespace SideBar.Nearby.Discovery;

/// <summary>
/// P/Invoke surface of the Win32 DNS-SD API (dnsapi.dll, Windows 10 1809+): DnsServiceBrowse / DnsServiceResolve /
/// DnsServiceRegister. Structures follow windns.h for x64. Only blittable types, callbacks are UnmanagedCallersOnly,
/// so it is NativeAOT friendly.
/// </summary>
internal static unsafe partial class DnsSdNative
{
    public const uint RequestVersion1 = 1;
    public const uint StatusSuccess = 0;
    public const uint StatusRequestPending = 9506;
    public const ushort TypePtr = 12;
    public const int FreeRecordList = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceCancel
    {
        public nint Reserved;
    }

    /// <summary>Layout shared by DNS_SERVICE_BROWSE_REQUEST and DNS_SERVICE_RESOLVE_REQUEST.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueryRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint QueryName;
        public nint Callback;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint ServiceInstance;
        public nint Callback;
        public nint Context;
        public nint Credentials;
        public int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceInstance
    {
        public nint InstanceName;
        public nint HostName;
        public nint Ip4Address;
        public nint Ip6Address;
        public ushort Port;
        public ushort Priority;
        public ushort Weight;
        public uint PropertyCount;
        public nint Keys;
        public nint Values;
        public uint InterfaceIndex;
    }

    /// <summary>Header of DNS_RECORDW up to the data union; PTR data is a single PWSTR at <see cref="DataOffset"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RecordHeader
    {
        public nint Next;
        public nint Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
    }

    public static readonly int DataOffset = sizeof(RecordHeader);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceBrowse(QueryRequest* request, ServiceCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceBrowseCancel(ServiceCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceResolve(QueryRequest* request, ServiceCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceResolveCancel(ServiceCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceRegister(RegisterRequest* request, ServiceCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceRegisterCancel(ServiceCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceDeRegister(RegisterRequest* request, ServiceCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial ServiceInstance* DnsServiceConstructInstance(
        nint serviceName, nint hostName, uint* ip4, nint ip6, ushort port, ushort priority, ushort weight,
        uint propertyCount, nint* keys, nint* values);

    [LibraryImport("dnsapi.dll")]
    public static partial void DnsServiceFreeInstance(ServiceInstance* instance);

    [LibraryImport("dnsapi.dll")]
    public static partial void DnsRecordListFree(nint recordList, int freeType);
}
