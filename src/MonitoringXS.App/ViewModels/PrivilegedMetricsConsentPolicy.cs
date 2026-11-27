using MonitoringXS.Platform.Windows.Broker;

namespace MonitoringXS.App.ViewModels;

public enum PrivilegedMetricsConsentState
{
    /// <summary>The installed service is code-signed (or a development build); it never reads the opt-in.</summary>
    NotRequired,
    Allowed,
    NotAllowed
}

/// <summary>
/// Reads and changes the administrator opt-in that lets an unsigned service
/// accept this unsigned app. Changing it always goes through UAC.
/// </summary>
internal interface IPrivilegedMetricsConsentService
{
    bool IsGranted();

    /// <summary>
    /// True when the installed service checks publishers (Release builds) and is
    /// itself unsigned, which is the only case the opt-in changes anything.
    /// </summary>
    bool OptInApplies();

    Task<PrivilegedMetricsConsentChangeResult> RequestChangeAsync(
        bool grant,
        CancellationToken cancellationToken);

    Task ReconnectAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsPrivilegedMetricsConsentService(PrivilegedEtwBrokerClient? client)
    : IPrivilegedMetricsConsentService
{
    public bool IsGranted() => PrivilegedMetricsConsent.IsGranted();

    public bool OptInApplies() =>
        PrivilegedMetricsConsent.ServiceChecksPublisher
        && !PrivilegedMetricsConsent.IsInstalledBrokerSigned();

    public Task<PrivilegedMetricsConsentChangeResult> RequestChangeAsync(
        bool grant,
        CancellationToken cancellationToken) =>
        PrivilegedMetricsConsentElevation.RequestAsync(grant, cancellationToken);

    public Task ReconnectAsync(CancellationToken cancellationToken) =>
        client?.ResetConnectionAsync(cancellationToken) ?? Task.CompletedTask;
}

/// <summary>
/// Pure decisions for the startup consent prompt and the Settings state.
/// </summary>
internal static class PrivilegedMetricsConsentPolicy
{
    /// <summary>
    /// Prompt only when the running service rejected this app, the service is
    /// unsigned (so an opt-in can actually help), no opt-in exists yet, the user
    /// has not chosen "Don't ask again", and this app run has not asked already.
    /// </summary>
    public static bool ShouldPrompt(
        BrokerOperationalState brokerState,
        bool brokerSigned,
        bool consentGranted,
        bool promptSuppressedByUser,
        bool alreadyPromptedThisSession) =>
        brokerState == BrokerOperationalState.ClientRejected
        && !brokerSigned
        && !consentGranted
        && !promptSuppressedByUser
        && !alreadyPromptedThisSession;

    public static PrivilegedMetricsConsentState Resolve(bool brokerSigned, bool consentGranted) =>
        brokerSigned
            ? PrivilegedMetricsConsentState.NotRequired
            : consentGranted
                ? PrivilegedMetricsConsentState.Allowed
                : PrivilegedMetricsConsentState.NotAllowed;
}
