using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors;

public sealed class ProcessMetricCollector : IProcessMetricCollector
{
    private readonly IProcessResourceCounterReader _counterReader;
    private readonly Dictionary<ProcessInstanceId, CpuState> _cpuStates = [];
    private readonly Dictionary<ProcessInstanceId, IoState> _ioStates = [];

    public ProcessMetricCollector(IProcessResourceCounterReader counterReader)
    {
        _counterReader = counterReader;
    }

    public ValueTask<IReadOnlyList<ProcessMetricSample>> CollectAsync(
        IReadOnlyList<ProcessDescriptor> processes,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        List<ProcessMetricSample> samples = new(processes.Count);
        HashSet<ProcessInstanceId> live = [];

        foreach (ProcessDescriptor descriptor in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            live.Add(descriptor.InstanceId);
            samples.Add(CollectOne(descriptor, capturedAt));
        }

        foreach (ProcessInstanceId stale in _cpuStates.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            _cpuStates.Remove(stale);
        }

        foreach (ProcessInstanceId stale in _ioStates.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            _ioStates.Remove(stale);
        }

        return ValueTask.FromResult<IReadOnlyList<ProcessMetricSample>>(samples);
    }

    private ProcessMetricSample CollectOne(ProcessDescriptor descriptor, DateTimeOffset capturedAt)
    {
        MetricValue<ProcessResourceCounters> resource = _counterReader.Read(descriptor.InstanceId);
        if (!resource.IsAvailable)
        {
            _cpuStates.Remove(descriptor.InstanceId);
            _ioStates.Remove(descriptor.InstanceId);
            return Unavailable(
                descriptor.InstanceId,
                capturedAt,
                resource.Availability,
                resource.Detail ?? "Process resource counters are unavailable.");
        }

        ProcessResourceCounters counters = resource.Value!.Value;
        MetricValue<double> cpu = CalculateCpu(
            descriptor.InstanceId,
            capturedAt,
            counters.TotalProcessorTime);
        (MetricValue<double> ioReadRate, MetricValue<double> ioWriteRate) =
            CalculateIoRates(descriptor.InstanceId, capturedAt, counters.IoCounters);

        return new ProcessMetricSample(
            descriptor.InstanceId,
            capturedAt,
            cpu,
            MetricValue<long>.Available(Math.Max(0, counters.WorkingSetBytes)),
            ioReadRate,
            ioWriteRate,
            SelectIoCounter(counters.IoCounters, value => value.ReadBytes),
            SelectIoCounter(counters.IoCounters, value => value.WriteBytes),
            SelectIoCounter(counters.IoCounters, value => value.ReadOperationCount),
            SelectIoCounter(counters.IoCounters, value => value.WriteOperationCount));
    }

    private MetricValue<double> CalculateCpu(ProcessInstanceId process, DateTimeOffset capturedAt, TimeSpan totalCpu)
    {
        if (!_cpuStates.TryGetValue(process, out CpuState previous))
        {
            _cpuStates[process] = new CpuState(capturedAt, totalCpu);
            return MetricValue<double>.Unavailable(MetricAvailability.WarmingUp, "A second sample is required for a CPU delta.");
        }

        _cpuStates[process] = new CpuState(capturedAt, totalCpu);
        double elapsedMilliseconds = (capturedAt - previous.CapturedAt).TotalMilliseconds;
        double cpuMilliseconds = (totalCpu - previous.TotalCpu).TotalMilliseconds;
        if (elapsedMilliseconds <= 0 || cpuMilliseconds < 0)
        {
            return MetricValue<double>.Unavailable(MetricAvailability.Error, "Invalid sampling interval.");
        }

        double normalized = cpuMilliseconds / (elapsedMilliseconds * Environment.ProcessorCount) * 100d;
        return MetricValue<double>.Available(Math.Clamp(normalized, 0d, 100d));
    }

    private (MetricValue<double> Read, MetricValue<double> Write) CalculateIoRates(
        ProcessInstanceId process,
        DateTimeOffset capturedAt,
        MetricValue<ProcessIoCounters> current)
    {
        if (!current.IsAvailable)
        {
            _ioStates.Remove(process);
            MetricValue<double> unavailable = MetricValue<double>.Unavailable(current.Availability, current.Detail);
            return (unavailable, unavailable);
        }

        ProcessIoCounters counters = current.Value!.Value;
        if (!_ioStates.TryGetValue(process, out IoState previous))
        {
            _ioStates[process] = new IoState(capturedAt, counters);
            MetricValue<double> warmingUp = MetricValue<double>.Unavailable(
                MetricAvailability.WarmingUp,
                "A second sample is required for an I/O rate.");
            return (warmingUp, warmingUp);
        }

        _ioStates[process] = new IoState(capturedAt, counters);
        double elapsedSeconds = (capturedAt - previous.CapturedAt).TotalSeconds;
        if (elapsedSeconds <= 0 || counters.ReadBytes < previous.Counters.ReadBytes || counters.WriteBytes < previous.Counters.WriteBytes)
        {
            MetricValue<double> invalid = MetricValue<double>.Unavailable(
                MetricAvailability.Error,
                "Invalid process I/O sampling interval or counter reset.");
            return (invalid, invalid);
        }

        double readRate = (counters.ReadBytes - previous.Counters.ReadBytes) / elapsedSeconds;
        double writeRate = (counters.WriteBytes - previous.Counters.WriteBytes) / elapsedSeconds;
        return (MetricValue<double>.Available(readRate), MetricValue<double>.Available(writeRate));
    }

    private static MetricValue<ulong> SelectIoCounter(
        MetricValue<ProcessIoCounters> counters,
        Func<ProcessIoCounters, ulong> selector) => counters.IsAvailable
        ? counters.IsComplete
            ? MetricValue<ulong>.Available(selector(counters.Value!.Value))
            : MetricValue<ulong>.Partial(selector(counters.Value!.Value), counters.Detail ?? "I/O counter is incomplete.")
        : MetricValue<ulong>.Unavailable(counters.Availability, counters.Detail);

    private static ProcessMetricSample Unavailable(
        ProcessInstanceId process,
        DateTimeOffset capturedAt,
        MetricAvailability availability,
        string detail) => new(
            process,
            capturedAt,
            MetricValue<double>.Unavailable(availability, detail),
            MetricValue<long>.Unavailable(availability, detail),
            MetricValue<double>.Unavailable(availability, detail),
            MetricValue<double>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail),
            MetricValue<ulong>.Unavailable(availability, detail));

    private readonly record struct CpuState(DateTimeOffset CapturedAt, TimeSpan TotalCpu);

    private readonly record struct IoState(DateTimeOffset CapturedAt, ProcessIoCounters Counters);
}
