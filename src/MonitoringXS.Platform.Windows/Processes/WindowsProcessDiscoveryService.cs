using System.Collections.Concurrent;
using System.Diagnostics;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Processes;

public sealed class WindowsProcessDiscoveryService : IProcessDiscoveryService
{
    private readonly ConcurrentDictionary<ProcessInstanceId, CachedMetadata> _metadataCache = new();

    public ValueTask<IReadOnlyList<ProcessDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<int, int> parents = NativeProcessTree.SnapshotParents();
        List<ProcessDescriptor> descriptors = [];
        HashSet<ProcessInstanceId> liveInstances = [];

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessDescriptor? descriptor = TryDescribe(process, parents);
                if (descriptor is null)
                {
                    continue;
                }

                liveInstances.Add(descriptor.InstanceId);
                descriptors.Add(descriptor);
            }
        }

        if (_metadataCache.Count > liveInstances.Count + 128)
        {
            foreach (ProcessInstanceId key in _metadataCache.Keys)
            {
                if (!liveInstances.Contains(key))
                {
                    _metadataCache.TryRemove(key, out _);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<ProcessDescriptor>>(descriptors);
    }

    private ProcessDescriptor? TryDescribe(Process process, IReadOnlyDictionary<int, int> parents)
    {
        try
        {
            DateTimeOffset startTime = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            ProcessInstanceId instanceId = new(process.Id, startTime);
            bool hasVisibleWindow = process.MainWindowHandle != nint.Zero;
            string? title = hasVisibleWindow ? NullIfWhitespace(process.MainWindowTitle) : null;
            int sessionId = TryGetSessionId(process);

            CachedMetadata metadata = _metadataCache.GetOrAdd(instanceId, _ => ReadMetadata(process));
            parents.TryGetValue(process.Id, out int parentId);

            return new ProcessDescriptor(
                instanceId,
                process.ProcessName,
                metadata.ExecutablePath,
                metadata.ProductName,
                metadata.FileDescription,
                metadata.Publisher,
                title,
                parentId == 0 ? null : parentId,
                sessionId == 0,
                hasVisibleWindow);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static CachedMetadata ReadMetadata(Process process)
    {
        try
        {
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return CachedMetadata.Empty;
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            return new CachedMetadata(
                path,
                NullIfWhitespace(version.ProductName),
                NullIfWhitespace(version.FileDescription),
                NullIfWhitespace(version.CompanyName));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or NotSupportedException
            or IOException
            or System.Security.SecurityException)
        {
            return CachedMetadata.Empty;
        }
    }

    private static int TryGetSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static string? NullIfWhitespace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record CachedMetadata(string? ExecutablePath, string? ProductName, string? FileDescription, string? Publisher)
    {
        public static CachedMetadata Empty { get; } = new(null, null, null, null);
    }
}
