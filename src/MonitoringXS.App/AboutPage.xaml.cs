using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using MonitoringXS.App.Localization;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App;

public sealed partial class AboutPage : UserControl, IDisposable
{
    private readonly LocalizationService _localization;

    public AboutPage()
    {
        _localization = ((App)Microsoft.UI.Xaml.Application.Current).Localization;
        InitializeComponent();
        ApplyLocalization();
        _localization.LanguageChanged += Localization_LanguageChanged;
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs args) =>
        ApplyLocalization();

    private void ApplyLocalization()
    {
        ProductTitle.Text = AppIdentity.ProductName;
        VersionText.Text = AppIdentity.DisplayVersion;
        BetaBadge.Text = AppIdentity.BetaChannel;
        DescriptionText.Text = _localization.Get(LocalizationKeys.AboutDescription);
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => _localization.Get(LocalizationKeys.ProcessArchitectureX86),
            Architecture.X64 => _localization.Get(LocalizationKeys.ProcessArchitectureX64),
            Architecture.Arm64 => _localization.Get(LocalizationKeys.ProcessArchitectureArm64),
            _ => _localization.Get(LocalizationKeys.ProcessArchitectureUnknown)
        };
        PlatformLabel.Text = $"{_localization.Get(LocalizationKeys.AboutPlatform)} · {architecture}";
        OpenSourceLabel.Text = _localization.Get(LocalizationKeys.AboutOpenSource);
        PrivacySummary.Text = _localization.Get(LocalizationKeys.AboutPrivacySummary);
        PrivacyDetail.Text = _localization.Get(LocalizationKeys.AboutPrivacyDetail);
        RepositoryLabel.Text = _localization.Get(LocalizationKeys.AboutRepository);
        RepositoryLink.Content = AppIdentity.RepositoryUrl;
        LicenseLabel.Text = $"{_localization.Get(LocalizationKeys.AboutLicense)} · {AppIdentity.License}";
        CopyrightLabel.Text = AppIdentity.Copyright;
        WhatsNewTitle.Text = _localization.Get(LocalizationKeys.WhatsNewTitle);
        BetaLimitationsTitle.Text = _localization.Get(LocalizationKeys.BetaLimitationsTitle);

        WhatsNewList.ItemsSource = new[]
        {
            _localization.Get(LocalizationKeys.WhatsNewLocalization),
            _localization.Get(LocalizationKeys.WhatsNewSearch),
            _localization.Get(LocalizationKeys.WhatsNewSorting),
            _localization.Get(LocalizationKeys.WhatsNewDiagnostics),
            _localization.Get(LocalizationKeys.WhatsNewMetricExplanations),
            _localization.Get(LocalizationKeys.WhatsNewProcessSelection),
            _localization.Get(LocalizationKeys.WhatsNewProcessSafety),
            _localization.Get(LocalizationKeys.WhatsNewCpuMonitoring),
            _localization.Get(LocalizationKeys.WhatsNewMemoryMonitoring),
            _localization.Get(LocalizationKeys.WhatsNewDiskMonitoring),
            _localization.Get(LocalizationKeys.WhatsNewNetworkMonitoring),
            _localization.Get(LocalizationKeys.WhatsNewGpuMonitoring),
            _localization.Get(LocalizationKeys.WhatsNewHistory),
            _localization.Get(LocalizationKeys.WhatsNewChartGaps),
            _localization.Get(LocalizationKeys.WhatsNewInstaller),
            _localization.Get(LocalizationKeys.WhatsNewSystemOverviewFoundation)
        };

        BetaLimitationsList.ItemsSource = new[]
        {
            _localization.Get(LocalizationKeys.BetaLimitationProviders),
            _localization.Get(LocalizationKeys.BetaLimitationHardware),
            _localization.Get(LocalizationKeys.BetaLimitationPermissions),
            _localization.Get(LocalizationKeys.BetaLimitationMetricStates),
            _localization.Get(LocalizationKeys.BetaLimitationChartGaps),
            _localization.Get(LocalizationKeys.BetaLimitationNoFakeData),
            _localization.Get(LocalizationKeys.BetaLimitationDefects)
        };
    }

    public void Dispose() => _localization.LanguageChanged -= Localization_LanguageChanged;
}
