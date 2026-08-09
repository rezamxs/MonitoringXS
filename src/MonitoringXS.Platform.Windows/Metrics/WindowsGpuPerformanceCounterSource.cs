using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Processes;

namespace MonitoringXS.Platform.Windows.Metrics;

public sealed partial class WindowsGpuPerformanceCounterSource : IGpuCounterSource, IDisposable
{
    private const string EngineCounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const string DedicatedMemoryCounterPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const string SharedMemoryCounterPath = @"\GPU Process Memory(*)\Shared Usage";
    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorNotEnoughMemory = 8;
    private const uint ErrorInvalidData = 13;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhRetry = 0x800007D4;
    private const uint PdhNoData = 0x800007D5;
    private const uint PdhAccessDenied = 0xC0000BDB;
    private const uint PdhNoObject = 0xC0000BB8;
    private const uint PdhNoCounter = 0xC0000BB9;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtLarge = 0x00000400;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumCounterArrayBytes = 64 * 1024 * 1024;
    private const uint MaximumCounterArrayItems = 65_536;
    private const int MaximumArrayReadAttempts = 3;
    private static readonly long ParentSnapshotLifetimeTicks =
        checked(Stopwatch.Frequency * 5L);
    private static readonly long QueryRetryIntervalTicks =
        checked(Stopwatch.Frequency * 60L);
    private readonly object _gate = new();
    private readonly GpuProcessLifetimeTracker _engineLifetimeTracker = new();
    private readonly GpuProcessLifetimeTracker _dedicatedMemoryLifetimeTracker = new();
    private readonly GpuProcessLifetimeTracker _sharedMemoryLifetimeTracker = new();
    private Dictionary<int, int?> _parentByPid = [];
    private long _parentSnapshotValidUntil;
    private nint _query;
    private nint _engineCounter;
    private nint _dedicatedMemoryCounter;
    private nint _sharedMemoryCounter;
    private uint _dedicatedMemorySetupStatus;
    private uint _sharedMemorySetupStatus;
    private int _queryOpenCount;
    private uint _lastQueryFailureStatus;
    private long _nextQueryAttemptTimestamp;
    private bool _hasBaseline;
    private bool _disposed;

    internal bool IsQueryOpen
    {
        get
        {
            lock (_gate)
            {
                return _query != 0;
            }
        }
    }

