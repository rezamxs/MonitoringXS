namespace MonitoringXS.IntegrationTests;

public sealed class InstallerLifecycleScriptTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "scripts",
        "installer",
        "Invoke-InstallerLifecycleValidation.ps1"));

    [Fact]
    public void MsiPhaseReturnsOnlyTheAuthoritativeScalarExitCode()
    {
        Assert.Contains("[int]$exitCode = Start-MsiProcess $arguments $LogName", Script, StringComparison.Ordinal);
        Assert.Contains("$null = Add-Result $LogName", Script, StringComparison.Ordinal);
        Assert.Contains("return [int]$exitCode", Script, StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit($script:MsiPhaseTimeoutMilliseconds)", Script, StringComparison.Ordinal);
        Assert.Contains("Phase: $Phase", Script, StringComparison.Ordinal);
        Assert.Contains("'lifecycle-phases.log'", Script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MonitoringXS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("MonitoringXS repository root was not found.");
    }
}
