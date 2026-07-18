using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Attribution;

public sealed class ApplicationAttributionService : IApplicationAttributionService
{
    private static readonly HashSet<string> InfrastructureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "csrss", "wininit", "winlogon", "smss", "services",
        "svchost", "lsass", "fontdrvhost", "dwm", "sihost", "taskhostw", "runtimebroker",
        "searchindexer", "spoolsv", "audiodg", "wudfhost", "securityhealthservice"
    };

    private static readonly Dictionary<string, KnownApplication> KnownApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = new("google-chrome", "Google Chrome", "Google LLC"),
        ["msedge"] = new("microsoft-edge", "Microsoft Edge", "Microsoft Corporation"),
        ["code"] = new("visual-studio-code", "Visual Studio Code", "Microsoft Corporation"),
        ["devenv"] = new("visual-studio", "Visual Studio", "Microsoft Corporation"),
        ["discord"] = new("discord", "Discord", "Discord Inc."),
        ["spotify"] = new("spotify", "Spotify", "Spotify AB"),
        ["steam"] = new("steam", "Steam", "Valve Corporation"),
        ["epicgameslauncher"] = new("epic-games-launcher", "Epic Games Launcher", "Epic Games, Inc."),
        ["windowsterminal"] = new("windows-terminal", "Windows Terminal", "Microsoft Corporation"),
        ["wt"] = new("windows-terminal", "Windows Terminal", "Microsoft Corporation"),
        ["notepad"] = new("notepad", "Notepad", "Microsoft Corporation"),
        ["calculatorapp"] = new("calculator", "Calculator", "Microsoft Corporation"),
        ["calculator"] = new("calculator", "Calculator", "Microsoft Corporation"),
        ["powershell"] = new("powershell", "Windows PowerShell", "Microsoft Corporation"),
        ["pwsh"] = new("powershell", "PowerShell", "Microsoft Corporation")
    };

    private static readonly HashSet<string> VsCodeHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "git", "rg", "language_server", "code-tunnel", "electron"
    };

    public IReadOnlyList<AttributionResult> Attribute(IReadOnlyList<ProcessDescriptor> processes)
    {
        Dictionary<int, ProcessDescriptor> byPid = processes.ToDictionary(item => item.InstanceId.ProcessId);
        List<AttributionResult> results = new(processes.Count);

        foreach (ProcessDescriptor process in processes)
        {
            results.Add(AttributeOne(process, byPid));
        }

        return results;
    }

    private static AttributionResult AttributeOne(ProcessDescriptor process, IReadOnlyDictionary<int, ProcessDescriptor> byPid)
    {
        string name = process.NormalizedProcessName;
        if (InfrastructureNames.Contains(name))
        {
            return AttributionResult.Hidden(process, "Known Windows infrastructure process.");
        }

        if (KnownApplications.TryGetValue(name, out KnownApplication? known))
        {
            return AttributionResult.Attributed(process, CreateKnownIdentity(process, known));
        }

        if (IsGameExecutable(process))
        {
            return AttributionResult.Attributed(process, CreatePathIdentity(process, "Game-library executable kept separate from its launcher."));
        }

        KnownApplication? parentApplication = FindKnownAncestor(process, byPid);
        if (parentApplication is not null && parentApplication.Id == "visual-studio-code" && VsCodeHelpers.Contains(name))
        {
            ApplicationIdentity identity = new(
                parentApplication.Id,
                parentApplication.DisplayName,
                parentApplication.Publisher,
                ApplicationDisposition.Installed,
                GetInstallationRoot(process.ExecutablePath),
                ClassificationConfidence.High,
                "Known development helper with direct process ancestry to Visual Studio Code.");
            return AttributionResult.Attributed(process, identity);
        }

        if (process.IsServiceSession)
        {
            return AttributionResult.Hidden(process, "Process belongs to the service session.");
        }

        if (IsWindowsSystemPath(process.ExecutablePath))
        {
            return AttributionResult.Hidden(process, "Executable is Windows infrastructure without a user-facing identity.");
        }

        if (!process.HasVisibleWindow && string.IsNullOrWhiteSpace(process.ProductName))
        {
            return AttributionResult.Hidden(process, "No user-facing window or application metadata was found.");
        }

        return AttributionResult.Attributed(process, CreatePathIdentity(process, process.HasVisibleWindow
            ? "Visible top-level window with executable identity."
            : "Non-system executable with application metadata."));
    }

    private static ApplicationIdentity CreateKnownIdentity(ProcessDescriptor process, KnownApplication known) => new(
        known.Id,
        known.DisplayName,
        process.Publisher ?? known.Publisher,
        DetectDisposition(process.ExecutablePath),
        GetInstallationRoot(process.ExecutablePath),
        ClassificationConfidence.High,
        "Exact known user-facing executable rule.");

    private static ApplicationIdentity CreatePathIdentity(ProcessDescriptor process, string reason)
    {
        string displayName = process.ProductName ?? process.FileDescription ?? process.NormalizedProcessName;
        string stablePart = process.ExecutablePath?.ToLowerInvariant() ?? process.NormalizedProcessName.ToLowerInvariant();
        string logicalId = $"exe:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stablePart)))[..16].ToLowerInvariant()}";
        ApplicationDisposition disposition = DetectDisposition(process.ExecutablePath);

        return new ApplicationIdentity(
            logicalId,
            displayName,
            process.Publisher,
            disposition,
            GetInstallationRoot(process.ExecutablePath),
            process.HasVisibleWindow ? ClassificationConfidence.Medium : ClassificationConfidence.Low,
            reason);
    }

    private static KnownApplication? FindKnownAncestor(ProcessDescriptor process, IReadOnlyDictionary<int, ProcessDescriptor> byPid)
    {
        int? current = process.ParentProcessId;
        for (int depth = 0; depth < 6 && current.HasValue; depth++)
        {
            if (!byPid.TryGetValue(current.Value, out ProcessDescriptor? parent))
            {
                break;
            }

            if (KnownApplications.TryGetValue(parent.NormalizedProcessName, out KnownApplication? known))
            {
                return known;
            }

            current = parent.ParentProcessId;
        }

        return null;
    }

    private static bool IsGameExecutable(ProcessDescriptor process)
    {
        string path = process.ExecutablePath ?? string.Empty;
        return path.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase) &&
               !process.NormalizedProcessName.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationDisposition DetectDisposition(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ApplicationDisposition.Unresolved;
        }

        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationDisposition.Packaged;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (IsUnder(path, programFiles) || IsUnder(path, programFilesX86) || IsUnder(path, localPrograms))
        {
            return ApplicationDisposition.Installed;
        }

        return ApplicationDisposition.Portable;
    }

    private static bool IsWindowsSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return IsUnder(path, windows);
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? GetInstallationRoot(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);

    private sealed record KnownApplication(string Id, string DisplayName, string Publisher);
}