    public ValueTask<GpuCounterBatch> CaptureAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DateTimeOffset normalizedCapturedAt = capturedAtUtc.ToUniversalTime();
            try
            {
                return ValueTask.FromResult(CaptureCore(
                    processes,
                    normalizedCapturedAt,
                    cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableCaptureException(exception))
            {
                CloseQuery();
                RegisterQueryFailure(ErrorInvalidData);
                return ValueTask.FromResult(UnavailableBatch(
                    processes,
                    normalizedCapturedAt,
                    MetricAvailability.Error,
                    GpuAvailabilityReason.InvalidData,
                    "Windows returned malformed GPU counter data; the interval was rejected.",
                    0));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CloseQuery();
            _disposed = true;
        }
    }

    private GpuCounterBatch CaptureCore(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (processes.Count == 0)
        {
            return new GpuCounterBatch(
                [],
                MetricAvailability.Available,
                GpuAvailabilityReason.None,
                Diagnostics(
                    MetricAvailability.Available,
                    GpuAvailabilityReason.None,
                    null,
                    processes.Count,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    stopwatch.Elapsed.TotalMilliseconds));
        }

        uint initializeStatus = EnsureQuery();
        if (initializeStatus != ErrorSuccess)
        {
            stopwatch.Stop();
            return UnavailableBatch(
                processes,
                capturedAtUtc,
                initializeStatus,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        uint collectionStatus = PdhCollectQueryData(_query);
        if (collectionStatus != ErrorSuccess)
        {
            PdhFailure failure = ClassifyPdhFailure(collectionStatus);
            CloseQuery();
            RegisterQueryFailure(collectionStatus);
            stopwatch.Stop();
            return UnavailableBatch(
                processes,
                capturedAtUtc,
                failure.Availability,
                failure.Reason,
                failure.Detail,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        if (!_hasBaseline)
        {
            _hasBaseline = true;
            stopwatch.Stop();
            return UnavailableBatch(
                processes,
                capturedAtUtc,
                MetricAvailability.WarmingUp,
                GpuAvailabilityReason.WarmingUp,
                "A second PDH sample is required for GPU engine utilization.",
                stopwatch.Elapsed.TotalMilliseconds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        CounterArrayResult<double> engineResult = ReadDoubleArray(_engineCounter);
        CounterArrayResult<long> dedicatedResult = _dedicatedMemoryCounter != 0
            ? ReadLongArray(_dedicatedMemoryCounter)
            : new CounterArrayResult<long>(_dedicatedMemorySetupStatus, [], 0);
        CounterArrayResult<long> sharedResult = _sharedMemoryCounter != 0
            ? ReadLongArray(_sharedMemoryCounter)
            : new CounterArrayResult<long>(_sharedMemorySetupStatus, [], 0);
        PdhFailure engineFailure = ClassifyCounterResult(
            engineResult.Status,
            "GPU engine utilization");
        PdhFailure dedicatedFailure = ClassifyCounterResult(
            dedicatedResult.Status,
            "dedicated GPU memory");
        PdhFailure sharedFailure = ClassifyCounterResult(
            sharedResult.Status,
            "shared GPU memory");
        bool recreateQueryAfterCapture = engineFailure.Availability is
            MetricAvailability.Error or MetricAvailability.Unsupported;

        IGrouping<int, ProcessDescriptor>[] targetGroups = processes
            .GroupBy(process => process.InstanceId.ProcessId)
            .ToArray();
        HashSet<int> ambiguousTargetPids = targetGroups
            .Where(group => group.Select(item => item.InstanceId).Distinct().Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet();
        Dictionary<int, ProcessDescriptor> targetByPid = targetGroups
            .Where(group => !ambiguousTargetPids.Contains(group.Key))
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<int, int?> parentByPid = GetParentSnapshot();
        Dictionary<int, int?> targetAncestorByPid = [];
        Dictionary<int, Dictionary<GpuEngineId, double>> enginesByPid = [];
        Dictionary<GpuMemoryInstanceId, ulong> dedicatedByInstance = [];
        Dictionary<GpuMemoryInstanceId, ulong> sharedByInstance = [];
        HashSet<int> incompleteEnginePids = [];
        HashSet<int> incompleteDedicatedPids = [];
        HashSet<int> incompleteSharedPids = [];
        HashSet<GpuMemoryInstanceId> dedicatedInstances = [];
        HashSet<GpuMemoryInstanceId> sharedInstances = [];
        HashSet<ulong> adapters = [];
        HashSet<int> observedEngineCounterPids = [];
        HashSet<int> observedDedicatedMemoryCounterPids = [];
        HashSet<int> observedSharedMemoryCounterPids = [];
        int invalidCounterSamples = engineResult.InvalidNativeItems
            + dedicatedResult.InvalidNativeItems
            + sharedResult.InvalidNativeItems;
        int malformedCounterInstances = invalidCounterSamples;
        int duplicateCounterInstances = 0;
        int unattributedDescendantCounterInstances = 0;
        int outsideApplicationSetCounterInstances = 0;
        int exitedProcessCounterInstances = 0;
        int unknownProcessCounterInstances = 0;
        double machineWideMaxEngineUtilization = 0d;
        bool machineWideEngineFound = false;

        foreach (PdhCounterValue<double> item in engineResult.Items)
        {
            if (!TryParseEngineInstance(
                item.InstanceName,
                out int processId,
                out GpuEngineId engine))
            {
                if (TryExtractRawPid(item.InstanceName, out int rawPid) && rawPid <= 0)
                {
                    unknownProcessCounterInstances++;
                }
                else
                {
                    malformedCounterInstances++;
                }

                invalidCounterSamples++;
                continue;
            }

            observedEngineCounterPids.Add(processId);
            adapters.Add(engine.AdapterLuid);

            // Track machine-wide busiest engine across ALL processes, before target filtering.
            if (IsValidCounterStatus(item.CounterStatus)
                && double.IsFinite(item.Value)
                && item.Value >= 0
                && item.Value <= 100d)
            {
                machineWideMaxEngineUtilization = Math.Max(
                    machineWideMaxEngineUtilization, item.Value);
                machineWideEngineFound = true;
            }

            if (!targetByPid.ContainsKey(processId))
            {
                if (TryFindTargetAncestor(
                    processId,
                    parentByPid,
                    targetByPid,
                    targetAncestorByPid,
                    out int targetAncestor))
                {
                    incompleteEnginePids.Add(targetAncestor);
                    unattributedDescendantCounterInstances++;
                }
                else if (parentByPid.ContainsKey(processId))
                {
                    outsideApplicationSetCounterInstances++;
                }
                else
                {
                    exitedProcessCounterInstances++;
                }

                continue;
            }

            if (!IsValidCounterStatus(item.CounterStatus)
                || !double.IsFinite(item.Value)
                || item.Value < 0
                || item.Value > 100d)
            {
                incompleteEnginePids.Add(processId);
                invalidCounterSamples++;
                continue;
            }

            if (!enginesByPid.TryGetValue(
                    processId,
                    out Dictionary<GpuEngineId, double>? engines))
            {
                engines = [];
                enginesByPid.Add(processId, engines);
            }

            if (engines.TryGetValue(engine, out double existing))
            {
                duplicateCounterInstances++;
                incompleteEnginePids.Add(processId);
                engines[engine] = Math.Max(existing, item.Value);
            }
            else
            {
                engines.Add(engine, item.Value);
            }
        }

        ReadMemoryItems(
            dedicatedResult.Items,
            targetByPid,
            dedicatedByInstance,
            dedicatedInstances,
            adapters,
            incompleteDedicatedPids,
            parentByPid,
            targetAncestorByPid,
            observedDedicatedMemoryCounterPids,
            ref unattributedDescendantCounterInstances,
            ref outsideApplicationSetCounterInstances,
            ref exitedProcessCounterInstances,
            ref unknownProcessCounterInstances,
            ref duplicateCounterInstances,
            ref malformedCounterInstances,
            ref invalidCounterSamples);
        ReadMemoryItems(
            sharedResult.Items,
            targetByPid,
            sharedByInstance,
            sharedInstances,
            adapters,
            incompleteSharedPids,
            parentByPid,
            targetAncestorByPid,
            observedSharedMemoryCounterPids,
            ref unattributedDescendantCounterInstances,
            ref outsideApplicationSetCounterInstances,
            ref exitedProcessCounterInstances,
            ref unknownProcessCounterInstances,
            ref duplicateCounterInstances,
            ref malformedCounterInstances,
            ref invalidCounterSamples);
        ProcessInstanceId[] targetInstances = processes
            .Select(process => process.InstanceId)
            .ToArray();
        GpuProcessLifetimeTrustResult engineTrust = _engineLifetimeTracker.Update(
            targetInstances,
            observedEngineCounterPids,
            engineResult.Status == ErrorSuccess);
        GpuProcessLifetimeTrustResult dedicatedMemoryTrust =
            _dedicatedMemoryLifetimeTracker.Update(
                targetInstances,
                observedDedicatedMemoryCounterPids,
                dedicatedResult.Status == ErrorSuccess);
        GpuProcessLifetimeTrustResult sharedMemoryTrust =
            _sharedMemoryLifetimeTracker.Update(
                targetInstances,
                observedSharedMemoryCounterPids,
                sharedResult.Status == ErrorSuccess);
        HashSet<int> lifetimeReusePids =
        [
            .. engineTrust.PidReusePids,
            .. dedicatedMemoryTrust.PidReusePids,
            .. sharedMemoryTrust.PidReusePids
        ];
        List<GpuProcessCounterSnapshot> snapshots = new(processes.Count);
        int pidReuseRejected = lifetimeReusePids.Count;
        int inaccessible = 0;
        foreach (ProcessDescriptor descriptor in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ambiguousTargetPids.Contains(descriptor.InstanceId.ProcessId))
            {
                invalidCounterSamples++;
                snapshots.Add(UnavailableSnapshot(
                    descriptor.InstanceId,
                    capturedAtUtc,
                    MetricAvailability.Error,
                    "More than one process lifetime was supplied for the same PID."));
                continue;
            }

            ProcessIdentityCheck identity = ValidateProcessIdentity(descriptor.InstanceId);
            if (!identity.IsMatch)
            {
                if (identity.Reason == GpuAvailabilityReason.PidReused)
                {
                    if (lifetimeReusePids.Add(descriptor.InstanceId.ProcessId))
                    {
                        pidReuseRejected++;
                    }
                }
                else
                {
                    inaccessible++;
                }

                snapshots.Add(UnavailableSnapshot(
                    descriptor.InstanceId,
                    capturedAtUtc,
                    identity.Availability,
                    identity.Detail));
                continue;
            }

            int processId = descriptor.InstanceId.ProcessId;
            bool engineQuarantined = engineTrust.QuarantinedPids.Contains(processId);
            bool dedicatedMemoryQuarantined =
                dedicatedMemoryTrust.QuarantinedPids.Contains(processId);
            bool sharedMemoryQuarantined =
                sharedMemoryTrust.QuarantinedPids.Contains(processId);
            GpuEngineUsage[] engines = !engineQuarantined
                && enginesByPid.TryGetValue(
                processId,
                out Dictionary<GpuEngineId, double>? processEngines)
                ? processEngines
                    .Select(pair => new GpuEngineUsage(pair.Key, pair.Value))
                    .ToArray()
                : [];
            GpuMemoryInstanceId[] processDedicatedInstances = dedicatedInstances
                .Where(instance => instance.ProcessId == processId)
                .ToArray();
            GpuMemoryInstanceId[] processSharedInstances = sharedInstances
                .Where(instance => instance.ProcessId == processId)
                .ToArray();
            MetricValue<ulong> dedicated = dedicatedMemoryQuarantined
                ? QuarantinedMemory("dedicated GPU memory")
                : SumMemory(
                    processDedicatedInstances,
                    dedicatedByInstance,
                    dedicatedFailure,
                    incompleteDedicatedPids.Contains(processId),
                    "dedicated GPU memory");
            MetricValue<ulong> shared = sharedMemoryQuarantined
                ? QuarantinedMemory("shared GPU memory")
                : SumMemory(
                    processSharedInstances,
                    sharedByInstance,
                    sharedFailure,
                    incompleteSharedPids.Contains(processId),
                    "shared GPU memory");
            MetricAvailability engineAvailability = engineQuarantined
                ? MetricAvailability.Unavailable
                : engineFailure.Availability;
            string? engineDetail = engineQuarantined
                ? QuarantineDetail("GPU utilization")
                : engineFailure.Detail;
            if (!engineQuarantined
                && engineAvailability == MetricAvailability.Available
                && incompleteEnginePids.Contains(processId))
            {
                engineAvailability = MetricAvailability.Partial;
                engineDetail = "One or more GPU engine counter instances were invalid or duplicated; utilization is incomplete.";
            }

            snapshots.Add(new GpuProcessCounterSnapshot(
                descriptor.InstanceId,
                capturedAtUtc,
                engines,
                dedicated,
                shared,
                engineAvailability,
                engineDetail));
        }

        MetricAvailability[] snapshotStates = snapshots
            .SelectMany(snapshot => new[]
            {
                snapshot.EngineAvailability,
                snapshot.DedicatedMemoryBytes.Availability,
                snapshot.SharedMemoryBytes.Availability
            })
            .ToArray();
        int quarantinedUtilizationSamples = engineTrust.QuarantinedPids.Count;
        int quarantinedDedicatedMemorySamples =
            dedicatedMemoryTrust.QuarantinedPids.Count;
        int quarantinedSharedMemorySamples =
            sharedMemoryTrust.QuarantinedPids.Count;
        int firstObservationCounterSamplesRejected =
            engineTrust.FirstObservationPids.Count
            + dedicatedMemoryTrust.FirstObservationPids.Count
            + sharedMemoryTrust.FirstObservationPids.Count;
        int quarantinedCounterSamples = quarantinedUtilizationSamples
            + quarantinedDedicatedMemorySamples
            + quarantinedSharedMemorySamples;
        MetricAvailability batchAvailability = CombineAvailability(snapshotStates);
        GpuAvailabilityReason reason = batchAvailability == MetricAvailability.Available
            ? GpuAvailabilityReason.None
            : pidReuseRejected > 0
                ? GpuAvailabilityReason.PidReused
                : quarantinedCounterSamples > 0
                    ? GpuAvailabilityReason.AmbiguousCounterLifetime
                : snapshotStates.Contains(MetricAvailability.AccessDenied)
                    ? GpuAvailabilityReason.AccessDenied
                    : inaccessible > 0 || unattributedDescendantCounterInstances > 0
                        ? GpuAvailabilityReason.ProcessUnavailable
                        : invalidCounterSamples > 0
                            ? GpuAvailabilityReason.InvalidData
                            : FirstReason(
                                engineFailure.Reason,
                                dedicatedFailure.Reason,
                                sharedFailure.Reason);
        stopwatch.Stop();
        GpuCollectorDiagnostics diagnostics = Diagnostics(
            batchAvailability,
            reason,
            batchAvailability == MetricAvailability.Available
                ? null
                : quarantinedCounterSamples > 0
                    ? $"{quarantinedCounterSamples} GPU process counter samples are quarantined until a complete gap and later reappearance establish a safe counter lifetime."
                    : "Some target process GPU samples were incomplete or unavailable.",
            processes.Count,
            snapshots.Count(snapshot =>
                snapshot.EngineAvailability is MetricAvailability.Available or MetricAvailability.Partial
                || snapshot.DedicatedMemoryBytes.IsAvailable
                || snapshot.SharedMemoryBytes.IsAvailable),
            engineResult.Items.Count,
            dedicatedInstances.Union(sharedInstances).Count(),
            adapters.Count,
            pidReuseRejected,
            inaccessible,
            unattributedDescendantCounterInstances,
            invalidCounterSamples,
            stopwatch.Elapsed.TotalMilliseconds,
            outsideApplicationSetCounterInstances,
            exitedProcessCounterInstances,
            unknownProcessCounterInstances,
            malformedCounterInstances,
            duplicateCounterInstances,
            Math.Max(0, _queryOpenCount - 1),
            engineFailure.Availability,
            dedicatedFailure.Availability,
            sharedFailure.Availability,
            firstObservationCounterSamplesRejected,
            quarantinedUtilizationSamples,
            quarantinedDedicatedMemorySamples,
            quarantinedSharedMemorySamples);
        if (recreateQueryAfterCapture)
        {
            CloseQuery();
            RegisterQueryFailure(engineResult.Status);
        }

        MetricValue<double>? machineWideGpu = engineFailure.Availability is
            MetricAvailability.Available or MetricAvailability.Partial
            ? machineWideEngineFound
                ? new MetricValue<double>(machineWideMaxEngineUtilization, MetricAvailability.Available, null)
                : null
            : null;

        return new GpuCounterBatch(snapshots, batchAvailability, reason, diagnostics)
        {
            MachineWideGpuUtilizationPercent = machineWideGpu
        };
    }

    private uint EnsureQuery()
    {
        if (_query != 0)
        {
            return ErrorSuccess;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastQueryFailureStatus != ErrorSuccess
            && now < _nextQueryAttemptTimestamp)
        {
            return _lastQueryFailureStatus;
        }

        uint status = PdhOpenQuery(null, 0, out _query);
        if (status != ErrorSuccess)
        {
            _query = 0;
            RegisterQueryFailure(status);
            return status;
        }

        _queryOpenCount++;
        status = PdhAddEnglishCounter(_query, EngineCounterPath, 0, out _engineCounter);
        if (status != ErrorSuccess)
        {
            CloseQuery();
            RegisterQueryFailure(status);
            return status;
        }

        _dedicatedMemorySetupStatus = PdhAddEnglishCounter(
            _query,
            DedicatedMemoryCounterPath,
            0,
            out _dedicatedMemoryCounter);
        _sharedMemorySetupStatus = PdhAddEnglishCounter(
            _query,
            SharedMemoryCounterPath,
            0,
            out _sharedMemoryCounter);
        _lastQueryFailureStatus = ErrorSuccess;
        _nextQueryAttemptTimestamp = 0;
        return ErrorSuccess;
    }

    private void RegisterQueryFailure(uint status)
    {
        _lastQueryFailureStatus = status;
        long now = Stopwatch.GetTimestamp();
        _nextQueryAttemptTimestamp = now > long.MaxValue - QueryRetryIntervalTicks
            ? long.MaxValue
            : now + QueryRetryIntervalTicks;
    }

    private void CloseQuery()
    {
        if (_query != 0)
        {
            _ = PdhCloseQuery(_query);
        }

        _query = 0;
        _engineCounter = 0;
        _dedicatedMemoryCounter = 0;
        _sharedMemoryCounter = 0;
        _dedicatedMemorySetupStatus = ErrorSuccess;
        _sharedMemorySetupStatus = ErrorSuccess;
        _hasBaseline = false;
        _engineLifetimeTracker.ResetForProviderRestart();
        _dedicatedMemoryLifetimeTracker.ResetForProviderRestart();
        _sharedMemoryLifetimeTracker.ResetForProviderRestart();
    }

    private Dictionary<int, int?> GetParentSnapshot()
    {
        long now = Stopwatch.GetTimestamp();
        if (_parentByPid.Count == 0 || now >= _parentSnapshotValidUntil)
        {
            _parentByPid = NativeProcessTree.Snapshot()
                .ToDictionary(process => process.ProcessId, process => process.ParentProcessId);
            _parentSnapshotValidUntil = now > long.MaxValue - ParentSnapshotLifetimeTicks
                ? long.MaxValue
                : now + ParentSnapshotLifetimeTicks;
        }

        return _parentByPid;
    }

    private static CounterArrayResult<double> ReadDoubleArray(nint counter)
    {
        for (int attempt = 0; attempt < MaximumArrayReadAttempts; attempt++)
        {
            uint bufferSize = 0;
            uint itemCount = 0;
            uint status = PdhGetFormattedCounterArrayDouble(
                counter,
                PdhFmtDouble | PdhFmtNoCap100,
                ref bufferSize,
                ref itemCount,
                0);
            if (status == PdhNoData)
            {
                return new CounterArrayResult<double>(PdhNoData, [], 0);
            }

            if (status is PdhRetry)
            {
                continue;
            }

            if (status != PdhMoreData)
            {
                return new CounterArrayResult<double>(status, [], 0);
            }

            if (!IsSafeCounterBuffer(bufferSize, itemCount))
            {
                return new CounterArrayResult<double>(ErrorNotEnoughMemory, [], 0);
            }

            nint buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
            try
            {
                status = PdhGetFormattedCounterArrayDouble(
                    counter,
                    PdhFmtDouble | PdhFmtNoCap100,
                    ref bufferSize,
                    ref itemCount,
                    buffer);
                if (status is PdhMoreData or PdhRetry)
                {
                    continue;
                }

                if (status != ErrorSuccess
                    || !IsSafeCounterBuffer(bufferSize, itemCount))
                {
                    return new CounterArrayResult<double>(
                        status == ErrorSuccess ? ErrorNotEnoughMemory : status,
                        [],
                        0);
                }

                int itemSize = Marshal.SizeOf<PdhFormattedCounterValueItemDouble>();
                if (!DoesItemArrayFit(bufferSize, itemCount, itemSize))
                {
                    return new CounterArrayResult<double>(ErrorNotEnoughMemory, [], 0);
                }

                List<PdhCounterValue<double>> items = new(checked((int)itemCount));
                int invalidNativeItems = 0;
                for (int index = 0; index < itemCount; index++)
                {
                    PdhFormattedCounterValueItemDouble item =
                        Marshal.PtrToStructure<PdhFormattedCounterValueItemDouble>(
                            buffer + checked(index * itemSize));
                    if (!TryReadBufferString(
                            item.InstanceName,
                            buffer,
                            bufferSize,
                            out string name))
                    {
                        invalidNativeItems++;
                        continue;
                    }

                    items.Add(new PdhCounterValue<double>(
                        name,
                        item.Value.Value,
                        item.Value.CounterStatus));
                }

                return new CounterArrayResult<double>(
                    ErrorSuccess,
                    items,
                    invalidNativeItems);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new CounterArrayResult<double>(PdhRetry, [], 0);
    }

    private static CounterArrayResult<long> ReadLongArray(nint counter)
    {
        for (int attempt = 0; attempt < MaximumArrayReadAttempts; attempt++)
        {
            uint bufferSize = 0;
            uint itemCount = 0;
            uint status = PdhGetFormattedCounterArrayLong(
                counter,
                PdhFmtLarge,
                ref bufferSize,
                ref itemCount,
                0);
            if (status == PdhNoData)
            {
                return new CounterArrayResult<long>(PdhNoData, [], 0);
            }

            if (status is PdhRetry)
            {
                continue;
            }

            if (status != PdhMoreData)
            {
                return new CounterArrayResult<long>(status, [], 0);
            }

            if (!IsSafeCounterBuffer(bufferSize, itemCount))
            {
                return new CounterArrayResult<long>(ErrorNotEnoughMemory, [], 0);
            }

            nint buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
            try
            {
                status = PdhGetFormattedCounterArrayLong(
                    counter,
                    PdhFmtLarge,
                    ref bufferSize,
                    ref itemCount,
                    buffer);
                if (status is PdhMoreData or PdhRetry)
                {
                    continue;
                }

                if (status != ErrorSuccess
                    || !IsSafeCounterBuffer(bufferSize, itemCount))
                {
                    return new CounterArrayResult<long>(
                        status == ErrorSuccess ? ErrorNotEnoughMemory : status,
                        [],
                        0);
                }

                int itemSize = Marshal.SizeOf<PdhFormattedCounterValueItemLong>();
                if (!DoesItemArrayFit(bufferSize, itemCount, itemSize))
                {
                    return new CounterArrayResult<long>(ErrorNotEnoughMemory, [], 0);
                }

                List<PdhCounterValue<long>> items = new(checked((int)itemCount));
                int invalidNativeItems = 0;
                for (int index = 0; index < itemCount; index++)
                {
                    PdhFormattedCounterValueItemLong item =
                        Marshal.PtrToStructure<PdhFormattedCounterValueItemLong>(
                            buffer + checked(index * itemSize));
                    if (!TryReadBufferString(
                            item.InstanceName,
                            buffer,
                            bufferSize,
                            out string name))
                    {
                        invalidNativeItems++;
                        continue;
                    }

                    items.Add(new PdhCounterValue<long>(
                        name,
                        item.Value.Value,
                        item.Value.CounterStatus));
                }

                return new CounterArrayResult<long>(
                    ErrorSuccess,
                    items,
                    invalidNativeItems);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new CounterArrayResult<long>(PdhRetry, [], 0);
    }

    internal static bool IsSafeCounterBuffer(uint bufferSize, uint itemCount) =>
        bufferSize > 0
        && bufferSize <= MaximumCounterArrayBytes
        && itemCount <= MaximumCounterArrayItems;

    internal static bool DoesItemArrayFit(uint bufferSize, uint itemCount, int itemSize) =>
        itemSize > 0
        && (ulong)itemCount * (uint)itemSize <= bufferSize;

    private static bool TryReadBufferString(
        nint stringPointer,
        nint buffer,
        uint bufferSize,
        out string value)
    {
        value = string.Empty;
        long offset = stringPointer.ToInt64() - buffer.ToInt64();
        if (stringPointer == 0
            || offset < 0
            || (ulong)offset >= bufferSize
            || (offset & 1) != 0)
        {
            return false;
        }

        int maximumCharacters = checked((int)((bufferSize - (uint)offset) / sizeof(char)));
        for (int index = 0; index < maximumCharacters; index++)
        {
            if (Marshal.ReadInt16(stringPointer, checked(index * sizeof(char))) != 0)
            {
                continue;
            }

            value = Marshal.PtrToStringUni(stringPointer, index) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static void ReadMemoryItems(
        IReadOnlyList<PdhCounterValue<long>> items,
        Dictionary<int, ProcessDescriptor> targets,
        Dictionary<GpuMemoryInstanceId, ulong> values,
        HashSet<GpuMemoryInstanceId> instances,
        HashSet<ulong> adapters,
        HashSet<int> incompletePids,
        Dictionary<int, int?> parentByPid,
        Dictionary<int, int?> targetAncestorByPid,
        HashSet<int> observedCounterPids,
        ref int unattributedDescendantCounterInstances,
        ref int outsideApplicationSetCounterInstances,
        ref int exitedProcessCounterInstances,
        ref int unknownProcessCounterInstances,
        ref int duplicateCounterInstances,
        ref int malformedCounterInstances,
        ref int invalidCounterSamples)
    {
        foreach (PdhCounterValue<long> item in items)
        {
            if (!TryParseMemoryInstance(
                item.InstanceName,
                out int processId,
                out ulong adapterLuid,
                out int physicalAdapterIndex))
            {
                if (TryExtractRawPid(item.InstanceName, out int rawPid) && rawPid <= 0)
                {
                    unknownProcessCounterInstances++;
                }
                else
                {
                    malformedCounterInstances++;
                }

                invalidCounterSamples++;
                continue;
            }

            GpuMemoryInstanceId instance = new(
                processId,
                adapterLuid,
                physicalAdapterIndex);
            observedCounterPids.Add(processId);
            adapters.Add(instance.AdapterLuid);
            if (!targets.ContainsKey(instance.ProcessId))
            {
                if (TryFindTargetAncestor(
                    instance.ProcessId,
                    parentByPid,
                    targets,
                    targetAncestorByPid,
                    out int targetAncestor))
                {
                    incompletePids.Add(targetAncestor);
                    unattributedDescendantCounterInstances++;
                }
                else if (parentByPid.ContainsKey(processId))
                {
                    outsideApplicationSetCounterInstances++;
                }
                else
                {
                    exitedProcessCounterInstances++;
                }

                continue;
            }

            bool duplicate = !instances.Add(instance);
            if (duplicate)
            {
                duplicateCounterInstances++;
                incompletePids.Add(instance.ProcessId);
            }

            if (!IsValidCounterStatus(item.CounterStatus) || item.Value < 0)
            {
                incompletePids.Add(instance.ProcessId);
                invalidCounterSamples++;
                continue;
            }

            ulong value = checked((ulong)item.Value);
            if (values.TryGetValue(instance, out ulong existing))
            {
                values[instance] = Math.Max(existing, value);
            }
            else
            {
                values.Add(instance, value);
            }
        }
    }

    private static bool TryFindTargetAncestor(
        int processId,
        Dictionary<int, int?> parentByPid,
        Dictionary<int, ProcessDescriptor> targets,
        Dictionary<int, int?> targetAncestorByPid,
        out int targetAncestor)
    {
        const int maximumAncestryDepth = 32;
        if (targetAncestorByPid.TryGetValue(processId, out int? cached))
        {
            targetAncestor = cached.GetValueOrDefault();
            return cached.HasValue;
        }

        int current = processId;
        for (int depth = 0; depth < maximumAncestryDepth; depth++)
        {
            if (!parentByPid.TryGetValue(current, out int? parent)
                || parent is null
                || parent <= 0)
            {
                break;
            }

            current = parent.Value;
            if (targets.ContainsKey(current))
            {
                targetAncestor = current;
                targetAncestorByPid[processId] = current;
                return true;
            }
        }

        targetAncestor = 0;
        targetAncestorByPid[processId] = null;
        return false;
    }

    private static MetricValue<ulong> SumMemory(
        IReadOnlyList<GpuMemoryInstanceId> instances,
        IReadOnlyDictionary<GpuMemoryInstanceId, ulong> values,
        PdhFailure counterState,
        bool hadInvalidValue,
        string name)
    {
        if (counterState.Availability is not MetricAvailability.Available)
        {
            return MetricValue<ulong>.Unavailable(
                counterState.Availability,
                counterState.Detail);
        }

        ulong total = 0;
        bool complete = !hadInvalidValue;
        bool overflow = false;
        foreach (GpuMemoryInstanceId instance in instances)
        {
            if (!values.TryGetValue(instance, out ulong value))
            {
                complete = false;
                continue;
            }

            if (ulong.MaxValue - total < value)
            {
                total = ulong.MaxValue;
                overflow = true;
                complete = false;
                break;
            }

            total += value;
        }

        return complete
            ? MetricValue<ulong>.Available(total)
            : MetricValue<ulong>.Partial(
                total,
                overflow
                    ? $"The {name} sum overflowed the supported range; the displayed value is a lower bound."
                    : $"One or more {name} counter instances were invalid or missing; the displayed sum is incomplete.");
    }

    private static MetricValue<ulong> QuarantinedMemory(string name) =>
        MetricValue<ulong>.Unavailable(
            MetricAvailability.Unavailable,
            QuarantineDetail(name));

    private static string QuarantineDetail(string name) =>
        $"{name} is quarantined because its counter was already present when this process lifetime was first observed. A complete counter gap followed by a new appearance is required before attribution.";

    internal static MetricValue<ulong> SumMemoryForTesting(
        IReadOnlyList<GpuMemoryInstanceId> instances,
        IReadOnlyDictionary<GpuMemoryInstanceId, ulong> values,
        MetricAvailability availability,
        string? detail,
        bool hadInvalidValue,
        string name) =>
        SumMemory(
            instances,
            values,
            new PdhFailure(availability, GpuAvailabilityReason.None, detail),
            hadInvalidValue,
            name);

    private static ProcessIdentityCheck ValidateProcessIdentity(ProcessInstanceId expected)
    {
        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            expected.ProcessId);
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ErrorAccessDenied
                ? new ProcessIdentityCheck(
                    false,
                    MetricAvailability.AccessDenied,
                    GpuAvailabilityReason.AccessDenied,
                    "Windows denied access while validating the process start time.")
                : new ProcessIdentityCheck(
                    false,
                    MetricAvailability.Unavailable,
                    GpuAvailabilityReason.ProcessUnavailable,
                    "The process exited before GPU counter attribution completed.");
        }

        if (!GetProcessTimes(process, out FileTime creation, out _, out _, out _))
        {
            int error = Marshal.GetLastPInvokeError();
            return new ProcessIdentityCheck(
                false,
                error == ErrorAccessDenied
                    ? MetricAvailability.AccessDenied
                    : MetricAvailability.Unavailable,
                error == ErrorAccessDenied
                    ? GpuAvailabilityReason.AccessDenied
                    : GpuAvailabilityReason.ProcessUnavailable,
                "The process start time could not be validated after GPU sampling.");
        }

        DateTimeOffset actual;
        try
        {
            // GetProcessTimes returns an absolute Windows FILETIME; both sides are UTC.
            actual = DateTimeOffset.FromFileTime(creation.ToInt64()).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return new ProcessIdentityCheck(
                false,
                MetricAvailability.Error,
                GpuAvailabilityReason.CounterReadFailure,
                "Windows returned an invalid process start time.");
        }

        return actual == expected.StartTimeUtc
            ? new ProcessIdentityCheck(
                true,
                MetricAvailability.Available,
                GpuAvailabilityReason.None,
                null)
            : new ProcessIdentityCheck(
                false,
                MetricAvailability.Error,
                GpuAvailabilityReason.PidReused,
                "The process ID was reused before GPU counter attribution completed.");
    }

    internal static bool TryParseEngineInstance(
        string instanceName,
        out int processId,
        out GpuEngineId engineId)
    {
        if (!TryParseInstanceFields(instanceName, out Dictionary<string, string>? fields)
            || fields.Count != 5
            || !TryParsePositiveInt(fields, "pid", out processId)
            || !TryParseLuid(fields, out ulong luid)
            || !TryParseNonNegativeInt(fields, "phys", out int physical)
            || !TryParseNonNegativeInt(fields, "eng", out int engine)
            || !fields.TryGetValue("engtype", out string? engineType)
            || string.IsNullOrWhiteSpace(engineType))
        {
            processId = 0;
            engineId = default;
            return false;
        }

        engineId = new GpuEngineId(
            luid,
            physical,
            engine,
            NormalizeEngineType(engineType));
        return true;
    }

    internal static bool TryParseMemoryInstance(
        string instanceName,
        out int processId,
        out ulong adapterLuid,
        out int physicalAdapterIndex)
    {
        if (!TryParseInstanceFields(instanceName, out Dictionary<string, string>? fields)
            || fields.Count != 3
            || !TryParsePositiveInt(fields, "pid", out processId)
            || !TryParseLuid(fields, out adapterLuid)
            || !TryParseNonNegativeInt(fields, "phys", out physicalAdapterIndex))
        {
            processId = 0;
            adapterLuid = 0;
            physicalAdapterIndex = 0;
            return false;
        }

        return true;
    }

    private static bool TryParseInstanceFields(
        string instanceName,
        out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        string normalized = DuplicateInstanceSuffixPattern().Replace(
            instanceName.Trim(),
            string.Empty);
        MatchCollection matches = InstanceFieldPattern().Matches(normalized);
        if (matches.Count == 0)
        {
            return false;
        }

        int consumed = 0;
        foreach (Match match in matches)
        {
            if (match.Index != consumed)
            {
                return false;
            }

            string key = match.Groups["key"].Value;
            string value = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(value)
                || !fields.TryAdd(key, value))
            {
                return false;
            }

            consumed = match.Index + match.Length;
        }

        return consumed == normalized.Length;
    }

    private static bool TryParsePositiveInt(
        Dictionary<string, string> fields,
        string key,
        out int value)
    {
        value = 0;
        return fields.TryGetValue(key, out string? text)
            && int.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value)
            && value > 0;
    }

    private static bool TryParseNonNegativeInt(
        Dictionary<string, string> fields,
        string key,
        out int value)
    {
        value = 0;
        return fields.TryGetValue(key, out string? text)
            && int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value)
            && value >= 0;
    }

    private static bool TryParseLuid(
        Dictionary<string, string> fields,
        out ulong luid)
    {
        luid = 0;
        if (!fields.TryGetValue("luid", out string? text))
        {
            return false;
        }

        Match match = LuidPattern().Match(text);
        if (!match.Success
            || !uint.TryParse(
                match.Groups["high"].Value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint high)
            || !uint.TryParse(
                match.Groups["low"].Value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint low))
        {
            return false;
        }

        luid = ((ulong)high << 32) | low;
        return true;
    }

    private static string NormalizeEngineType(string engineType)
    {
        string compact = engineType.Trim();
        string[] knownTypes =
        [
            "3D",
            "Compute",
            "Copy",
            "Crypto",
            "Cuda",
            "GDI Render",
            "Graphics",
            "Overlay",
            "SceneAssembly",
            "Security",
            "VideoDecode",
            "VideoEncode",
            "VideoProcessing"
        ];
        return knownTypes.FirstOrDefault(
            known => string.Equals(known, compact, StringComparison.OrdinalIgnoreCase))
            ?? "Unknown";
    }

    private static bool TryExtractRawPid(string instanceName, out int processId)
    {
        processId = 0;
        Match match = PidFieldPattern().Match(instanceName);
        return match.Success
            && int.TryParse(
                match.Groups["pid"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out processId);
    }

    private static bool IsValidCounterStatus(uint status) =>
        status is 0 or 1;

    private static bool IsRecoverableCaptureException(Exception exception) =>
        exception is ArgumentException
            or ArithmeticException
            or ExternalException
            or InvalidOperationException
            or RegexMatchTimeoutException;

    private static PdhFailure ClassifyCounterResult(uint status, string counterName) =>
        status switch
        {
            ErrorSuccess => new(
                MetricAvailability.Available,
                GpuAvailabilityReason.None,
                null),
            PdhNoData => new(
                MetricAvailability.Unavailable,
                GpuAvailabilityReason.CounterUnavailable,
                $"Windows returned no {counterName} counter data for this interval."),
            PdhRetry or PdhMoreData => new(
                MetricAvailability.Unavailable,
                GpuAvailabilityReason.ProviderUnavailable,
                $"Windows requested a retry while reading {counterName} counters (0x{status:X8})."),
            _ => ClassifyPdhFailure(status)
        };

    private static PdhFailure ClassifyPdhFailure(uint status) => status switch
    {
        ErrorAccessDenied or PdhAccessDenied => new(
            MetricAvailability.AccessDenied,
            GpuAvailabilityReason.AccessDenied,
            $"Windows denied access to GPU performance counters (0x{status:X8})."),
        PdhNoObject or PdhNoCounter => new(
            MetricAvailability.Unsupported,
            GpuAvailabilityReason.CounterSetUnavailable,
            $"The required GPU performance counter is unavailable (0x{status:X8}). A WDDM 2.x driver is required."),
        ErrorNotEnoughMemory => new(
            MetricAvailability.Error,
            GpuAvailabilityReason.InvalidData,
            "The GPU counter buffer exceeded its bounded safety limit or contained an invalid item count."),
        _ => new(
            MetricAvailability.Error,
            GpuAvailabilityReason.CounterReadFailure,
            $"GPU performance-counter sampling failed (0x{status:X8}).")
    };

    private static MetricAvailability CombineAvailability(
        MetricAvailability[] states)
    {
        if (states.Length == 0 || states.All(state => state == MetricAvailability.Available))
        {
            return MetricAvailability.Available;
        }

        if (states.Any(state => state is MetricAvailability.Available or MetricAvailability.Partial))
        {
            return MetricAvailability.Partial;
        }

        MetricAvailability[] priority =
        [
            MetricAvailability.AccessDenied,
            MetricAvailability.Unsupported,
            MetricAvailability.Error,
            MetricAvailability.WarmingUp,
            MetricAvailability.Unavailable
        ];
        return priority.FirstOrDefault(states.Contains, MetricAvailability.Unavailable);
    }

    private static GpuAvailabilityReason FirstReason(
        params GpuAvailabilityReason[] reasons) =>
        reasons.FirstOrDefault(
            reason => reason != GpuAvailabilityReason.None,
            GpuAvailabilityReason.CounterReadFailure);

    private static GpuCounterBatch UnavailableBatch(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        uint status,
        double durationMilliseconds)
    {
        PdhFailure failure = ClassifyPdhFailure(status);
        return UnavailableBatch(
            processes,
            capturedAtUtc,
            failure.Availability,
            failure.Reason,
            failure.Detail,
            durationMilliseconds);
    }

    private static GpuCounterBatch UnavailableBatch(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        MetricAvailability availability,
        GpuAvailabilityReason reason,
        string? detail,
        double durationMilliseconds)
    {
        GpuProcessCounterSnapshot[] snapshots = processes
            .Select(process => UnavailableSnapshot(
                process.InstanceId,
                capturedAtUtc,
                availability,
                detail))
            .ToArray();
        GpuCollectorDiagnostics diagnostics = Diagnostics(
            availability,
            reason,
            detail,
            processes.Count,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            durationMilliseconds);
        return new GpuCounterBatch(snapshots, availability, reason, diagnostics);
    }

    private static GpuProcessCounterSnapshot UnavailableSnapshot(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        MetricAvailability availability,
        string? detail) => new(
        process,
        capturedAtUtc,
        [],
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        availability,
        detail);

    private static GpuCollectorDiagnostics Diagnostics(
        MetricAvailability status,
        GpuAvailabilityReason reason,
        string? detail,
        int targetProcessCount,
        int sampledProcessCount,
        int engineInstanceCount,
        int memoryInstanceCount,
        int adapterCount,
        int pidReuseRejected,
        int inaccessibleProcessSamples,
        int unattributedDescendantCounterInstances,
        int invalidCounterSamples,
        double durationMilliseconds,
        int outsideApplicationSetCounterInstances = 0,
        int exitedProcessCounterInstances = 0,
        int unknownProcessCounterInstances = 0,
        int malformedCounterInstances = 0,
        int duplicateCounterInstances = 0,
        int queryRecreationCount = 0,
        MetricAvailability? utilizationCounterStatus = null,
        MetricAvailability? dedicatedMemoryCounterStatus = null,
        MetricAvailability? sharedMemoryCounterStatus = null,
        int firstObservationCounterSamplesRejected = 0,
        int quarantinedUtilizationSamples = 0,
        int quarantinedDedicatedMemorySamples = 0,
        int quarantinedSharedMemorySamples = 0) => new()
    {
        ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
        CollectorStatus = status,
        Reason = reason,
        CollectorStatusReason = detail,
        TargetProcessCount = targetProcessCount,
        SampledProcessCount = sampledProcessCount,
        EngineCounterInstanceCount = engineInstanceCount,
        ProcessMemoryCounterInstanceCount = memoryInstanceCount,
        ActiveAdapterCount = adapterCount,
        PidReuseSamplesRejected = pidReuseRejected,
        FirstObservationCounterSamplesRejected =
            firstObservationCounterSamplesRejected,
        QuarantinedUtilizationSamples = quarantinedUtilizationSamples,
        QuarantinedDedicatedMemorySamples = quarantinedDedicatedMemorySamples,
        QuarantinedSharedMemorySamples = quarantinedSharedMemorySamples,
        InaccessibleProcessSamples = inaccessibleProcessSamples,
        UnattributedDescendantCounterInstances = unattributedDescendantCounterInstances,
        OutsideApplicationSetCounterInstances = outsideApplicationSetCounterInstances,
        ExitedProcessCounterInstances = exitedProcessCounterInstances,
        UnknownProcessCounterInstances = unknownProcessCounterInstances,
        MalformedCounterInstances = malformedCounterInstances,
        DuplicateCounterInstances = duplicateCounterInstances,
        InvalidCounterSamples = invalidCounterSamples,
        QueryRecreationCount = queryRecreationCount,
        UtilizationCounterStatus = utilizationCounterStatus ?? status,
        DedicatedMemoryCounterStatus = dedicatedMemoryCounterStatus ?? status,
        SharedMemoryCounterStatus = sharedMemoryCounterStatus ?? status,
        CollectionDurationMilliseconds = durationMilliseconds,
        SharedMemoryMayDoubleCountAcrossProcesses = true
    };

    [GeneratedRegex(
        @"(?:^|_)(?<key>pid|luid|phys|eng|engtype)_(?<value>.*?)(?=_(?:pid|luid|phys|eng|engtype)_|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex InstanceFieldPattern();

    [GeneratedRegex(
        @"^0x(?<high>[0-9a-f]+)_0x(?<low>[0-9a-f]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex LuidPattern();

    [GeneratedRegex(
        @"#\d+$",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex DuplicateInstanceSuffixPattern();

    [GeneratedRegex(
        @"(?:^|_)pid_(?<pid>\d+)(?=_|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex PidFieldPattern();

    internal readonly record struct GpuMemoryInstanceId(
        int ProcessId,
        ulong AdapterLuid,
        int PhysicalAdapterIndex);

    private readonly record struct ProcessIdentityCheck(
        bool IsMatch,
        MetricAvailability Availability,
        GpuAvailabilityReason Reason,
        string? Detail);

    private readonly record struct PdhFailure(
        MetricAvailability Availability,
        GpuAvailabilityReason Reason,
        string? Detail);

    private readonly record struct CounterArrayResult<T>(
        uint Status,
        IReadOnlyList<PdhCounterValue<T>> Items,
        int InvalidNativeItems);

    private readonly record struct PdhCounterValue<T>(
        string InstanceName,
        T Value,
        uint CounterStatus);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PdhFormattedCounterValueItemDouble
    {
        public readonly nint InstanceName;
        public readonly PdhFormattedCounterValueDouble Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PdhFormattedCounterValueDouble
    {
        public readonly uint CounterStatus;
        public readonly double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PdhFormattedCounterValueItemLong
    {
        public readonly nint InstanceName;
        public readonly PdhFormattedCounterValueLong Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PdhFormattedCounterValueLong
    {
        public readonly uint CounterStatus;
        public readonly long Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public long ToInt64() => unchecked((long)(((ulong)_highDateTime << 32) | _lowDateTime));
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(
        string? dataSource,
        nuint userData,
        out nint query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(
        nint query,
        string fullCounterPath,
        nuint userData,
        out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArrayDouble(
        nint counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        nint itemBuffer);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArrayLong(
        nint counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        nint itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);
}

internal sealed class GpuProcessLifetimeTracker
{
    internal const int MaximumTrackedProcessLifetimes = 32_768;

    private readonly Dictionary<int, TrackedLifetime> _tracked = [];
    private long _captureSequence;

    internal int TrackedProcessCount => _tracked.Count;

    internal GpuProcessLifetimeTrustResult Update(
        IEnumerable<ProcessInstanceId> targets,
        HashSet<int> currentCounterPids,
        bool counterEnumerationComplete = true)
    {
        _captureSequence++;
        ProcessInstanceId[] currentTargets = targets
            .GroupBy(target => target.ProcessId)
            .Select(group => group.First())
            .ToArray();
        HashSet<int> currentTargetPids = currentTargets
            .Select(target => target.ProcessId)
            .ToHashSet();

        foreach (ProcessInstanceId target in currentTargets)
        {
            bool hasCounter = currentCounterPids.Contains(target.ProcessId);
            TrackedLifetime current;
            if (!_tracked.TryGetValue(target.ProcessId, out TrackedLifetime previous))
            {
                current = NewLifetime(
                    target,
                    hasCounter,
                    counterEnumerationComplete,
                    QuarantineOrigin.FirstObservation);
            }
            else if (previous.Instance != target)
            {
                current = NewLifetime(
                    target,
                    hasCounter,
                    counterEnumerationComplete,
                    QuarantineOrigin.PidReuse);
            }
            else
            {
                current = Advance(
                    previous,
                    hasCounter,
                    counterEnumerationComplete);
            }

            _tracked[target.ProcessId] = current with
            {
                LastSeenCapture = _captureSequence
            };
        }

        Prune(currentTargetPids);
        TrackedLifetime[] quarantined = _tracked.Values
            .Where(item => currentTargetPids.Contains(item.Instance.ProcessId)
                && item.State == CounterTrustState.Quarantined)
            .ToArray();
        return new GpuProcessLifetimeTrustResult(
            quarantined.Select(item => item.Instance.ProcessId).ToHashSet(),
            quarantined
                .Where(item => item.Origin == QuarantineOrigin.PidReuse)
                .Select(item => item.Instance.ProcessId)
                .ToHashSet(),
            quarantined
                .Where(item => item.Origin == QuarantineOrigin.FirstObservation)
                .Select(item => item.Instance.ProcessId)
                .ToHashSet());
    }

    internal void ResetForProviderRestart()
    {
        foreach (int processId in _tracked.Keys.ToArray())
        {
            TrackedLifetime previous = _tracked[processId];
            _tracked[processId] = previous with
            {
                State = CounterTrustState.Unproven,
                Origin = QuarantineOrigin.None
            };
        }
    }

    private TrackedLifetime NewLifetime(
        ProcessInstanceId instance,
        bool hasCounter,
        bool counterEnumerationComplete,
        QuarantineOrigin counterPresentOrigin) =>
        new(
            instance,
            hasCounter
                ? CounterTrustState.Quarantined
                : counterEnumerationComplete
                    ? CounterTrustState.AwaitingCounter
                    : CounterTrustState.Unproven,
            hasCounter ? counterPresentOrigin : QuarantineOrigin.None,
            _captureSequence);

    private static TrackedLifetime Advance(
        TrackedLifetime previous,
        bool hasCounter,
        bool counterEnumerationComplete)
    {
        return previous.State switch
        {
            CounterTrustState.Unproven when hasCounter => previous with
            {
                State = CounterTrustState.Quarantined,
                Origin = QuarantineOrigin.FirstObservation
            },
            CounterTrustState.Unproven when counterEnumerationComplete =>
                previous with
                {
                    State = CounterTrustState.AwaitingCounter,
                    Origin = QuarantineOrigin.None
                },
            CounterTrustState.AwaitingCounter when hasCounter => previous with
            {
                State = CounterTrustState.Trusted,
                Origin = QuarantineOrigin.None
            },
            CounterTrustState.Quarantined
                when counterEnumerationComplete && !hasCounter => previous with
                {
                    State = CounterTrustState.AwaitingCounter,
                    Origin = QuarantineOrigin.None
                },
            _ => previous
        };
    }

    private void Prune(HashSet<int> currentTargetPids)
    {
        if (_tracked.Count <= MaximumTrackedProcessLifetimes)
        {
            return;
        }

        int excess = _tracked.Count - MaximumTrackedProcessLifetimes;
        int[] removable = _tracked
                     .Where(pair => !currentTargetPids.Contains(pair.Key))
                     .OrderBy(pair => pair.Value.LastSeenCapture)
                     .Select(pair => pair.Key)
                     .Take(excess)
                     .ToArray();
        foreach (int processId in removable)
        {
            _tracked.Remove(processId);
        }

        excess = _tracked.Count - MaximumTrackedProcessLifetimes;
        foreach (int processId in _tracked
                     .OrderBy(pair => pair.Value.LastSeenCapture)
                     .Select(pair => pair.Key)
                     .Take(Math.Max(0, excess))
                     .ToArray())
        {
            _tracked.Remove(processId);
        }
    }

    private readonly record struct TrackedLifetime(
        ProcessInstanceId Instance,
        CounterTrustState State,
        QuarantineOrigin Origin,
        long LastSeenCapture);

    private enum CounterTrustState
    {
        Unproven,
        AwaitingCounter,
        Trusted,
        Quarantined
    }

    private enum QuarantineOrigin
    {
        None,
        FirstObservation,
        PidReuse
    }
}

internal sealed record GpuProcessLifetimeTrustResult(
    HashSet<int> QuarantinedPids,
    HashSet<int> PidReusePids,
    HashSet<int> FirstObservationPids);
