using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Metadata;

namespace MonitoringXS.Platform.Windows.Processes;

public sealed class WindowsProcessDiscoveryService : IProcessDiscoveryService
{
    private const int MetadataCacheCapacity = 512;
    private static readonly TimeSpan MetadataRevalidationInterval = TimeSpan.FromMinutes(10);
    private readonly IExecutableMetadataProvider _metadataProvider;
    private readonly Func<IReadOnlyList<NativeProcessTree.ProcessEntry>> _captureProcesses;
    private readonly Func<IReadOnlyDictionary<int, NativeWindowSnapshot.WindowDescriptor>> _captureWindows;
    private readonly Func<int, NativeProcessDetails.ProcessDetails?, NativeProcessDetails.ProcessDetailsReadResult> _readDetails;
    private readonly object _cacheGate = new();
    private readonly Dictionary<int, NativeProcessDetails.ProcessDetails> _processDetailsByPid = [];
    private readonly Dictionary<string, MetadataCacheEntry> _metadataByPath = new(StringComparer.OrdinalIgnoreCase);

    public WindowsProcessDiscoveryService()
        : this(new ExecutableMetadataProvider())
    {
    }

    public WindowsProcessDiscoveryService(IExecutableMetadataProvider metadataProvider)
        : this(
            metadataProvider,
            NativeProcessTree.Snapshot,
            NativeWindowSnapshot.Capture,
            NativeProcessDetails.Read)
    {
    }

    internal WindowsProcessDiscoveryService(
        IExecutableMetadataProvider metadataProvider,
        Func<IReadOnlyList<NativeProcessTree.ProcessEntry>> captureProcesses,
        Func<IReadOnlyDictionary<int, NativeWindowSnapshot.WindowDescriptor>> captureWindows,
        Func<int, NativeProcessDetails.ProcessDetails?, NativeProcessDetails.ProcessDetailsReadResult> readDetails)
    {
        ArgumentNullException.ThrowIfNull(metadataProvider);
        ArgumentNullException.ThrowIfNull(captureProcesses);
        ArgumentNullException.ThrowIfNull(captureWindows);
        ArgumentNullException.ThrowIfNull(readDetails);
        _metadataProvider = metadataProvider;
        _captureProcesses = captureProcesses;
        _captureWindows = captureWindows;
        _readDetails = readDetails;
    }

    public async ValueTask<ProcessDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<NativeProcessTree.ProcessEntry> processSnapshot = _captureProcesses();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<int, NativeWindowSnapshot.WindowDescriptor> windows = _captureWindows();
        List<BasicProcessDescriptor> basics = [];
        List<ProcessDiscoveryIssue> issues = [];
        HashSet<int> liveProcessIds = [];

        foreach (NativeProcessTree.ProcessEntry process in processSnapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            liveProcessIds.Add(process.ProcessId);
            windows.TryGetValue(process.ProcessId, out NativeWindowSnapshot.WindowDescriptor? window);
            NativeProcessDetails.ProcessDetails? cached;
            lock (_cacheGate)
            {
                _processDetailsByPid.TryGetValue(process.ProcessId, out cached);
            }

            NativeProcessDetails.ProcessDetailsReadResult read = _readDetails(process.ProcessId, cached);
            NativeProcessDetails.ProcessDetails? details = read.Details;
            if (details is null)
            {
                issues.Add(new(
                    process.ProcessId,
                    ToDescriptorIssueKind(read.Failure),
                    "Process details could not be materialized."));
            }
            else if (details.ExecutablePathFailure != NativeProcessDetails.ProcessDetailsReadFailure.None)
            {
                issues.Add(new(
                    process.ProcessId,
                    ToPathIssueKind(details.ExecutablePathFailure),
                    "Executable path is unavailable."));
            }
            BasicProcessDescriptor? descriptor = TryDescribeBasic(process, window, details);
            if (descriptor is not null)
            {
                basics.Add(descriptor);
                lock (_cacheGate)
                {
                    _processDetailsByPid[process.ProcessId] = details!;
                }
            }
        }

        lock (_cacheGate)
        {
            foreach (int stalePid in _processDetailsByPid.Keys.Where(pid => !liveProcessIds.Contains(pid)).ToArray())
            {
                _processDetailsByPid.Remove(stalePid);
            }
        }

