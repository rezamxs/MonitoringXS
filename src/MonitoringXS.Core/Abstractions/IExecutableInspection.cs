using MonitoringXS.Core.Models;

namespace MonitoringXS.Core.Abstractions;

public interface IExecutableMetadataProvider
{
    ValueTask<ExecutableMetadata> GetMetadataAsync(string executablePath, CancellationToken cancellationToken);
}

public interface IDigitalSignatureInspector
{
    ValueTask<DigitalSignatureInfo> InspectAsync(string executablePath, CancellationToken cancellationToken);
}

public interface IApplicationIconProvider
{
    ValueTask<ApplicationIconData?> GetIconAsync(string sourcePath, int pixelSize, CancellationToken cancellationToken);
}
