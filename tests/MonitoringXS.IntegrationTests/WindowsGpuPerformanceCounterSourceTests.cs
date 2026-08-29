using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Metrics;
using System.Diagnostics;

namespace MonitoringXS.IntegrationTests;

public sealed class WindowsGpuPerformanceCounterSourceTests
{
    [Fact]
    public void ParsesEngineInstanceWithoutGuessingPidOrAdapter()
    {
        const string instance =
            "pid_18788_luid_0x00000001_0x0000A778_phys_2_eng_6_engtype_Copy";

        bool parsed = WindowsGpuPerformanceCounterSource.TryParseEngineInstance(
            instance,
            out int processId,
            out GpuEngineId engine);

        Assert.True(parsed);
        Assert.Equal(18_788, processId);
        Assert.Equal(0x000000010000A778UL, engine.AdapterLuid);
        Assert.Equal(2, engine.PhysicalAdapterIndex);
        Assert.Equal(6, engine.EngineIndex);
        Assert.Equal("Copy", engine.EngineType);
    }

    [Fact]
    public void ParsesProcessMemoryInstanceAcrossAdapters()
    {
        const string instance =
            "pid_18788_luid_0x00000000_0x0000A6F6_phys_1";

        bool parsed = WindowsGpuPerformanceCounterSource.TryParseMemoryInstance(
            instance,
            out int processId,
            out ulong adapterLuid,
            out int physicalAdapterIndex);

        Assert.True(parsed);
        Assert.Equal(18_788, processId);
        Assert.Equal(0xA6F6UL, adapterLuid);
        Assert.Equal(1, physicalAdapterIndex);
    }

    [Theory]
    [InlineData(
        "ENGTYPE_3d_ENG_6_PHYS_2_LUID_0X00000001_0X0000A778_PID_18788",
        "3D")]
    [InlineData(
        "luid_0x00000001_0x0000A778_pid_18788_eng_6_engtype_Copy_phys_2#4",
        "Copy")]
    [InlineData(
        "pid_18788_luid_0x00000001_0x0000A778_phys_2_eng_6_engtype_vendor localized engine",
        "Unknown")]
    public void ParsesEngineFieldsWithoutDependingOnOrderOrDuplicateSuffix(
        string instance,
        string expectedType)
    {
        bool parsed = WindowsGpuPerformanceCounterSource.TryParseEngineInstance(
            instance,
            out int processId,
            out GpuEngineId engine);

        Assert.True(parsed);
        Assert.Equal(18_788, processId);
        Assert.Equal(0x000000010000A778UL, engine.AdapterLuid);
        Assert.Equal(2, engine.PhysicalAdapterIndex);
        Assert.Equal(6, engine.EngineIndex);
        Assert.Equal(expectedType, engine.EngineType);
    }

    [Theory]
    [InlineData("phys_1_pid_18788_luid_0x00000000_0x0000A6F6")]
    [InlineData("luid_0X00000000_0X0000A6F6_phys_1_pid_18788#2")]
    public void ParsesMemoryFieldsIndependentlyOfOrder(string instance)
    {
        Assert.True(WindowsGpuPerformanceCounterSource.TryParseMemoryInstance(
            instance,
            out int processId,
            out ulong adapterLuid,
            out int physicalAdapterIndex));
        Assert.Equal(18_788, processId);
        Assert.Equal(0xA6F6UL, adapterLuid);
        Assert.Equal(1, physicalAdapterIndex);
    }

