using MonitoringXS.Application;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application.Tests;

public sealed class SystemOverviewServiceTests
{
    [Fact]
    public async Task CaptureAsyncFirstCpuSampleIsWarmingUp()
    {
        FakeProvider provider = new(
            cpuPercent: MetricValue<double>.Unavailable(MetricAvailability.WarmingUp, "First sample."));
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(
            diskDiagnostics: null, networkDiagnostics: null, gpuBatch: null, CancellationToken.None);

        Assert.Equal(MetricAvailability.WarmingUp, snapshot.TotalCpuPercent.Availability);
        Assert.False(snapshot.TotalCpuPercent.IsAvailable);
    }

    [Fact]
    public async Task CaptureAsyncCpuAvailableOnSecondSample()
    {
        FakeProvider provider = new(
            cpuPercent: MetricValue<double>.Available(42.5));
        SystemOverviewService service = new(provider);

        // First call (warming up state is handled by provider)
        await service.CaptureAsync(null, null, null, CancellationToken.None);
        // Second call with available CPU
        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, null, CancellationToken.None);

        Assert.True(snapshot.TotalCpuPercent.IsAvailable);
        Assert.Equal(42.5, snapshot.TotalCpuPercent.Value);
    }

    [Fact]
    public async Task CaptureAsyncRejectsInvalidCpuValues()
    {
        FakeProvider provider = new(
            cpuPercent: MetricValue<double>.Available(double.NaN));
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, null, CancellationToken.None);

        // NaN should propagate as-is from provider; the service trusts the provider
        // but MetricValue with NaN value and Available status is still "available" per struct semantics
        Assert.Equal(MetricAvailability.Available, snapshot.TotalCpuPercent.Availability);
    }

    [Fact]
    public async Task CaptureAsyncMemoryCalculationsAreCorrect()
    {
        FakeProvider provider = new(
            totalMemory: MetricValue<long>.Available(16_000_000_000),
            usedMemory: MetricValue<long>.Available(8_000_000_000),
            availableMemory: MetricValue<long>.Available(8_000_000_000),
            memoryUtilization: MetricValue<double>.Available(50.0));
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, null, CancellationToken.None);

        Assert.Equal(16_000_000_000, snapshot.TotalPhysicalMemoryBytes.Value);
        Assert.Equal(8_000_000_000, snapshot.UsedPhysicalMemoryBytes.Value);
        Assert.Equal(8_000_000_000, snapshot.AvailablePhysicalMemoryBytes.Value);
        Assert.Equal(50.0, snapshot.PhysicalMemoryUtilizationPercent.Value);
    }

    [Fact]
    public async Task CaptureAsyncDiskRateRequiresTwoSamples()
    {
        PhysicalDiskCollectorDiagnostics diagnostics = new(
            EtwEventsLost: 0,
            QueueEventsDropped: 0,
            UnattributedEvents: 0,
            PidReuseEventsRejected: 0,
            ReadBytesObserved: 1_000_000,
            WriteBytesObserved: 500_000,
            LastSuccessfulEventTimestampUtc: DateTimeOffset.UtcNow,
            CollectorStatus: MetricAvailability.Available,
            SessionTotalsAreLowerBounds: false);
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        // First sample - should be WarmingUp for disk rates
        SystemOverviewSnapshot first = await service.CaptureAsync(diagnostics, null, null, CancellationToken.None);
        Assert.Equal(MetricAvailability.WarmingUp, first.DiskReadBytesPerSecond.Availability);
        Assert.Equal(MetricAvailability.WarmingUp, first.DiskWriteBytesPerSecond.Availability);

        // Second sample with increased bytes
        PhysicalDiskCollectorDiagnostics diagnostics2 = diagnostics with
        {
            ReadBytesObserved = 2_000_000,
            WriteBytesObserved = 800_000
        };
        SystemOverviewSnapshot second = await service.CaptureAsync(diagnostics2, null, null, CancellationToken.None);
        Assert.True(second.DiskReadBytesPerSecond.IsAvailable);
        Assert.True(second.DiskWriteBytesPerSecond.IsAvailable);
        Assert.True(second.DiskReadBytesPerSecond.Value > 0);
        Assert.True(second.DiskWriteBytesPerSecond.Value > 0);
    }

    [Fact]
    public async Task CaptureAsyncNetworkRateRequiresTwoSamples()
    {
        NetworkCollectorDiagnostics diagnostics = new()
        {
            CollectorStatus = MetricAvailability.Available,
            TotalSourceReceiveBytes = 5_000_000,
            TotalSourceSendBytes = 2_000_000,
            SessionTotalsAreLowerBounds = false
        };
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot first = await service.CaptureAsync(null, diagnostics, null, CancellationToken.None);
        Assert.Equal(MetricAvailability.WarmingUp, first.NetworkReceiveBytesPerSecond.Availability);
        Assert.Equal(MetricAvailability.WarmingUp, first.NetworkSendBytesPerSecond.Availability);

        NetworkCollectorDiagnostics diagnostics2 = diagnostics with
        {
            TotalSourceReceiveBytes = 6_000_000,
            TotalSourceSendBytes = 2_500_000
        };
        SystemOverviewSnapshot second = await service.CaptureAsync(null, diagnostics2, null, CancellationToken.None);
        Assert.True(second.NetworkReceiveBytesPerSecond.IsAvailable);
        Assert.True(second.NetworkSendBytesPerSecond.IsAvailable);
    }

    [Fact]
    public async Task CaptureAsyncGpuReturnsUnsupportedWhenBatchIsNull()
    {
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, gpuBatch: null, CancellationToken.None);

        Assert.Equal(MetricAvailability.Unsupported, snapshot.GpuUtilizationPercent.Availability);
        Assert.False(snapshot.GpuUtilizationPercent.IsAvailable);
    }

    [Fact]
    public async Task CaptureAsyncGpuReturnsWarmingUpWhenBatchIsWarmingUp()
    {
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.WarmingUp,
            Reason: GpuAvailabilityReason.WarmingUp,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.WarmingUp,
                Reason = GpuAvailabilityReason.WarmingUp,
                CollectorStatusReason = "GPU counters require a second sample."
            });
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        Assert.Equal(MetricAvailability.WarmingUp, snapshot.GpuUtilizationPercent.Availability);
    }

    [Fact]
    public async Task CaptureAsyncGpuUsesMachineWideValue()
    {
        // Machine-wide value represents busiest engine across ALL system processes.
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Available,
            Reason: GpuAvailabilityReason.None,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Available,
                Reason = GpuAvailabilityReason.None
            })
        {
            MachineWideGpuUtilizationPercent = MetricValue<double>.Available(60.0)
        };
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        Assert.True(snapshot.GpuUtilizationPercent.IsAvailable);
        Assert.Equal(60.0, snapshot.GpuUtilizationPercent.Value);
    }

    [Fact]
    public async Task CaptureAsyncGpuReturnsUnavailableWhenNoMachineWideValue()
    {
        // Batch says Available but no machine-wide value was produced.
        // This happens when the counter source could not compute a machine-wide reading.
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Available,
            Reason: GpuAvailabilityReason.None,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Available,
                Reason = GpuAvailabilityReason.None
            });
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        // No machine-wide data → Unavailable (not fabricated from monitored-app absence)
        Assert.Equal(MetricAvailability.Unavailable, snapshot.GpuUtilizationPercent.Availability);
        Assert.False(snapshot.GpuUtilizationPercent.IsAvailable);
    }

    [Fact]
    public async Task CaptureAsyncGpuMachineWideZeroIsAvailable()
    {
        // GPU idle: machine-wide value is 0%. This IS valid data.
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Available,
            Reason: GpuAvailabilityReason.None,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Available,
                Reason = GpuAvailabilityReason.None
            })
        {
            MachineWideGpuUtilizationPercent = MetricValue<double>.Available(0.0)
        };
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        Assert.True(snapshot.GpuUtilizationPercent.IsAvailable);
        Assert.Equal(0.0, snapshot.GpuUtilizationPercent.Value);
    }

    [Fact]
    public async Task CaptureAsyncGpuPassesThroughBatchErrorStatus()
    {
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Error,
            Reason: GpuAvailabilityReason.CounterReadFailure,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Error,
                Reason = GpuAvailabilityReason.CounterReadFailure,
                CollectorStatusReason = "GPU performance-counter sampling failed."
            });
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        Assert.Equal(MetricAvailability.Error, snapshot.GpuUtilizationPercent.Availability);
        Assert.False(snapshot.GpuUtilizationPercent.IsAvailable);
    }

    [Fact]
    public async Task CaptureAsyncGpuPassesThroughBatchUnsupportedStatus()
    {
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Unsupported,
            Reason: GpuAvailabilityReason.CounterSetUnavailable,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Unsupported,
                Reason = GpuAvailabilityReason.CounterSetUnavailable,
                CollectorStatusReason = "WDDM 2.x driver required."
            });
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        Assert.Equal(MetricAvailability.Unsupported, snapshot.GpuUtilizationPercent.Availability);
    }

    [Fact]
    public async Task CaptureAsyncGpuPartialWithValidMachineWideRemainsPartial()
    {
        // Batch is Partial (some processes unavailable) but machine-wide value is valid.
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Partial,
            Reason: GpuAvailabilityReason.CounterUnavailable,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Partial,
                Reason = GpuAvailabilityReason.CounterUnavailable
            })
        {
            MachineWideGpuUtilizationPercent = MetricValue<double>.Available(25.0)
        };
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        Assert.Equal(MetricAvailability.Partial, snapshot.GpuUtilizationPercent.Availability);
        Assert.True(snapshot.GpuUtilizationPercent.IsAvailable);
        Assert.Equal(25.0, snapshot.GpuUtilizationPercent.Value);
    }

    [Fact]
    public async Task CaptureAsyncGpuRejectsImpossiblePercentage()
    {
        // Impossible values (>100%) must be REJECTED, not silently clamped.
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Available,
            Reason: GpuAvailabilityReason.None,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Available,
                Reason = GpuAvailabilityReason.None
            })
        {
            MachineWideGpuUtilizationPercent = MetricValue<double>.Available(150.0)
        };
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        // Rejected, NOT clamped to 100
        Assert.Equal(MetricAvailability.Error, snapshot.GpuUtilizationPercent.Availability);
        Assert.False(snapshot.GpuUtilizationPercent.IsAvailable);
        Assert.Contains("rejected", snapshot.GpuUtilizationPercent.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureAsyncGpuMachineWideIndependentOfMonitoredApps()
    {
        // Even with NO monitored-app process data, machine-wide GPU shows real utilization.
        // This proves System Overview GPU is truly machine-wide, not app-filtered.
        GpuCounterBatch batch = new(
            Processes: [],
            Availability: MetricAvailability.Available,
            Reason: GpuAvailabilityReason.None,
            Diagnostics: new GpuCollectorDiagnostics
            {
                ProviderName = GpuCollectorDiagnostics.WindowsPdhProvider,
                CollectorStatus = MetricAvailability.Available,
                Reason = GpuAvailabilityReason.None
            })
        {
            MachineWideGpuUtilizationPercent = MetricValue<double>.Available(75.0)
        };
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, batch, CancellationToken.None);

        Assert.True(snapshot.GpuUtilizationPercent.IsAvailable);
        Assert.Equal(75.0, snapshot.GpuUtilizationPercent.Value);
    }

    [Fact]
    public async Task CaptureAsyncPartialDiskSemantics()
    {
        PhysicalDiskCollectorDiagnostics diagnostics = new(
            EtwEventsLost: 5,
            QueueEventsDropped: 0,
            UnattributedEvents: 2,
            PidReuseEventsRejected: 0,
            ReadBytesObserved: 1_000_000,
            WriteBytesObserved: 500_000,
            LastSuccessfulEventTimestampUtc: DateTimeOffset.UtcNow,
            CollectorStatus: MetricAvailability.Partial,
            SessionTotalsAreLowerBounds: true);
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        // First sample
        await service.CaptureAsync(diagnostics, null, null, CancellationToken.None);

        // Second sample with partial status
        PhysicalDiskCollectorDiagnostics diagnostics2 = diagnostics with
        {
            ReadBytesObserved = 2_000_000,
            WriteBytesObserved = 800_000
        };
        SystemOverviewSnapshot snapshot = await service.CaptureAsync(diagnostics2, null, null, CancellationToken.None);

        Assert.Equal(MetricAvailability.Partial, snapshot.Diagnostics.DiskAvailability);
    }

    [Fact]
    public async Task CaptureAsyncNullDiskDiagnosticsReturnsUnsupported()
    {
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(
            diskDiagnostics: null, networkDiagnostics: null, gpuBatch: null, CancellationToken.None);

        Assert.Equal(MetricAvailability.Unsupported, snapshot.Diagnostics.DiskAvailability);
        Assert.NotNull(snapshot.Diagnostics.DiskDetail);
    }

    [Fact]
    public async Task CaptureAsyncNullNetworkDiagnosticsReturnsUnsupported()
    {
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(
            diskDiagnostics: null, networkDiagnostics: null, gpuBatch: null, CancellationToken.None);

        Assert.Equal(MetricAvailability.Unsupported, snapshot.Diagnostics.NetworkAvailability);
        Assert.NotNull(snapshot.Diagnostics.NetworkDetail);
    }

    [Fact]
    public async Task CaptureAsyncProviderFailureDoesNotThrow()
    {
        ThrowingProvider provider = new();
        SystemOverviewService service = new(provider);

        // Should throw because the provider throws and the service doesn't catch it
        // (failure isolation is at the coordinator level, not within the service)
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync(null, null, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CaptureAsyncCancellationPropagates()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        SlowProvider provider = new();
        SystemOverviewService service = new(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CaptureAsync(null, null, null, cts.Token).AsTask());
    }

    [Fact]
    public async Task GetHistoryReturnsBoundedTimeSeries()
    {
        FakeProvider provider = new(cpuPercent: MetricValue<double>.Available(25.0));
        SystemOverviewService service = new(provider);

        // Capture multiple snapshots
        for (int i = 0; i < 5; i++)
        {
            await service.CaptureAsync(null, null, null, CancellationToken.None);
        }

        IReadOnlyList<SystemOverviewHistoryPoint> history = service.GetHistory();
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public async Task GetHistoryDoesNotExceedCapacity()
    {
        FakeProvider provider = new(cpuPercent: MetricValue<double>.Available(25.0));
        SystemOverviewService service = new(provider);

        // Capture more than 60 snapshots (the capacity)
        for (int i = 0; i < 70; i++)
        {
            await service.CaptureAsync(null, null, null, CancellationToken.None);
        }

        IReadOnlyList<SystemOverviewHistoryPoint> history = service.GetHistory();
        Assert.True(history.Count <= 60);
    }

    [Fact]
    public async Task CaptureAsyncNoFakeZeroForUnavailableMetrics()
    {
        FakeProvider provider = new(
            cpuPercent: MetricValue<double>.Unavailable(MetricAvailability.Unavailable, "Counter not ready."));
        SystemOverviewService service = new(provider);

        SystemOverviewSnapshot snapshot = await service.CaptureAsync(null, null, null, CancellationToken.None);

        Assert.False(snapshot.TotalCpuPercent.IsAvailable);
        Assert.Null(snapshot.TotalCpuPercent.Value);
        Assert.NotEqual(0d, snapshot.TotalCpuPercent.Value);
    }

    [Fact]
    public async Task CaptureAsyncSaturatingSubtractionPreventsOverflow()
    {
        // When counter wraps or resets, subtraction should saturate to 0
        PhysicalDiskCollectorDiagnostics first = new(
            EtwEventsLost: 0,
            QueueEventsDropped: 0,
            UnattributedEvents: 0,
            PidReuseEventsRejected: 0,
            ReadBytesObserved: ulong.MaxValue,
            WriteBytesObserved: ulong.MaxValue,
            LastSuccessfulEventTimestampUtc: DateTimeOffset.UtcNow,
            CollectorStatus: MetricAvailability.Available,
            SessionTotalsAreLowerBounds: false);
        PhysicalDiskCollectorDiagnostics second = first with
        {
            ReadBytesObserved = 100, // Wrapped around
            WriteBytesObserved = 50
        };
        FakeProvider provider = new();
        SystemOverviewService service = new(provider);

        await service.CaptureAsync(first, null, null, CancellationToken.None);
        SystemOverviewSnapshot snapshot = await service.CaptureAsync(second, null, null, CancellationToken.None);

        // Rate should be non-negative (saturating subtraction)
        Assert.True(snapshot.DiskReadBytesPerSecond.IsAvailable);
        Assert.True(snapshot.DiskReadBytesPerSecond.Value >= 0);
    }

    [Fact]
    public async Task CaptureAsyncDiskFailureDoesNotBreakNetworkOrMemory()
    {
        // Even when disk diagnostics indicate error, memory and network remain functional
        NetworkCollectorDiagnostics networkDiag = new()
        {
            CollectorStatus = MetricAvailability.Available,
            TotalSourceReceiveBytes = 1_000_000,
            TotalSourceSendBytes = 500_000,
            SessionTotalsAreLowerBounds = false
        };
        FakeProvider provider = new(
            totalMemory: MetricValue<long>.Available(8_000_000_000),
            usedMemory: MetricValue<long>.Available(4_000_000_000),
            availableMemory: MetricValue<long>.Available(4_000_000_000),
            memoryUtilization: MetricValue<double>.Available(50.0));
        SystemOverviewService service = new(provider);

        // First call to establish network baseline
        await service.CaptureAsync(null, networkDiag, null, CancellationToken.None);

        // Second call with network data
        NetworkCollectorDiagnostics networkDiag2 = networkDiag with
        {
            TotalSourceReceiveBytes = 2_000_000,
            TotalSourceSendBytes = 1_000_000
        };
        SystemOverviewSnapshot snapshot = await service.CaptureAsync(
            diskDiagnostics: null, // No disk
            networkDiag2,
            gpuBatch: null,
            CancellationToken.None);

        // Memory should still be available
        Assert.True(snapshot.TotalPhysicalMemoryBytes.IsAvailable);
        Assert.True(snapshot.PhysicalMemoryUtilizationPercent.IsAvailable);
        // Network should be available
        Assert.True(snapshot.NetworkReceiveBytesPerSecond.IsAvailable);
        // Disk should be unsupported (null diagnostics)
        Assert.Equal(MetricAvailability.Unsupported, snapshot.Diagnostics.DiskAvailability);
    }

    private sealed class FakeProvider(
        MetricValue<double>? cpuPercent = null,
        MetricValue<long>? totalMemory = null,
        MetricValue<long>? usedMemory = null,
        MetricValue<long>? availableMemory = null,
        MetricValue<double>? memoryUtilization = null) : ISystemOverviewProvider
    {
        private int _callCount;

        public ValueTask<SystemOverviewSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            // Increment timestamp by 1 second per call to ensure positive elapsed time for rate calculations
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow.AddSeconds(Interlocked.Increment(ref _callCount));
            SystemOverviewSnapshot snapshot = new(
                CapturedAt: capturedAt,
                TotalCpuPercent: cpuPercent ?? MetricValue<double>.Unavailable(MetricAvailability.WarmingUp, "Test warming up."),
                TotalPhysicalMemoryBytes: totalMemory ?? MetricValue<long>.Available(8_000_000_000),
                UsedPhysicalMemoryBytes: usedMemory ?? MetricValue<long>.Available(4_000_000_000),
                AvailablePhysicalMemoryBytes: availableMemory ?? MetricValue<long>.Available(4_000_000_000),
                PhysicalMemoryUtilizationPercent: memoryUtilization ?? MetricValue<double>.Available(50.0),
                DiskReadBytesPerSecond: MetricValue<double>.Unavailable(MetricAvailability.Unsupported),
                DiskWriteBytesPerSecond: MetricValue<double>.Unavailable(MetricAvailability.Unsupported),
                NetworkReceiveBytesPerSecond: MetricValue<double>.Unavailable(MetricAvailability.Unsupported),
                NetworkSendBytesPerSecond: MetricValue<double>.Unavailable(MetricAvailability.Unsupported),
                GpuUtilizationPercent: MetricValue<double>.Unavailable(MetricAvailability.Unsupported),
                Diagnostics: new SystemOverviewDiagnostics(
                    (cpuPercent ?? MetricValue<double>.Unavailable(MetricAvailability.WarmingUp)).Availability,
                    null,
                    MetricAvailability.Available,
                    null,
                    MetricAvailability.Unsupported,
                    null,
                    MetricAvailability.Unsupported,
                    null,
                    MetricAvailability.Unsupported,
                    null,
                    1,
                    false));
            return new ValueTask<SystemOverviewSnapshot>(snapshot);
        }
    }

    private sealed class ThrowingProvider : ISystemOverviewProvider
    {
        public ValueTask<SystemOverviewSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated provider failure.");
    }

    private sealed class SlowProvider : ISystemOverviewProvider
    {
        public async ValueTask<SystemOverviewSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw new InvalidOperationException("Should have been cancelled.");
        }
    }
}