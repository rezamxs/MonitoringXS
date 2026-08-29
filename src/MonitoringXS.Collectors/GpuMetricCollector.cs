using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class GpuMetricCollector : IGpuMetricCollector
{
    private readonly IGpuCounterSource _counterSource;

    public GpuMetricCollector(IGpuCounterSource counterSource)
    {
        _counterSource = counterSource;
    }

    public GpuCounterBatch? LastBatch { get; private set; }

    public async ValueTask<IReadOnlyList<GpuProcessSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        GpuCounterBatch batch = await _counterSource.CaptureAsync(
            processes,
            capturedAtUtc.ToUniversalTime(),
            cancellationToken);
        LastBatch = batch;
        Dictionary<ProcessInstanceId, GpuProcessCounterSnapshot> byProcess =
            batch.Processes
                .GroupBy(snapshot => snapshot.Process)
                .ToDictionary(group => group.Key, group => group.First());
        List<GpuProcessSample> samples = new(processes.Count);

        foreach (ProcessDescriptor descriptor in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byProcess.TryGetValue(
                    descriptor.InstanceId,
                    out GpuProcessCounterSnapshot? snapshot))
            {
                string detail = batch.Diagnostics.CollectorStatusReason
                    ?? "The GPU provider did not return a sample for this process instance.";
                MetricAvailability availability = batch.Availability is
                    MetricAvailability.Available or MetricAvailability.Partial
                    ? MetricAvailability.Error
                    : batch.Availability;
                samples.Add(Unavailable(
                    descriptor.InstanceId,
                    capturedAtUtc,
                    availability,
                    detail,
                    batch.Diagnostics));
                continue;
            }

            GpuEngineUsage[] engines = NormalizeEngines(
                snapshot.Engines,
                out bool invalidEngineValue,
                out bool duplicateEngineIdentity);
            samples.Add(new GpuProcessSample(
                snapshot.Process,
                snapshot.CapturedAtUtc,
                Utilization(
                    snapshot,
                    engines,
                    invalidEngineValue,
                    duplicateEngineIdentity),
                snapshot.DedicatedMemoryBytes,
                snapshot.SharedMemoryBytes,
                engines,
                batch.Diagnostics));
        }

        return samples;
    }

    private static MetricValue<double> Utilization(
        GpuProcessCounterSnapshot snapshot,
        GpuEngineUsage[] normalized,
        bool invalidValue,
        bool duplicateIdentity)
    {
        if (snapshot.EngineAvailability is not (MetricAvailability.Available or MetricAvailability.Partial))
        {
            return MetricValue<double>.Unavailable(
                snapshot.EngineAvailability,
                snapshot.EngineDetail);
        }

        if (normalized.Length == 0 && snapshot.Engines.Count > 0)
        {
            return MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "Windows returned an invalid GPU engine value.");
        }

        double busiestEngine = normalized
            .Select(engine => engine.UtilizationPercent)
            .DefaultIfEmpty()
            .Max();
        bool complete = snapshot.EngineAvailability == MetricAvailability.Available
            && !invalidValue
            && !duplicateIdentity;
        return complete
            ? MetricValue<double>.Available(busiestEngine)
            : MetricValue<double>.Partial(
                busiestEngine,
                duplicateIdentity
                    ? "A duplicate GPU engine instance was ignored; utilization may be incomplete."
                    : invalidValue
                        ? "An invalid GPU engine value was ignored; utilization is a lower bound."
                        : snapshot.EngineDetail
                            ?? "Some GPU engine counters were unavailable; utilization is a lower bound.");
    }

    private static GpuEngineUsage[] NormalizeEngines(
        IReadOnlyList<GpuEngineUsage> engines,
        out bool invalidValue,
        out bool duplicateIdentity)
    {
        GpuEngineUsage[] valid = engines
            .Where(engine => double.IsFinite(engine.UtilizationPercent)
                && engine.UtilizationPercent >= 0d
                && engine.UtilizationPercent <= 100d)
            .ToArray();
        invalidValue = valid.Length != engines.Count;
        IGrouping<GpuEngineId, GpuEngineUsage>[] groups = valid
            .GroupBy(engine => engine.Engine)
            .ToArray();
        duplicateIdentity = groups.Any(group => group.Count() > 1);
        return groups
            .Select(group => new GpuEngineUsage(
                group.Key,
                group.Max(item => item.UtilizationPercent)))
            .ToArray();
    }

    private static GpuProcessSample Unavailable(
        ProcessInstanceId process,
        DateTimeOffset capturedAtUtc,
        MetricAvailability availability,
        string detail,
        GpuCollectorDiagnostics diagnostics) => new(
        process,
        capturedAtUtc.ToUniversalTime(),
        MetricValue<double>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        MetricValue<ulong>.Unavailable(availability, detail),
        [],
        diagnostics);
}
