using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Metadata;

namespace MonitoringXS.Platform.Windows.Processes;

public sealed class WindowsProcessDiscoveryService : IProcessDiscoveryService
{
    private const int MetadataCacheCapacity = 512;
    private static readonly TimeSpan MetadataRevalidationInterval = TimeSpan.FromMinutes(10);
    private readonly IExecutableMetadataProvider _metadataProvider;
    private readonly object _cacheGate = new();
    private readonly Dictionary<int, NativeProcessDetails.ProcessDetails> _processDetailsByPid = [];
    private readonly Dictionary<string, MetadataCacheEntry> _metadataByPath = new(StringComparer.OrdinalIgnoreCase);

    public WindowsProcessDiscoveryService()
        : this(new ExecutableMetadataProvider())
    {
    }

    public WindowsProcessDiscoveryService(IExecutableMetadataProvider metadataProvider)
    {
        _metadataProvider = metadataProvider;
    }

    public async ValueTask<IReadOnlyList<ProcessDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<int, NativeWindowSnapshot.WindowDescriptor> windows = NativeWindowSnapshot.Capture();
        List<BasicProcessDescriptor> basics = [];
        HashSet<int> liveProcessIds = [];

        foreach (NativeProcessTree.ProcessEntry process in NativeProcessTree.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            liveProcessIds.Add(process.ProcessId);
            windows.TryGetValue(process.ProcessId, out NativeWindowSnapshot.WindowDescriptor? window);
            NativeProcessDetails.ProcessDetails? cached;
            lock (_cacheGate)
            {
                _processDetailsByPid.TryGetValue(process.ProcessId, out cached);
            }

            NativeProcessDetails.ProcessDetails? details = NativeProcessDetails.TryRead(process.ProcessId, cached);
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

        return basics.Select(item => item.ToProcessDescriptor(
            item.ExecutablePath is not null ? metadataByPath.GetValueOrDefault(item.ExecutablePath) : null)).ToArray();
    }

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
