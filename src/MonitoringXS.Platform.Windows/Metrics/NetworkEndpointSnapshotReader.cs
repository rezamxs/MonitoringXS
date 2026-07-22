using System.Runtime.InteropServices;

namespace MonitoringXS.Platform.Windows.Metrics;

internal static class NetworkEndpointSnapshotReader
{
    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private const int TcpOwnerPidConnections = 4;
    private const int UdpOwnerPid = 1;

    public static EndpointSnapshot Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        Dictionary<int, int> tcp = [];
        Dictionary<int, int> udp = [];
        bool tcpAvailable = TryReadTable(
            GetExtendedTcpTable,
            TcpOwnerPidConnections,
            AddressFamilyInet,
            24,
            20,
            tcp)
            && TryReadTable(
                GetExtendedTcpTable,
                TcpOwnerPidConnections,
                AddressFamilyInet6,
                56,
                52,
                tcp);
        bool udpAvailable = TryReadTable(
            GetExtendedUdpTable,
            UdpOwnerPid,
            AddressFamilyInet,
            12,
            8,
            udp)
            && TryReadTable(
                GetExtendedUdpTable,
                UdpOwnerPid,
                AddressFamilyInet6,
                28,
                24,
                udp);

        return new EndpointSnapshot(tcpAvailable ? tcp : null, udpAvailable ? udp : null);
    }

    private static bool TryReadTable(
        TableReader reader,
        int tableClass,
        int addressFamily,
        int rowSize,
        int processIdOffset,
        Dictionary<int, int> counts)
    {
        int bufferLength = 0;
        uint firstResult = reader(0, ref bufferLength, false, addressFamily, tableClass, 0);
        if (firstResult != ErrorInsufficientBuffer || bufferLength < sizeof(int) || bufferLength > MaximumTableBytes)
        {
            return false;
        }

        nint buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            uint result = reader(buffer, ref bufferLength, false, addressFamily, tableClass, 0);
            if (result != 0 || bufferLength < sizeof(int))
            {
                return false;
            }

            int rowCount = Marshal.ReadInt32(buffer);
            if (rowCount < 0 || (long)sizeof(int) + ((long)rowCount * rowSize) > bufferLength)
            {
                return false;
            }

            for (int index = 0; index < rowCount; index++)
            {
                int processId = Marshal.ReadInt32(buffer, sizeof(int) + (index * rowSize) + processIdOffset);
                if (processId <= 0)
                {
                    continue;
                }

                counts.TryGetValue(processId, out int count);
                counts[processId] = count == int.MaxValue ? count : count + 1;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal readonly record struct EndpointSnapshot(
        IReadOnlyDictionary<int, int>? TcpConnections,
        IReadOnlyDictionary<int, int>? UdpEndpoints);

    private delegate uint TableReader(
        nint table,
        ref int bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint table,
        ref int bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        nint table,
        ref int bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