    [Fact]
    public void DedicatedAndSharedMemorySumsOnlyEnumerateTheirOwnCounterInstances()
    {
        WindowsGpuPerformanceCounterSource.GpuMemoryInstanceId dedicatedInstance =
            new(18788, 0xA6F6UL, 0);
        WindowsGpuPerformanceCounterSource.GpuMemoryInstanceId sharedInstance =
            new(18788, 0xA6F6UL, 0);

        MetricValue<ulong> dedicated = WindowsGpuPerformanceCounterSource.SumMemoryForTesting(
            [dedicatedInstance],
            new Dictionary<WindowsGpuPerformanceCounterSource.GpuMemoryInstanceId, ulong>
            {
                [dedicatedInstance] = 64 * 1024UL
            },
            MetricAvailability.Available,
            null,
            hadInvalidValue: false,
            "dedicated GPU memory");
        MetricValue<ulong> shared = WindowsGpuPerformanceCounterSource.SumMemoryForTesting(
            [sharedInstance],
            new Dictionary<WindowsGpuPerformanceCounterSource.GpuMemoryInstanceId, ulong>
            {
                [sharedInstance] = 8 * 1024UL
            },
            MetricAvailability.Available,
            null,
            hadInvalidValue: false,
            "shared GPU memory");

        Assert.True(dedicated.IsComplete);
        Assert.Equal(64 * 1024UL, dedicated.Value);
        Assert.True(shared.IsComplete);
        Assert.Equal(8 * 1024UL, shared.Value);
    }

    [Theory]
    [InlineData("pid_123_luid_bad")]
    [InlineData("pid_0_luid_0x0_0x0_phys_x_eng_0_engtype_3D")]
    [InlineData("process_123_luid_0x0_0x0_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_4294967296_luid_0x0_0x0_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_123_pid_124_luid_0x0_0x0_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_123_luid_0x0_0x0_phys_0_engtype_3D")]
    public void RejectsMalformedEngineInstances(string instance)
    {
        Assert.False(WindowsGpuPerformanceCounterSource.TryParseEngineInstance(
            instance,
            out _,
            out _));
    }

    [Theory]
    [InlineData("pid_123_luid_0x0_0x0")]
    [InlineData("pid_123_luid_0x0_0x0_phys_0_phys_1")]
    [InlineData("pid_123_luid_0x0_0x0_phys_0_eng_0")]
    [InlineData("pid_0_luid_0x0_0x0_phys_0")]
    public void RejectsMalformedMemoryInstances(string instance)
    {
        Assert.False(WindowsGpuPerformanceCounterSource.TryParseMemoryInstance(
            instance,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void ReusedPidRemainsQuarantinedUntilOldCounterDisappears()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessInstanceId oldLifetime = new(500, now.AddMinutes(-2));
        ProcessInstanceId newLifetime = new(500, now.AddMinutes(-1));
        GpuProcessLifetimeTracker tracker = new();

        Assert.Empty(tracker.Update([oldLifetime], []).QuarantinedPids);
        Assert.Empty(tracker.Update([oldLifetime], [500]).QuarantinedPids);
        GpuProcessLifetimeTrustResult reused = tracker.Update([newLifetime], [500]);
        Assert.Contains(500, reused.QuarantinedPids);
        Assert.Contains(500, reused.PidReusePids);
        Assert.Contains(500, tracker.Update([newLifetime], [500]).QuarantinedPids);
        Assert.Empty(tracker.Update([newLifetime], []).QuarantinedPids);
        Assert.Empty(tracker.Update([newLifetime], [500]).QuarantinedPids);
    }

    [Fact]
    public void CounterThatPredatesAttributionIsNotAssignedWithoutAnObservedGap()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessInstanceId current = new(501, now.AddMinutes(-1));
        GpuProcessLifetimeTracker tracker = new();

        Assert.Empty(tracker.Update([], [501]).QuarantinedPids);
        GpuProcessLifetimeTrustResult result = tracker.Update([current], [501]);

        Assert.Contains(501, result.QuarantinedPids);
        Assert.Contains(501, result.FirstObservationPids);
    }

    [Fact]
    public void FailedEnumerationDoesNotClearPidReuseQuarantine()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessInstanceId oldLifetime = new(502, now.AddMinutes(-2));
        ProcessInstanceId newLifetime = new(502, now.AddMinutes(-1));
        GpuProcessLifetimeTracker tracker = new();

        tracker.Update([oldLifetime], []);
        tracker.Update([oldLifetime], [502]);
        Assert.Contains(502, tracker.Update([newLifetime], [502]).QuarantinedPids);
        Assert.Contains(502, tracker.Update(
            [newLifetime],
            [],
            counterEnumerationComplete: false).QuarantinedPids);
        Assert.Contains(502, tracker.Update([newLifetime], [502]).QuarantinedPids);
    }

