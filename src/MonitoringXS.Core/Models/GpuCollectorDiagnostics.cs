namespace MonitoringXS.Core.Models;

public readonly record struct GpuCollectorDiagnostics
{
    public const string WindowsPdhProvider = "Windows PDH GPU performance counters";

    public string ProviderName { get; init; }

    public MetricAvailability CollectorStatus { get; init; }

    public GpuAvailabilityReason Reason { get; init; }

    public string? CollectorStatusReason { get; init; }

    public int TargetProcessCount { get; init; }

    public int SampledProcessCount { get; init; }

    public int EngineCounterInstanceCount { get; init; }

    public int ProcessMemoryCounterInstanceCount { get; init; }

    public int ActiveAdapterCount { get; init; }

    public int PidReuseSamplesRejected { get; init; }

    public int FirstObservationCounterSamplesRejected { get; init; }

    public int QuarantinedUtilizationSamples { get; init; }

    public int QuarantinedDedicatedMemorySamples { get; init; }

    public int QuarantinedSharedMemorySamples { get; init; }

    public int InaccessibleProcessSamples { get; init; }

    public int UnattributedDescendantCounterInstances { get; init; }

    public int OutsideApplicationSetCounterInstances { get; init; }

    public int ExitedProcessCounterInstances { get; init; }

    public int UnknownProcessCounterInstances { get; init; }

    public int MalformedCounterInstances { get; init; }

    public int DuplicateCounterInstances { get; init; }

    public int InvalidCounterSamples { get; init; }

    public int QueryRecreationCount { get; init; }

    public MetricAvailability UtilizationCounterStatus { get; init; }

    public MetricAvailability DedicatedMemoryCounterStatus { get; init; }

    public MetricAvailability SharedMemoryCounterStatus { get; init; }

    public double CollectionDurationMilliseconds { get; init; }

    // Windows per-process video-memory counters include cross-process shared allocations.
    public bool SharedMemoryMayDoubleCountAcrossProcesses { get; init; }
}
