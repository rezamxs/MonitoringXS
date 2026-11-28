using MonitoringXS.App.ViewModels;

namespace MonitoringXS.App.Tests;

public sealed class PrivilegedMetricsConsentPolicyTests
{
    [Fact]
    public void PromptsOnlyForARejectedUnsignedClientWithoutOptInOrSuppression()
    {
        Assert.True(PrivilegedMetricsConsentPolicy.ShouldPrompt(
            BrokerOperationalState.ClientRejected,
            brokerSigned: false,
            consentGranted: false,
            promptSuppressedByUser: false,
            alreadyPromptedThisSession: false));
    }

    [Theory]
    // Each row flips exactly one condition away from the prompting case.
    [InlineData(BrokerOperationalState.Healthy, false, false, false, false)]
    [InlineData(BrokerOperationalState.NotInstalled, false, false, false, false)]
    [InlineData(BrokerOperationalState.Stopped, false, false, false, false)]
    [InlineData(BrokerOperationalState.ConnectionUnavailable, false, false, false, false)]
    [InlineData(BrokerOperationalState.ProtocolMismatch, false, false, false, false)]
    [InlineData(BrokerOperationalState.ClientRejected, true, false, false, false)]
    [InlineData(BrokerOperationalState.ClientRejected, false, true, false, false)]
    [InlineData(BrokerOperationalState.ClientRejected, false, false, true, false)]
    [InlineData(BrokerOperationalState.ClientRejected, false, false, false, true)]
    public void DoesNotPromptWhenAnyConditionFails(
        BrokerOperationalState state,
        bool brokerSigned,
        bool consentGranted,
        bool suppressed,
        bool alreadyPrompted)
    {
        Assert.False(PrivilegedMetricsConsentPolicy.ShouldPrompt(
            state,
            brokerSigned,
            consentGranted,
            suppressed,
            alreadyPrompted));
    }

    [Theory]
    [InlineData(true, false, PrivilegedMetricsConsentState.NotRequired)]
    [InlineData(true, true, PrivilegedMetricsConsentState.NotRequired)]
    [InlineData(false, true, PrivilegedMetricsConsentState.Allowed)]
    [InlineData(false, false, PrivilegedMetricsConsentState.NotAllowed)]
    public void SettingsStateIgnoresTheOptInForASignedService(
        bool brokerSigned,
        bool consentGranted,
        PrivilegedMetricsConsentState expected)
    {
        Assert.Equal(expected, PrivilegedMetricsConsentPolicy.Resolve(brokerSigned, consentGranted));
    }
}
