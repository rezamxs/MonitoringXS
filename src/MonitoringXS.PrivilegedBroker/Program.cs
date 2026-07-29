using System.Runtime.InteropServices;
using System.Security.Principal;
using MonitoringXS.Platform.Windows.Broker;

namespace MonitoringXS.PrivilegedBroker;

internal static class Program
{
    internal const string RequiredServiceAccount = "LocalSystem";
    private const int AccessDenied = 5;
    private const int UnsupportedRequest = 64;

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UnsupportedRequest;
        }

        if (TryReadServiceEndpoint(args, out BrokerPipeEndpoint? endpoint, out bool diagnostics))
        {
            if (!IsLocalSystem())
            {
                return AccessDenied;
            }

            Action<string>? diagnostic = diagnostics
                ? BrokerStartupDiagnostics.Write
                : null;
            diagnostic?.Invoke($"identity-resolved userSid={endpoint!.UserSid} session={endpoint.SessionId} pipe={endpoint.PipeName}");
            return WindowsServiceHost.Run(token => RunBrokerAsync(endpoint!, token, diagnostic));
        }

        if (args is ["--console"])
        {
            using CancellationTokenSource shutdown = new();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            RunBrokerAsync(BrokerPipeEndpoint.ForCurrentProcess(), shutdown.Token)
                .GetAwaiter()
                .GetResult();
            return 0;
        }

        return UnsupportedRequest;
    }

    private static bool IsLocalSystem()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
    }

    internal static bool TryReadServiceEndpoint(
        IReadOnlyList<string> args,
        out BrokerPipeEndpoint? endpoint)
        => TryReadServiceEndpoint(args, out endpoint, out _);

    internal static bool TryReadServiceEndpoint(
        IReadOnlyList<string> args,
        out BrokerPipeEndpoint? endpoint,
        out bool diagnostics)
    {
        endpoint = null;
        diagnostics = args.Count > 0 && args[^1] == "--diagnostics";
        int endpointArgumentCount = diagnostics ? args.Count - 1 : args.Count;
        if ((endpointArgumentCount is not 4 and not 6)
            || args[0] != "--user-sid"
            || args[2] != "--session"
            || !int.TryParse(args[3], out int sessionId)
            || (endpointArgumentCount == 6 && args[4] != "--logon-sid"))
        {
            return false;
        }

        try
        {
            endpoint = BrokerPipeEndpoint.Create(
                args[1],
                sessionId,
                endpointArgumentCount == 6 ? args[5] : null);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task RunBrokerAsync(
        BrokerPipeEndpoint endpoint,
        CancellationToken cancellationToken,
        Action<string>? diagnostic = null)
    {
        diagnostic?.Invoke("service-started");
        await using PrivilegedEtwBrokerServer server = new(endpoint, diagnostic);
        diagnostic?.Invoke("broker-server-created");
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static class BrokerStartupDiagnostics
{
    private static readonly object Gate = new();
    private static string? _path = Path.Combine(
        AppContext.BaseDirectory,
        "startup.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    _path!,
                    $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never change broker availability or security behavior.
        }
    }
}

internal static class WindowsServiceHost
{
    public const string ServiceName = "MonitoringXS.PrivilegedEtwBroker";
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;
    private static readonly ServiceMainCallback ServiceMainDelegate = ServiceMain;
    private static readonly ServiceControlHandler ControlHandlerDelegate = ControlHandler;
    private static readonly CancellationTokenSource Shutdown = new();
    private static Func<CancellationToken, Task>? _run;
    private static nint _statusHandle;
    private static ServiceStatus _status;

    public static int Run(Func<CancellationToken, Task> run)
    {
        _run = run;
        ServiceTableEntry[] table =
        [
            new() { ServiceName = ServiceName, ServiceMain = ServiceMainDelegate },
            new()
        ];
        return StartServiceCtrlDispatcher(table) ? 0 : Marshal.GetLastWin32Error();
    }

    private static void ServiceMain(uint argumentCount, nint arguments)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, ControlHandlerDelegate, 0);
        if (_statusHandle == 0)
        {
            return;
        }

        _status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = ServiceStartPending,
            WaitHint = 5_000
        };
        SetServiceStatus(_statusHandle, ref _status);
        _status.CurrentState = ServiceRunning;
        _status.ControlsAccepted = ServiceAcceptStop | ServiceAcceptShutdown;
        _status.WaitHint = 0;
        SetServiceStatus(_statusHandle, ref _status);

        uint exitCode = 0;
        try
        {
            _run!(Shutdown.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            exitCode = 1;
        }
        finally
        {
            _status.CurrentState = ServiceStopped;
            _status.ControlsAccepted = 0;
            _status.Win32ExitCode = exitCode;
            SetServiceStatus(_statusHandle, ref _status);
        }
    }

    private static uint ControlHandler(
        uint control,
        uint eventType,
        nint eventData,
        nint context)
    {
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            _status.CurrentState = ServiceStopPending;
            _status.ControlsAccepted = 0;
            _status.WaitHint = 5_000;
            SetServiceStatus(_statusHandle, ref _status);
            Shutdown.Cancel();
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;

        public ServiceMainCallback? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private delegate void ServiceMainCallback(uint argumentCount, nint arguments);

    private delegate uint ServiceControlHandler(
        uint control,
        uint eventType,
        nint eventData,
        nint context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(
        [In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerEx(
        string serviceName,
        ServiceControlHandler handler,
        nint context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint statusHandle, ref ServiceStatus serviceStatus);
}