        Dictionary<string, ExecutableMetadata> metadataByPath = new(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HashSet<string> liveMetadataPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in basics
            .Where(item => !item.IsServiceSession && item.HasVisibleWindow)
            .Select(item => item.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            liveMetadataPaths.Add(path);
            MetadataCacheEntry? cached;
            lock (_cacheGate)
            {
                _metadataByPath.TryGetValue(path, out cached);
            }

            if (cached is not null && now < cached.RevalidateAt)
            {
                metadataByPath[path] = cached.Metadata;
                continue;
            }

            ExecutableMetadata metadata = await _metadataProvider.GetMetadataAsync(path, cancellationToken);
            metadataByPath[path] = metadata;
            if (!metadata.IsAvailable)
            {
                ProcessDiscoveryIssueKind kind = string.Equals(
                    metadata.UnavailableReason,
                    nameof(UnauthorizedAccessException),
                    StringComparison.Ordinal)
                        ? ProcessDiscoveryIssueKind.AccessDenied
                        : ProcessDiscoveryIssueKind.MetadataUnavailable;
                foreach (BasicProcessDescriptor basic in basics.Where(item =>
                    string.Equals(item.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add(new(basic.InstanceId.ProcessId, kind, metadata.UnavailableReason));
                }
            }
            lock (_cacheGate)
            {
                if (_metadataByPath.ContainsKey(path) || _metadataByPath.Count < MetadataCacheCapacity)
                {
                    _metadataByPath[path] = new MetadataCacheEntry(
                        metadata,
                        now.Add(MetadataRevalidationInterval));
                }
            }
        }

        lock (_cacheGate)
        {
            foreach (string stalePath in _metadataByPath.Keys
                .Where(path => !liveMetadataPaths.Contains(path))
                .ToArray())
            {
                _metadataByPath.Remove(stalePath);
            }
        }

        ProcessDescriptor[] processes = basics.Select(item => item.ToProcessDescriptor(
            item.ExecutablePath is not null ? metadataByPath.GetValueOrDefault(item.ExecutablePath) : null)).ToArray();
        return new(
            liveProcessIds.Order().ToArray(),
            processes,
            issues.ToArray());
    }

    private static ProcessDiscoveryIssueKind ToDescriptorIssueKind(
        NativeProcessDetails.ProcessDetailsReadFailure failure) => failure switch
        {
            NativeProcessDetails.ProcessDetailsReadFailure.AccessDenied => ProcessDiscoveryIssueKind.AccessDenied,
            NativeProcessDetails.ProcessDetailsReadFailure.ProcessExited => ProcessDiscoveryIssueKind.ProcessExited,
            _ => ProcessDiscoveryIssueKind.DescriptorUnavailable
        };

    private static ProcessDiscoveryIssueKind ToPathIssueKind(
        NativeProcessDetails.ProcessDetailsReadFailure failure) => failure switch
        {
            NativeProcessDetails.ProcessDetailsReadFailure.AccessDenied => ProcessDiscoveryIssueKind.AccessDenied,
            NativeProcessDetails.ProcessDetailsReadFailure.ProcessExited => ProcessDiscoveryIssueKind.ProcessExited,
            _ => ProcessDiscoveryIssueKind.ExecutablePathUnavailable
        };

    private static BasicProcessDescriptor? TryDescribeBasic(
        NativeProcessTree.ProcessEntry process,
        NativeWindowSnapshot.WindowDescriptor? window,
        NativeProcessDetails.ProcessDetails? details)
    {
        if (details is null)
        {
            return null;
        }

        return new BasicProcessDescriptor(
            new ProcessInstanceId(process.ProcessId, details.StartTimeUtc),
            process.ExecutableName,
            details.ExecutablePath,
            window?.Title,
            process.ParentProcessId,
            details.IsServiceSession,
            window is not null);
    }

    private sealed record BasicProcessDescriptor(
        ProcessInstanceId InstanceId,
        string ProcessName,
        string? ExecutablePath,
        string? MainWindowTitle,
        int? ParentProcessId,
        bool IsServiceSession,
        bool HasVisibleWindow)
    {
        public ProcessDescriptor ToProcessDescriptor(ExecutableMetadata? metadata) => new(
            InstanceId,
            ProcessName,
            ExecutablePath,
            metadata?.ProductName,
            metadata?.FileDescription,
            metadata?.CompanyName,
            MainWindowTitle,
            ParentProcessId,
            IsServiceSession,
            HasVisibleWindow);
    }

    private sealed record MetadataCacheEntry(ExecutableMetadata Metadata, DateTimeOffset RevalidateAt);
}
