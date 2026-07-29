using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MonitoringXS.Platform.Windows.Broker;

public sealed record BrokerPipeEndpoint
{
    private BrokerPipeEndpoint(string userSid, string? logonSid, int sessionId)
    {
        UserSid = userSid;
        LogonSid = logonSid;
        SessionId = sessionId;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userSid}|{logonSid}|{sessionId}"));
        PipeName = $"{PrivilegedEtwBrokerProtocol.PipeNamePrefix}.{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }

    public string UserSid { get; }

    public string? LogonSid { get; }

    public int SessionId { get; }

    public string PipeName { get; }

    public static string ComputePipeName(
        string userSid,
        int sessionId,
        string? logonSid = null) =>
        Create(userSid, sessionId, logonSid).PipeName;

    public static BrokerPipeEndpoint Create(string userSid, int sessionId, string? logonSid = null)
    {
        SecurityIdentifier user = ParseSid(userSid, nameof(userSid));
        if (!user.IsAccountSid())
        {
            throw new ArgumentException("The broker client SID must be an account SID.", nameof(userSid));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionId);
        if (logonSid is not null
            && !ParseSid(logonSid, nameof(logonSid)).IsWellKnown(WellKnownSidType.LogonIdsSid))
        {
            throw new ArgumentException("The broker logon SID is malformed.", nameof(logonSid));
        }

        return new BrokerPipeEndpoint(user.Value, logonSid, sessionId);
    }

    public static BrokerPipeEndpoint ForCurrentProcess()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value
            ?? throw new UnauthorizedAccessException("The current process user SID is unavailable.");
        string? logonSid = identity.Groups?
            .Select(group => group as SecurityIdentifier)
            .FirstOrDefault(group => group?.IsWellKnown(WellKnownSidType.LogonIdsSid) == true)?
            .Value;
        return Create(userSid, Process.GetCurrentProcess().SessionId, logonSid);
    }

    private static SecurityIdentifier ParseSid(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A SID is required.", parameterName);
        }

        try
        {
            return new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The SID is malformed.", parameterName, exception);
        }
    }
}

internal static class BrokerPipeSecurity
{
    internal const PipeAccessRights ClientAccess =
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    public static PipeSecurity Create(
        BrokerPipeEndpoint endpoint,
        SecurityIdentifier serviceSid,
        bool setOwner = true,
        SecurityIdentifier? ownerSid = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(serviceSid);
        SecurityIdentifier owner = ownerSid
            ?? new SecurityIdentifier(WellKnownSidType.LocalServiceSid, domainSid: null);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        if (setOwner)
        {
            security.SetOwner(owner);
        }
        security.AddAccessRule(Rule(WellKnownSidType.NetworkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(endpoint.LogonSid ?? endpoint.UserSid),
            ClientAccess,
            AccessControlType.Allow));
        return security;
    }

    public static SecurityIdentifier ResolveServiceSid(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("A service name is required.", nameof(serviceName));
        }

        try
        {
            return (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName)
                .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException exception)
        {
            throw new InvalidOperationException(
                $"The dedicated service SID for '{serviceName}' is unavailable.",
                exception);
        }
    }

    private static PipeAccessRule Rule(
        WellKnownSidType sidType,
        PipeAccessRights rights,
        AccessControlType type) =>
        new(new SecurityIdentifier(sidType, domainSid: null), rights, type);
}
