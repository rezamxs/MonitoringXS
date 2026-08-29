using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using WixToolset.Dtf.WindowsInstaller;

namespace MonitoringXS.Installer.CustomActions;

public static class CaptureBrokerIdentity
{
    [CustomAction]
    public static ActionResult Capture(Session session)
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            using (Process process = Process.GetCurrentProcess())
            {
                SecurityIdentifier? user = identity.User;
                SecurityIdentifier? logon = identity.Groups?
                    .OfType<SecurityIdentifier>()
                    .FirstOrDefault(group => group.IsWellKnown(WellKnownSidType.LogonIdsSid));
                if (user == null || !user.IsAccountSid() || process.SessionId <= 0)
                {
                    session.Log("Monitoring XS installer could not resolve a safe interactive Broker identity.");
                    return ActionResult.Failure;
                }

                session["BROKER_USER_SID"] = user.Value;
                session["BROKER_LOGON_ARGUMENT"] = logon == null
                    ? string.Empty
                    : "--logon-sid " + logon.Value;
                session["BROKER_SESSION_ID"] = process.SessionId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                session.Log("Monitoring XS installer resolved Broker user and session identity; logon SID is optional.");
                return ActionResult.Success;
            }
        }
        catch (Exception exception)
        {
            session.Log("Monitoring XS installer identity resolution failed: {0}", exception.GetType().Name);
            return ActionResult.Failure;
        }
    }
}
