using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class GpuMetricAggregationServiceTests
{
    [Fact]
    public void SumsProcessesOnSameEngineThenSelectsBusiestEngine()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(200, now);
        ProcessDescriptor second = Process(201, now);
        ApplicationIdentity application = App("browser");
        GpuEngineId sharedEngine = new(1, 0, 0, "3D");
        GpuEngineId copyEngine = new(1, 0, 1, "Copy");

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [
                AttributionResult.Attributed(first, application),
                AttributionResult.Attributed(second, application)
            ],
            [
                Sample(first, now, [new(sharedEngine, 30), new(copyEngine, 15)], 100, 20),
                Sample(second, now, [new(sharedEngine, 40), new(copyEngine, 10)], 200, 30)
            ])[application.LogicalApplicationId];

        Assert.Equal(70d, result.UtilizationPercent.Value);
        Assert.Equal(sharedEngine, result.BusiestEngine);
        Assert.Equal(300UL, result.DedicatedMemoryBytes.Value);
        Assert.Equal(50UL, result.SharedMemoryBytes.Value);
    }

    [Fact]
    public void DoesNotSumParallelAdaptersIntoImpossiblePercentage()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(202, now);
        ApplicationIdentity application = App("game");

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [AttributionResult.Attributed(process, application)],
            [Sample(
                process,
                now,
                [
                    new(new GpuEngineId(1, 0, 0, "3D"), 80),
                    new(new GpuEngineId(2, 0, 0, "3D"), 70)
                ],
                0,
                0)])[application.LogicalApplicationId];

        Assert.Equal(80d, result.UtilizationPercent.Value);
    }

    [Fact]
    public void DuplicateEngineIdentityWithinOneProcessIsNotDoubleCounted()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(209, now);
        ApplicationIdentity application = App("duplicate-engine");
        GpuEngineId engine = new(1, 0, 0, "3D");

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [AttributionResult.Attributed(process, application)],
            [Sample(process, now, [new(engine, 35), new(engine, 35)], 0, 0)])
            [application.LogicalApplicationId];

        Assert.Equal(35d, result.UtilizationPercent.Value);
        Assert.Equal(MetricAvailability.Partial, result.UtilizationPercent.Availability);
    }

    [Fact]
    public void ImpossibleCombinedEngineValueIsRejected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(210, now);
        ProcessDescriptor second = Process(211, now);
        ApplicationIdentity application = App("over-capacity");
        GpuEngineId engine = new(1, 0, 0, "3D");

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [
                AttributionResult.Attributed(first, application),
                AttributionResult.Attributed(second, application)
            ],
            [
                Sample(first, now, [new(engine, 70)], 0, 0),
                Sample(second, now, [new(engine, 60)], 0, 0)
            ])[application.LogicalApplicationId];

        Assert.Null(result.UtilizationPercent.Value);
        Assert.Equal(MetricAvailability.Error, result.UtilizationPercent.Availability);
        Assert.Contains("exceeded", result.UtilizationPercent.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MemoryOverflowIsSaturatedAndMarkedPartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(212, now);
        ProcessDescriptor second = Process(213, now);
        ApplicationIdentity application = App("memory-overflow");

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [
                AttributionResult.Attributed(first, application),
                AttributionResult.Attributed(second, application)
            ],
            [
                Sample(first, now, [], ulong.MaxValue, 0),
                Sample(second, now, [], 1, 0)
            ])[application.LogicalApplicationId];

        Assert.Equal(ulong.MaxValue, result.DedicatedMemoryBytes.Value);
        Assert.Equal(MetricAvailability.Partial, result.DedicatedMemoryBytes.Availability);
        Assert.Contains("overflow", result.DedicatedMemoryBytes.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingProcessSampleMakesLogicalApplicationPartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(203, now);
        ProcessDescriptor second = Process(204, now);
        ApplicationIdentity application = App("editor");

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [
                AttributionResult.Attributed(first, application),
                AttributionResult.Attributed(second, application)
            ],
            [Sample(
                first,
                now,
                [new(new GpuEngineId(1, 0, 0, "3D"), 25)],
                100,
                50)])[application.LogicalApplicationId];

        Assert.Equal(MetricAvailability.Partial, result.UtilizationPercent.Availability);
        Assert.Equal(25d, result.UtilizationPercent.Value);
        Assert.Equal(MetricAvailability.Partial, result.DedicatedMemoryBytes.Availability);
        Assert.Contains("1 of 2", result.UtilizationPercent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsLogicalApplicationBoundaries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor chrome = Process(205, now);
        ProcessDescriptor chromeGpu = Process(206, now);
        ProcessDescriptor code = Process(207, now);
        ApplicationIdentity chromeApp = App("chrome");
        ApplicationIdentity codeApp = App("code");
        GpuEngineId engine = new(1, 0, 0, "3D");

        IReadOnlyDictionary<string, GpuMetricSet> result =
            new GpuMetricAggregationService().Aggregate(
                [
                    AttributionResult.Attributed(chrome, chromeApp),
                    AttributionResult.Attributed(chromeGpu, chromeApp),
                    AttributionResult.Attributed(code, codeApp)
                ],
                [
                    Sample(chrome, now, [new(engine, 10)], 10, 1),
                    Sample(chromeGpu, now, [new(engine, 20)], 20, 2),
                    Sample(code, now, [new(engine, 40)], 40, 4)
                ]);

        Assert.Equal(30d, result["chrome"].UtilizationPercent.Value);
        Assert.Equal(40d, result["code"].UtilizationPercent.Value);
        Assert.Equal(30UL, result["chrome"].DedicatedMemoryBytes.Value);
    }

    [Fact]
    public void ExitedHelperMemoryIsNotRetainedAsLiveMemory()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor main = Process(214, now);
        ProcessDescriptor helper = Process(215, now);
        ApplicationIdentity application = App("browser");
        GpuMetricAggregationService service = new();

        GpuMetricSet beforeExit = service.Aggregate(
            [
                AttributionResult.Attributed(main, application),
                AttributionResult.Attributed(helper, application)
            ],
            [
                Sample(main, now, [], 100, 10),
                Sample(helper, now, [], 200, 20)
            ])[application.LogicalApplicationId];
        GpuMetricSet afterExit = service.Aggregate(
            [AttributionResult.Attributed(main, application)],
            [Sample(main, now.AddSeconds(1), [], 100, 10)])
            [application.LogicalApplicationId];

        Assert.Equal(300UL, beforeExit.DedicatedMemoryBytes.Value);
        Assert.Equal(100UL, afterExit.DedicatedMemoryBytes.Value);
        Assert.Equal(10UL, afterExit.SharedMemoryBytes.Value);
    }

    [Fact]
    public void CompleteExitAndRelaunchDoNotRetainGpuState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor firstLifetime = Process(216, now);
        ProcessDescriptor secondLifetime = Process(217, now.AddMinutes(1));
        ApplicationIdentity application = App("game");
        GpuMetricAggregationService service = new();

        GpuMetricSet first = service.Aggregate(
            [AttributionResult.Attributed(firstLifetime, application)],
            [Sample(firstLifetime, now, [], 500, 50)])
            [application.LogicalApplicationId];
        IReadOnlyDictionary<string, GpuMetricSet> exited = service.Aggregate([], []);
        GpuMetricSet relaunched = service.Aggregate(
            [AttributionResult.Attributed(secondLifetime, application)],
            [Sample(secondLifetime, now.AddMinutes(1), [], 25, 5)])
            [application.LogicalApplicationId];

        Assert.Equal(500UL, first.DedicatedMemoryBytes.Value);
        Assert.Empty(exited);
        Assert.Equal(25UL, relaunched.DedicatedMemoryBytes.Value);
    }

    [Fact]
    public void AttributionChangeDoesNotTransferGpuState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(218, now);
        ApplicationIdentity firstApplication = App("first");
        ApplicationIdentity secondApplication = App("second");
        GpuMetricAggregationService service = new();

        IReadOnlyDictionary<string, GpuMetricSet> result = service.Aggregate(
            [AttributionResult.Attributed(process, secondApplication)],
            [Sample(process, now, [], 42, 7)]);

        Assert.DoesNotContain(firstApplication.LogicalApplicationId, result.Keys);
        Assert.Equal(42UL, result[secondApplication.LogicalApplicationId].DedicatedMemoryBytes.Value);
    }

    [Fact]
    public void AdapterDisappearanceDoesNotLeaveStaleBusiestEngine()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(219, now);
        ApplicationIdentity application = App("multi-adapter");
        GpuEngineId integrated = new(1, 0, 0, "3D");
        GpuEngineId discrete = new(2, 0, 0, "3D");
        GpuMetricAggregationService service = new();

        GpuMetricSet first = service.Aggregate(
            [AttributionResult.Attributed(process, application)],
            [Sample(process, now, [new(integrated, 10), new(discrete, 80)], 0, 0)])
            [application.LogicalApplicationId];
        GpuMetricSet second = service.Aggregate(
            [AttributionResult.Attributed(process, application)],
            [Sample(process, now.AddSeconds(1), [new(integrated, 20)], 0, 0)])
            [application.LogicalApplicationId];

        Assert.Equal(discrete, first.BusiestEngine);
        Assert.Equal(integrated, second.BusiestEngine);
        Assert.Equal(20d, second.UtilizationPercent.Value);
    }

    [Fact]
    public void UnsupportedMetricRemainsUnavailable()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(208, now);
        ApplicationIdentity application = App("legacy");
        GpuProcessSample unavailable = new(
            process.InstanceId,
            now,
            MetricValue<double>.Unavailable(MetricAvailability.Unsupported),
            MetricValue<ulong>.Unavailable(MetricAvailability.Unsupported),
            MetricValue<ulong>.Unavailable(MetricAvailability.Unsupported),
            [],
            new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Unsupported,
                Reason = GpuAvailabilityReason.UnsupportedDriver
            });

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [AttributionResult.Attributed(process, application)],
            [unavailable])[application.LogicalApplicationId];

        Assert.Equal(MetricAvailability.Unsupported, result.UtilizationPercent.Availability);
        Assert.Null(result.UtilizationPercent.Value);
    }

    [Fact]
    public void QuarantineDetailsSurviveLogicalApplicationAggregation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(220, now);
        ApplicationIdentity application = App("ambiguous-lifetime");
        GpuProcessSample quarantined = new(
            process.InstanceId,
            now,
            MetricValue<double>.Unavailable(
                MetricAvailability.Unavailable,
                "GPU utilization is quarantined."),
            MetricValue<ulong>.Unavailable(
                MetricAvailability.Unavailable,
                "Dedicated GPU memory is quarantined."),
            MetricValue<ulong>.Unavailable(
                MetricAvailability.Unavailable,
                "Shared GPU memory is quarantined."),
            [],
            new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Unavailable,
                Reason = GpuAvailabilityReason.AmbiguousCounterLifetime
            });

        GpuMetricSet result = new GpuMetricAggregationService().Aggregate(
            [AttributionResult.Attributed(process, application)],
            [quarantined])[application.LogicalApplicationId];

        Assert.Contains(
            "quarantined",
            result.UtilizationPercent.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "quarantined",
            result.DedicatedMemoryBytes.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "quarantined",
            result.SharedMemoryBytes.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    private static GpuProcessSample Sample(
        ProcessDescriptor process,
        DateTimeOffset now,
        IReadOnlyList<GpuEngineUsage> engines,
        ulong dedicated,
        ulong shared) => new(
        process.InstanceId,
        now,
        MetricValue<double>.Available(
            engines.Select(engine => engine.UtilizationPercent).DefaultIfEmpty().Max()),
        MetricValue<ulong>.Available(dedicated),
        MetricValue<ulong>.Available(shared),
        engines,
        new GpuCollectorDiagnostics
        {
            ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
            CollectorStatus = MetricAvailability.Available,
            Reason = GpuAvailabilityReason.None
        });

    private static ProcessDescriptor Process(int processId, DateTimeOffset startTime) => new(
        new ProcessInstanceId(processId, startTime),
        "process",
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        false);

    private static ApplicationIdentity App(string id) => new(
        id,
        id,
        null,
        ApplicationDisposition.Installed,
        null,
        ClassificationConfidence.High,
        "test");
}
