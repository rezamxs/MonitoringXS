using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Metadata;

namespace MonitoringXS.Platform.Windows.Processes;

public sealed class WindowsProcessDiscoveryService : IProcessDiscoveryService
{
    private const int MetadataCacheCapacity = 512;
    // Active-instance cache; entries disappear with their process and new entries stop caching at capacity.
    private const int ProcessDetailsCacheCapacity = 2048;
    private static readonly TimeSpan MetadataRevalidationInterval = TimeSpan.FromMinutes(10);
    private readonly IExecutableMetadataProvider _metadataProvider;
    private readonly Func<IReadOnlyList<NativeProcessTree.ProcessEntry>> _captureProcesses;
    private readonly Func<IReadOnlyDictionary<int, NativeWindowSnapshot.WindowDescriptor>> _captureWindows;
    private readonly Func<int, NativeProcessDetails.ProcessDetails?, NativeProcessDetails.ProcessDetailsReadResult> _readDetails;
    private readonly object _cacheGate = new();
    private readonly Dictionary<int, ProcessInstanceId> _instanceByPid = [];
    private readonly Dictionary<ProcessInstanceId, NativeProcessDetails.ProcessDetails> _processDetailsByInstance = [];
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
                cached = _instanceByPid.TryGetValue(process.ProcessId, out ProcessInstanceId instance)
                    && _processDetailsByInstance.TryGetValue(instance, out NativeProcessDetails.ProcessDetails? existing)
                    ? existing
                    : null;
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
                    if (_instanceByPid.TryGetValue(process.ProcessId, out ProcessInstanceId previous)
                        && previous != descriptor.InstanceId)
                    {
                        _instanceByPid.Remove(process.ProcessId);
                        _processDetailsByInstance.Remove(previous);
                    }

                    if (_processDetailsByInstance.ContainsKey(descriptor.InstanceId)
                        || _processDetailsByInstance.Count < ProcessDetailsCacheCapacity)
                    {
                        _instanceByPid[process.ProcessId] = descriptor.InstanceId;
                        _processDetailsByInstance[descriptor.InstanceId] = details!;
                    }
                }
            }
        }

        lock (_cacheGate)
        {
            foreach (int stalePid in _instanceByPid.Keys.Where(pid => !liveProcessIds.Contains(pid)).ToArray())
            {
                _instanceByPid.Remove(stalePid);
            }

            HashSet<ProcessInstanceId> liveInstances = _instanceByPid.Values.ToHashSet();
            foreach (ProcessInstanceId stale in _processDetailsByInstance.Keys
                .Where(instance => !liveInstances.Contains(instance))
                .ToArray())
            {
                _processDetailsByInstance.Remove(stale);
            }
        }

        Dictionary<string, ExecutableMetadata> metadataByPath = new(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HashSet<string> liveMetadataPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in basics
            .Where(item => !item.IsServiceSession)
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

        Dictionary<int, BasicProcessDescriptor> basicByPid = basics.ToDictionary(item => item.InstanceId.ProcessId);
        ProcessDescriptor[] processes = basics.Select(item => item.ToProcessDescriptor(
            item.ExecutablePath is not null ? metadataByPath.GetValueOrDefault(item.ExecutablePath) : null,
            ResolveParentName(item, basicByPid))).ToArray();
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
            window is not null,
            details.Architecture,
            MetricValue<int>.Available(process.ThreadCount),
            details.HandleCount);
    }

    private static string? ResolveParentName(
        BasicProcessDescriptor process,
        IReadOnlyDictionary<int, BasicProcessDescriptor> processesByPid)
    {
        if (process.ParentProcessId is not int parentPid
            || !processesByPid.TryGetValue(parentPid, out BasicProcessDescriptor? parent)
            || parent.InstanceId.StartTimeUtc > process.InstanceId.StartTimeUtc)
        {
            return null;
        }

        return parent.NormalizedProcessName;
    }

    private sealed record BasicProcessDescriptor(
        ProcessInstanceId InstanceId,
        string ProcessName,
        string? ExecutablePath,
        string? MainWindowTitle,
        int? ParentProcessId,
        bool IsServiceSession,
        bool HasVisibleWindow,
        ProcessArchitecture Architecture,
        MetricValue<int> ThreadCount,
        MetricValue<int> HandleCount)
    {
        public string NormalizedProcessName => ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? ProcessName[..^4]
            : ProcessName;

        public ProcessDescriptor ToProcessDescriptor(ExecutableMetadata? metadata, string? parentProcessName) => new(
            InstanceId,
            ProcessName,
            ExecutablePath,
            metadata?.ProductName,
            metadata?.FileDescription,
            metadata?.CompanyName,
            MainWindowTitle,
            ParentProcessId,
            IsServiceSession,
            HasVisibleWindow)
        {
            Architecture = Architecture,
            ThreadCount = ThreadCount,
            HandleCount = HandleCount,
            ParentProcessName = parentProcessName,
            FileVersion = metadata?.FileVersion
        };
    }

    private sealed record MetadataCacheEntry(ExecutableMetadata Metadata, DateTimeOffset RevalidateAt);
}
