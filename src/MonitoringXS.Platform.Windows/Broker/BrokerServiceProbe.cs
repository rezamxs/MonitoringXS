using System.Runtime.InteropServices;

namespace MonitoringXS.Platform.Windows.Broker;

public enum BrokerServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Unknown
}

public sealed record BrokerServiceSnapshot(
    BrokerServiceState State,
    bool? BinaryPresent,
    int? ProcessId = null);

public static class BrokerServiceProbe
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ServiceRunning = 4;
    private const int ErrorServiceDoesNotExist = 1060;

    public static string ConnectionFailureDetail() =>
        QuerySnapshot().State switch
        {
            BrokerServiceState.NotInstalled => "Broker service not installed.",
            BrokerServiceState.Stopped => "Broker service stopped.",
            _ => "Broker connection failed."
        };

    public static async ValueTask<BrokerServiceSnapshot> QueryAsync(
        CancellationToken cancellationToken) =>
        await Task.Run(QuerySnapshot, cancellationToken).ConfigureAwait(false);

    private static BrokerServiceSnapshot QuerySnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(BrokerServiceState.Unknown, null);
        }

        nint manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
        {
            return new(
                Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? BrokerServiceState.NotInstalled
                    : BrokerServiceState.Unknown,
                null);
        }

        try
        {
            nint service = OpenService(
                manager,
                PrivilegedEtwBrokerServer.ServiceName,
                ServiceQueryStatus | ServiceQueryConfig);
            if (service == 0)
            {
                return new(
                    Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                        ? BrokerServiceState.NotInstalled
                        : BrokerServiceState.Unknown,
                    null);
            }

            try
            {
                if (!QueryServiceStatusEx(
                    service,
                    0,
                    out ServiceStatusProcess status,
                    Marshal.SizeOf<ServiceStatusProcess>(),
                    out _))
                {
                    return new(BrokerServiceState.Unknown, null);
                }

                return new(
                    status.CurrentState == ServiceRunning
                        ? BrokerServiceState.Running
                        : BrokerServiceState.Stopped,
                    QueryBinaryPresent(service),
                    status.CurrentState == ServiceRunning && status.ProcessId > 0
                        ? status.ProcessId
                        : null);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static bool? QueryBinaryPresent(nint service)
    {
        QueryServiceConfig(service, 0, 0, out int requiredBytes);
        if (requiredBytes <= 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            if (!QueryServiceConfig(service, buffer, requiredBytes, out _))
            {
                return null;
            }

            QueryServiceConfigData config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            string? command = Marshal.PtrToStringUni(config.BinaryPathName);
            string? executable = ExecutablePath(command);
            return !string.IsNullOrWhiteSpace(executable) && File.Exists(executable);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded[0] == '"')
        {
            int closingQuote = expanded.IndexOf('"', 1);
            return closingQuote > 1 ? expanded[1..closingQuote] : null;
        }

        int separator = expanded.IndexOf(' ');
        return separator < 0 ? expanded : expanded[..separator];
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        nint service,
        nint queryServiceConfig,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        nint service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
        public int ProcessId;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        public int ServiceType;
        public int StartType;
        public int ErrorControl;
        public nint BinaryPathName;
        public nint LoadOrderGroup;
        public int TagId;
        public nint Dependencies;
        public nint ServiceStartName;
        public nint DisplayName;
    }
}
