using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using MonitoringXS.Collectors;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Metrics;

if (args.Length > 0 && string.Equals(args[0], "etw-raw", StringComparison.OrdinalIgnoreCase))
{
    string sessionName = $"MonitoringXS.PhysicalDisk.Raw.{Environment.ProcessId}";
    using TraceEventSession rawSession = new(
        sessionName,
        TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
    {
        StopOnDispose = true
    };
    rawSession.EnableKernelProvider(
        KernelTraceEventParser.Keywords.DiskIO |
        KernelTraceEventParser.Keywords.Thread);
    ETWTraceEventSource rawSource = rawSession.Source;
    KernelTraceEventParser rawParser = new(rawSource, KernelTraceEventParser.ParserTrackingOptions.None);
    int rawCount = 0;
    void Dump(DiskIOTraceData data)
    {
        if (Interlocked.Increment(ref rawCount) > 12)
        {
            rawSource.StopProcessing();
            return;
        }

        string payload = string.Join(",", data.PayloadNames.Select(
            (name, index) => $"{name}={data.PayloadValue(index)}"));
        Console.WriteLine($"event={data.EventName};version={data.Version};thread={data.ThreadID};process={data.ProcessID};payload={payload}");
    }
    rawParser.DiskIORead += Dump;
    rawParser.DiskIOWrite += Dump;
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
    using CancellationTokenRegistration registration = timeout.Token.Register(rawSource.StopProcessing);
    rawSource.Process();
    Console.WriteLine($"events_dumped={Math.Min(rawCount, 12)}");
    return;
}

if (args.Length > 0 && string.Equals(args[0], "etw-probe", StringComparison.OrdinalIgnoreCase))
{
    await using EtwPhysicalDiskEventSource probeSource = new();
    PhysicalDiskEventBatch first = await probeSource.ReadBatchAsync([], CancellationToken.None);
    await Task.Delay(TimeSpan.FromSeconds(2));
    PhysicalDiskEventBatch second = await probeSource.ReadBatchAsync([], CancellationToken.None);
    Console.WriteLine($"first_availability={first.Availability}");
    Console.WriteLine($"first_detail={first.Detail}");
    Console.WriteLine($"second_availability={second.Availability}");
    Console.WriteLine($"second_detail={second.Detail}");
    Console.WriteLine($"events_observed={second.EventsObserved}");
    Console.WriteLine($"maximum_queue_depth={second.MaximumQueueDepth}");
    Console.WriteLine($"events_dropped={second.QueueEventsDropped}");
    Console.WriteLine($"events_lost={second.EtwEventsLost}");
    return;
}

if (args.Length > 0 && string.Equals(args[0], "disk-smoke", StringComparison.OrdinalIgnoreCase))
{
    Console.Title = "MonitoringXS controlled physical-disk workload";
    string outputDirectory = args.Length > 1
        ? Path.GetFullPath(args[1])
        : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".artifacts", "DiskSmoke"));
    int durationSeconds = args.Length > 2 && int.TryParse(args[2], out int requestedDuration)
        ? Math.Clamp(requestedDuration, 0, 60)
        : 0;
    Directory.CreateDirectory(outputDirectory);
    string outputPath = Path.Combine(outputDirectory, $"monitoringxs-disk-smoke-{Guid.NewGuid():N}.bin");
    int bytesPerIteration = durationSeconds > 0 ? 4 * 1024 * 1024 : 16 * 1024 * 1024;
    byte[] buffer = new byte[1024 * 1024];
    Random.Shared.NextBytes(buffer);
    TimeSpan writeElapsed = TimeSpan.Zero;
    TimeSpan readElapsed = TimeSpan.Zero;
    long bytesWritten = 0;
    long bytesRead = 0;
    int iterations = 0;
    Stopwatch total = Stopwatch.StartNew();
    do
    {
        Stopwatch write = Stopwatch.StartNew();
        await using (FileStream stream = new(
            outputPath,
            iterations == 0 ? FileMode.CreateNew : FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            stream.Position = 0;
            for (int written = 0; written < bytesPerIteration; written += buffer.Length)
            {
                await stream.WriteAsync(buffer);
            }

            stream.SetLength(bytesPerIteration);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }

        write.Stop();
        writeElapsed += write.Elapsed;
        bytesWritten += bytesPerIteration;

        Stopwatch read = Stopwatch.StartNew();
        await using (FileStream stream = new(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            int count;
            while ((count = await stream.ReadAsync(buffer)) > 0)
            {
                bytesRead += count;
            }
        }

        read.Stop();
        readElapsed += read.Elapsed;
        iterations++;
        if (durationSeconds > 0)
        {
            await Task.Delay(500);
        }
    }
    while (durationSeconds > 0 && total.Elapsed < TimeSpan.FromSeconds(durationSeconds));

    total.Stop();
    Console.WriteLine($"path={outputPath}");
    Console.WriteLine($"iterations={iterations}");
    Console.WriteLine($"written_bytes={bytesWritten}");
    Console.WriteLine($"read_bytes={bytesRead}");
    Console.WriteLine(FormattableString.Invariant($"write_elapsed_ms={writeElapsed.TotalMilliseconds:F2}"));
    Console.WriteLine(FormattableString.Invariant($"read_elapsed_ms={readElapsed.TotalMilliseconds:F2}"));
    Console.WriteLine(FormattableString.Invariant($"total_elapsed_ms={total.Elapsed.TotalMilliseconds:F2}"));
    Console.WriteLine("artifact_retained=true");
    return;
}

const int processCount = 200;
const int eventCount = 10_000;
DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
ProcessDescriptor[] processes = Enumerable.Range(1, processCount)
    .Select(index => new ProcessDescriptor(
        new ProcessInstanceId(10_000 + index, start),
        $"benchmark-{index}",
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        true))
    .ToArray();
PhysicalDiskIoEvent[] events = Enumerable.Range(0, eventCount)
    .Select(index => new PhysicalDiskIoEvent(
        processes[index % processCount].InstanceId.ProcessId,
        index % 1024,
        start.AddSeconds(1).AddTicks(index),
        index % 2 == 0 ? PhysicalDiskOperation.Read : PhysicalDiskOperation.Write,
        4096))
    .ToArray();
BenchmarkEventSource source = new(
    new PhysicalDiskEventBatch([], MetricAvailability.Available, 0, 0, 0),
    new PhysicalDiskEventBatch(events, MetricAvailability.Available, 0, 0, 0));
PhysicalDiskMetricCollector collector = new(source);
await collector.CollectAsync(processes, start.AddSeconds(1), CancellationToken.None);
Stopwatch aggregation = Stopwatch.StartNew();
IReadOnlyList<PhysicalDiskProcessSample> samples = await collector.CollectAsync(
    processes,
    start.AddSeconds(2),
    CancellationToken.None);
aggregation.Stop();

Console.WriteLine($"processes={processCount}");
Console.WriteLine($"events={eventCount}");
Console.WriteLine($"elapsed_ms={aggregation.Elapsed.TotalMilliseconds:F2}");
Console.WriteLine($"attributed_read_bytes={samples.Aggregate(0UL, (total, sample) => total + (sample.SessionReadBytes.Value ?? 0))}");
Console.WriteLine($"attributed_write_bytes={samples.Aggregate(0UL, (total, sample) => total + (sample.SessionWriteBytes.Value ?? 0))}");

file sealed class BenchmarkEventSource(params PhysicalDiskEventBatch[] batches) : IPhysicalDiskEventSource
{
    private readonly Queue<PhysicalDiskEventBatch> _batches = new(batches);

    public ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_batches.Dequeue());
    }
}
