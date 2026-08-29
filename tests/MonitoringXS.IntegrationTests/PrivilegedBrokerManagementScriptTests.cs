namespace MonitoringXS.IntegrationTests;

public sealed class PrivilegedBrokerManagementScriptTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "scripts",
        "privileged-broker",
        "Manage-PrivilegedBroker.ps1"));

    [Fact]
    public void PublicModesAndRepositoryRootResolutionAreFixed()
    {
        Assert.Contains("[ValidateSet('Install', 'Status', 'Remove')]", Script, StringComparison.Ordinal);
        Assert.Contains("MonitoringXS.sln", Script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\", Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedServiceConfigurationUsesHardenedPersistentContract()
    {
        Assert.Contains("$serviceName = 'MonitoringXS.PrivilegedEtwBroker'", Script, StringComparison.Ordinal);
        Assert.Contains("$serviceAccount = 'LocalSystem'", Script, StringComparison.Ordinal);
        Assert.Contains("'start=', 'auto'", Script, StringComparison.Ordinal);
        Assert.Contains("sidtype', $serviceName, 'unrestricted'", Script, StringComparison.Ordinal);
        Assert.Contains("Get-ProtocolVersion", Script, StringComparison.Ordinal);
        Assert.DoesNotContain("MonitoringXS.App.exe", Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovalIsConfinedToManagedServiceAndDirectory()
    {
        Assert.Contains(
            "Refusing removal: service executable path is outside the managed installation directory.",
            Script,
            StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $paths.Root", Script, StringComparison.Ordinal);
        Assert.DoesNotContain("history.db", Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".artifacts", Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalStatusOutputUsesSafeOperationalCategories()
    {
        foreach (string category in new[]
        {
            "Installed:",
            "State:",
            "StartType:",
            "Account:",
            "Binary:",
            "Version:",
            "ProtocolCompatibility:"
        })
        {
            Assert.Contains(category, Script, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("PipeSddl", Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Write-Output \"SID", Script, StringComparison.OrdinalIgnoreCase);
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
