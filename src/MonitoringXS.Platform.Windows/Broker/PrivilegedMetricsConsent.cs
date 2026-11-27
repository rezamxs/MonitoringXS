using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace MonitoringXS.Platform.Windows.Broker;

/// <summary>
/// The administrator's explicit opt-in that lets an unsigned Privileged Metrics
/// Service accept the unsigned Monitoring XS app.
/// </summary>
/// <remarks>
/// Stored as <c>HKLM\SOFTWARE\MonitoringXS\PrivilegedMetrics</c>, value
/// <c>AllowUnsignedClient</c> = 1 (REG_DWORD), in the 64-bit registry view.
/// Only an elevated administrator can create keys under HKLM\SOFTWARE, and the
/// key is written with a protected DACL (SYSTEM and Administrators full control,
/// Users read) owned by Administrators. The service still verifies the owner and
/// DACL every time it reads the opt-in, so a key a standard user could create or
/// modify is ignored. A signed service never reads it.
/// </remarks>
public static class PrivilegedMetricsConsent
{
    public const string RegistryKeyPath = @"SOFTWARE\MonitoringXS\PrivilegedMetrics";
    public const string ValueName = "AllowUnsignedClient";
    public const string GrantArgument = "--grant-unsigned-client-consent";
    public const string RevokeArgument = "--revoke-unsigned-client-consent";
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const RegistryRights WriteRights = RegistryRights.SetValue
        | RegistryRights.CreateSubKey
        | RegistryRights.CreateLink
        | RegistryRights.Delete
        | RegistryRights.ChangePermissions
        | RegistryRights.TakeOwnership;

    /// <summary>Fixed install location of the service executable that the MSI ships.</summary>
    public static string InstalledBrokerPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Monitoring XS",
        "Broker",
        "MonitoringXS.PrivilegedBroker.exe");

    /// <summary>
    /// True only when the opt-in value is exactly 1 (REG_DWORD) and the key is
    /// owned and writable only by SYSTEM, Administrators or TrustedInstaller.
    /// </summary>
    public static bool IsGranted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = machine.OpenSubKey(RegistryKeyPath, writable: false);
            if (key is null)
            {
                return false;
            }

            RegistrySecurity security = key.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            return IsGranted(
                key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                key.GetValueKind(ValueName),
                IsProtected(security));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or SystemException)
        {
            return false;
        }
    }

    /// <summary>Pure opt-in decision: a protected key holding DWORD 1.</summary>
    internal static bool IsGranted(object? value, RegistryValueKind kind, bool keyIsProtected) =>
        keyIsProtected
        && kind == RegistryValueKind.DWord
        && value is int number
        && number == 1;

    /// <summary>
    /// A key is trusted only when its owner is SYSTEM, Administrators or
    /// TrustedInstaller and no other principal is allowed to write, delete,
    /// re-permission or take ownership of it.
    /// </summary>
    internal static bool IsProtected(RegistrySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !IsTrustedPrincipal(owner))
        {
            return false;
        }

        foreach (RegistryAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || (rule.RegistryRights & WriteRights) == 0
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            if (!IsTrustedPrincipal((SecurityIdentifier)rule.IdentityReference))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The DACL the opt-in key is written with: protected from inheritance,
    /// SYSTEM and Administrators full control, Users read only.
    /// </summary>
    internal static RegistrySecurity CreateProtectedSecurity()
    {
        RegistrySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            RegistryRights.ReadKey,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Writes the opt-in. Must run in an elevated administrator process (the
    /// service executable's <see cref="GrantArgument"/> mode, launched through UAC).
    /// </summary>
    public static void Grant()
    {
        EnsureElevatedAdministrator();
        RegistrySecurity security = CreateProtectedSecurity();
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey key = machine.CreateSubKey(
            RegistryKeyPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            security);
        // A pre-existing key keeps its old DACL through CreateSubKey; reset it.
        key.SetAccessControl(security);
        key.SetValue(ValueName, 1, RegistryValueKind.DWord);
    }

    /// <summary>Removes the opt-in. Must run in an elevated administrator process.</summary>
    public static void Revoke()
    {
        EnsureElevatedAdministrator();
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        machine.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
    }

    public static bool IsElevatedAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>False in development (Debug) builds, whose service does not check publishers.</summary>
    public static bool ServiceChecksPublisher => BrokerClientExecutableValidator.RequiresTrustedPublisher;

    /// <summary>True when the installed service executable carries an Authenticode signature.</summary>
    public static bool IsInstalledBrokerSigned() =>
        File.Exists(InstalledBrokerPath)
        && BrokerClientExecutableValidator.ReadPublisher(InstalledBrokerPath) is not null;

    private static void EnsureElevatedAdministrator()
    {
        if (!IsElevatedAdministrator())
        {
            throw new UnauthorizedAccessException("Changing the privileged metrics opt-in requires an elevated administrator.");
        }
    }

    private static bool IsTrustedPrincipal(SecurityIdentifier sid) =>
        sid.Value == TrustedInstallerSid
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
}

public enum PrivilegedMetricsConsentChangeResult
{
    Succeeded,
    Cancelled,
    HelperMissing,
    Failed
}

/// <summary>
/// Asks Windows (UAC, "runas") to run the installed service executable in its
/// consent mode, which is the only code path that writes or removes the opt-in.
/// </summary>
public static class PrivilegedMetricsConsentElevation
{
    private const int ErrorCancelled = 1223;

    public static async Task<PrivilegedMetricsConsentChangeResult> RequestAsync(
        bool grant,
        CancellationToken cancellationToken)
    {
        string helper = PrivilegedMetricsConsent.InstalledBrokerPath;
        if (!File.Exists(helper))
        {
            return PrivilegedMetricsConsentChangeResult.HelperMissing;
        }

        // ShellExecute with the "runas" verb is what raises the UAC prompt. The
        // argument is one fixed, space-free constant; no caller text is passed.
        ProcessStartInfo startInfo = new(
            helper,
            grant ? PrivilegedMetricsConsent.GrantArgument : PrivilegedMetricsConsent.RevokeArgument)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helper)!
        };
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return PrivilegedMetricsConsentChangeResult.Failed;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0
                ? PrivilegedMetricsConsentChangeResult.Succeeded
                : PrivilegedMetricsConsentChangeResult.Failed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return PrivilegedMetricsConsentChangeResult.Cancelled;
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or IOException)
        {
            return PrivilegedMetricsConsentChangeResult.Failed;
        }
    }
}