    [Fact]
    public void ProcessDiscoveredBeforeCounterAppearsIsTrusted()
    {
        ProcessInstanceId process = new(503, DateTimeOffset.UtcNow);
        GpuProcessLifetimeTracker tracker = new();

        Assert.Empty(tracker.Update([process], []).QuarantinedPids);
        Assert.Empty(tracker.Update([process], [503]).QuarantinedPids);
    }

    [Fact]
    public void CounterPresentOnFirstProcessObservationIsQuarantined()
    {
        ProcessInstanceId process = new(504, DateTimeOffset.UtcNow);
        GpuProcessLifetimeTracker tracker = new();

        GpuProcessLifetimeTrustResult result = tracker.Update([process], [504]);

        Assert.Contains(504, result.QuarantinedPids);
        Assert.Contains(504, result.FirstObservationPids);
    }

    [Fact]
    public void CounterGapAllowsSafeReappearance()
    {
        ProcessInstanceId process = new(505, DateTimeOffset.UtcNow);
        GpuProcessLifetimeTracker tracker = new();

        Assert.Contains(505, tracker.Update([process], [505]).QuarantinedPids);
        Assert.Empty(tracker.Update([process], []).QuarantinedPids);
        Assert.Empty(tracker.Update([process], [505]).QuarantinedPids);
    }

    [Fact]
    public void EnumerationFailureDoesNotProveCounterAbsence()
    {
        ProcessInstanceId process = new(506, DateTimeOffset.UtcNow);
        GpuProcessLifetimeTracker tracker = new();

        Assert.Empty(tracker.Update(
            [process],
            [],
            counterEnumerationComplete: false).QuarantinedPids);
        Assert.Contains(506, tracker.Update([process], [506]).QuarantinedPids);
    }

    [Fact]
    public void CollectorRestartQuarantinesCountersThatRemainPresent()
    {
        ProcessInstanceId process = new(507, DateTimeOffset.UtcNow);
        GpuProcessLifetimeTracker firstCollector = new();
        firstCollector.Update([process], []);
        Assert.Empty(firstCollector.Update([process], [507]).QuarantinedPids);

        GpuProcessLifetimeTracker restartedCollector = new();
        Assert.Contains(
            507,
            restartedCollector.Update([process], [507]).QuarantinedPids);
    }

    [Fact]
    public void ProcessExitDoesNotClearAQuarantinedCounter()
    {
        ProcessInstanceId process = new(508, DateTimeOffset.UtcNow);
        GpuProcessLifetimeTracker tracker = new();

        Assert.Contains(508, tracker.Update([process], [508]).QuarantinedPids);
        Assert.Empty(tracker.Update([], [508]).QuarantinedPids);
        Assert.Contains(508, tracker.Update([process], [508]).QuarantinedPids);
    }

    [Fact]
    public void PidReuseWhileQuarantinedRemainsRejected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessInstanceId first = new(509, now.AddMinutes(-1));
        ProcessInstanceId reused = new(509, now);
        GpuProcessLifetimeTracker tracker = new();

        Assert.Contains(509, tracker.Update([first], [509]).QuarantinedPids);
        GpuProcessLifetimeTrustResult result = tracker.Update([reused], [509]);

