using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IApplicationSettingsStore : IDisposable, IAsyncDisposable
{
    ValueTask<ApplicationSettingsLoadResult> LoadAsync(CancellationToken cancellationToken);

    ValueTask<ApplicationSettingsSaveResult> SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken);
}
