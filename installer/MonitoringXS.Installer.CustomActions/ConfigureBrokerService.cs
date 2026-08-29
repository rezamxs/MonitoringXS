using System;
using System.Runtime.InteropServices;
using WixToolset.Dtf.WindowsInstaller;

namespace MonitoringXS.Installer.CustomActions;

public static class ConfigureBrokerService
{
    private const string ServiceName = "MonitoringXS.PrivilegedEtwBroker";
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const int ServiceConfigServiceSidInfo = 5;
    private const uint ServiceSidTypeUnrestricted = 1;

    [CustomAction]
    public static ActionResult SetUnrestrictedSid(Session session)
    {
        IntPtr manager = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;
        IntPtr sidInfo = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, 0x0001);
            if (manager == IntPtr.Zero)
            {
                return Failure(session, "open service manager");
            }

            service = OpenService(
                manager,
                ServiceName,
                ServiceChangeConfig | ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                return Failure(session, "open Broker service");
            }

            sidInfo = Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(sidInfo, unchecked((int)ServiceSidTypeUnrestricted));
            if (!ChangeServiceConfig2(service, ServiceConfigServiceSidInfo, sidInfo))
            {
                return Failure(session, "set Broker service SID type");
            }

            uint required;
            if (!QueryServiceConfig2(
                    service,
                    ServiceConfigServiceSidInfo,
                    sidInfo,
                    sizeof(uint),
                    out required)
                || unchecked((uint)Marshal.ReadInt32(sidInfo)) != ServiceSidTypeUnrestricted)
            {
                return Failure(session, "verify Broker service SID type");
            }

            session.Log("Monitoring XS Broker service SID type is unrestricted.");
            return ActionResult.Success;
        }
        catch (Exception exception)
        {
            session.Log("Monitoring XS Broker service configuration failed: {0}", exception.GetType().Name);
            return ActionResult.Failure;
        }
        finally
        {
            if (sidInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(sidInfo);
            }
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            if (manager != IntPtr.Zero)
            {
                CloseServiceHandle(manager);
            }
        }
    }

    private static ActionResult Failure(Session session, string operation)
    {
        session.Log(
            "Monitoring XS installer could not {0}; Win32 error {1}.",
            operation,
            Marshal.GetLastWin32Error());
        return ActionResult.Failure;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(
        IntPtr serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        IntPtr service,
        int informationLevel,
        IntPtr information);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(
        IntPtr service,
        int informationLevel,
        IntPtr buffer,
        int bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