        Assert.Contains(509, result.QuarantinedPids);
        Assert.Contains(509, result.PidReusePids);
    }

    [Fact]
    public void CounterFamiliesHaveIndependentQuarantineState()
    {
        ProcessInstanceId process = new(510, DateTimeOffset.UtcNow);
        GpuProcessLifetimeTracker utilization = new();
        GpuProcessLifetimeTracker dedicatedMemory = new();
        GpuProcessLifetimeTracker sharedMemory = new();

        Assert.Contains(510, utilization.Update([process], [510]).QuarantinedPids);
        Assert.Empty(dedicatedMemory.Update([process], []).QuarantinedPids);
        Assert.Contains(510, sharedMemory.Update([process], [510]).QuarantinedPids);

        Assert.Empty(dedicatedMemory.Update([process], [510]).QuarantinedPids);
        Assert.Contains(510, utilization.Update([process], [510]).QuarantinedPids);
        Assert.Contains(510, sharedMemory.Update([process], [510]).QuarantinedPids);
    }

    [Fact]
    public void LifetimeStateRemainsBounded()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GpuProcessLifetimeTracker tracker = new();

        for (int processId = 1;
             processId <= GpuProcessLifetimeTracker.MaximumTrackedProcessLifetimes + 10;
             processId++)
        {
            tracker.Update(
                [new ProcessInstanceId(processId, now)],
                []);
        }

        Assert.True(
            tracker.TrackedProcessCount
            <= GpuProcessLifetimeTracker.MaximumTrackedProcessLifetimes);
    }

    [Theory]
    [InlineData(0U, 1U, false)]
    [InlineData(1024U, 1U, true)]
    [InlineData(67_108_864U, 65_536U, true)]
    [InlineData(67_108_865U, 1U, false)]
    [InlineData(1024U, 65_537U, false)]
    public void CounterArrayAllocationIsBounded(
        uint bufferSize,
        uint itemCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsGpuPerformanceCounterSource.IsSafeCounterBuffer(
                bufferSize,
                itemCount));
    }

    [Theory]
    [InlineData(32U, 2U, 16, true)]
    [InlineData(31U, 2U, 16, false)]
    public void CounterItemArrayMustFitInsideNativeBuffer(
        uint bufferSize,
        uint itemCount,
        int itemSize,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsGpuPerformanceCounterSource.DoesItemArrayFit(
                bufferSize,
                itemCount,
                itemSize));
    }

    [Fact]
    public async Task RepeatedNativeQueryCreationSamplingAndDisposalIsSafe()
    {
        using Process current = Process.GetCurrentProcess();
        ProcessDescriptor descriptor = new(
            new ProcessInstanceId(
                current.Id,
                current.StartTime.ToUniversalTime()),
            "testhost",
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false);

        for (int iteration = 0; iteration < 10; iteration++)
        {
            WindowsGpuPerformanceCounterSource source = new();
            GpuCounterBatch first = await source.CaptureAsync(
                [descriptor],
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            await Task.Delay(10, TestContext.Current.CancellationToken);
            GpuCounterBatch second = await source.CaptureAsync(
                [descriptor],
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.NotEqual(default, first.Diagnostics.ProviderName);
            Assert.NotEqual(default, second.Diagnostics.ProviderName);
            source.Dispose();
            source.Dispose();
            Assert.False(source.IsQueryOpen);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await source.CaptureAsync(
                    [descriptor],
                    DateTimeOffset.UtcNow,
                    CancellationToken.None));
        }
    }

    [Fact]
    public async Task ConcurrentCapturesSerializeWithoutCorruptingTheNativeQuery()
    {
        using Process current = Process.GetCurrentProcess();
        ProcessDescriptor descriptor = new(
            new ProcessInstanceId(
                current.Id,
                current.StartTime.ToUniversalTime()),
            "testhost",
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false);
        using WindowsGpuPerformanceCounterSource source = new();

        Task<GpuCounterBatch>[] captures = Enumerable.Range(0, 8)
            .Select(_ => source.CaptureAsync(
                [descriptor],
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken).AsTask())
            .ToArray();

        GpuCounterBatch[] results = await Task.WhenAll(captures);

        Assert.Equal(8, results.Length);
        Assert.All(results, result => Assert.NotEqual(
            default,
            result.Diagnostics.ProviderName));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeNativeCollection()
    {
        using WindowsGpuPerformanceCounterSource source = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.CaptureAsync(
                [],
                DateTimeOffset.UtcNow,
                cancellation.Token));

        Assert.False(source.IsQueryOpen);
    }
}
