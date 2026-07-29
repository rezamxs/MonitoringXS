using System.Runtime.InteropServices;

namespace MonitoringXS.Platform.Windows.Broker;

internal enum BrokerServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Unknown
}

internal static class BrokerServiceProbe
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ServiceRunning = 4;
    private const int ErrorServiceDoesNotExist = 1060;

    public static string ConnectionFailureDetail() =>
        QueryState() switch
        {
            BrokerServiceState.NotInstalled => "Broker service not installed.",
            BrokerServiceState.Stopped => "Broker service stopped.",
            _ => "Broker connection failed."
        };

    private static BrokerServiceState QueryState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return BrokerServiceState.Unknown;
        }

        nint manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
        {
            return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                ? BrokerServiceState.NotInstalled
                : BrokerServiceState.Unknown;
        }

        try
        {
            nint service = OpenService(manager, PrivilegedEtwBrokerServer.ServiceName, ServiceQueryStatus);
            if (service == 0)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? BrokerServiceState.NotInstalled
                    : BrokerServiceState.Unknown;
            }

            try
            {
                if (!QueryServiceStatusEx(
                    service,
                    0,
                    out ServiceStatusProcess status,
                    Marshal.SizeOf<ServiceStatusProcess>()))
                {
                    return BrokerServiceState.Unknown;
                }

                return status.CurrentState == ServiceRunning
                    ? BrokerServiceState.Running
                    : BrokerServiceState.Stopped;
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        nint service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize);

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
}
