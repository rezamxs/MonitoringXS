using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IUserAttributionOverrideStore
{
    ValueTask<UserAttributionOverrideSnapshot> GetAllAsync(CancellationToken cancellationToken);

    ValueTask<OverrideMutationResult> UpsertAsync(UserAttributionOverride attributionOverride, CancellationToken cancellationToken);

    ValueTask<OverrideMutationResult> RemoveAsync(string executablePath, CancellationToken cancellationToken);
}
