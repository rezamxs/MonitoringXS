using System.Reflection;

namespace MonitoringXS.Core.Models;

/// <summary>
/// Authoritative application identity sourced from assembly metadata.
/// All version and channel values are defined once in Directory.Build.props
/// and flow through MSBuild-generated assembly attributes.
/// </summary>
public static class AppIdentity
{
    private static readonly Assembly Assembly = typeof(AppIdentity).Assembly;

    public static string ProductName { get; } =
        Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Monitoring XS";

    public static string NumericVersion { get; } =
        Assembly.GetName().Version?.ToString(3) ?? "0.9.0";

    public static string DisplayVersion { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? NumericVersion;

    public static string BetaChannel { get; } = "Public Beta";

    public static string Copyright { get; } =
        Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    public static string RepositoryUrl { get; } =
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "RepositoryUrl", StringComparison.Ordinal))
            ?.Value
        ?? "https://github.com/rezamxs/MonitoringXS";

    public static string License { get; } = "MIT";
}
