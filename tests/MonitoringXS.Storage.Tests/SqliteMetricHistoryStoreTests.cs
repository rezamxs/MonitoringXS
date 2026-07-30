using Microsoft.Data.Sqlite;
using MonitoringXS.Core.Models;
using MonitoringXS.Storage.History;

namespace MonitoringXS.Storage.Tests;

public sealed class SqliteMetricHistoryStoreTests
{
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatesVersionedSchemaAndPersistsAcrossRestart()
    {
        using TestDatabase database = new();
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        await using (SqliteMetricHistoryStore writer = database.Store())
        {
            await writer.EnqueueAsync([Snapshot(captured, 25)], TestCancellation);
            await writer.FlushAsync(TestCancellation);
            MetricHistoryQueryResult result = await writer.QueryAsync(
                "app.example",
                MetricHistoryMetric.CpuPercent,
                captured.AddMinutes(-1),
                captured.AddMinutes(1),
                TestCancellation);
            Assert.True(result.IsAvailable);
            Assert.Equal(25, Assert.Single(result.Points).Value);
        }

        await using (SqliteMetricHistoryStore reader = database.Store())
        {
            MetricHistoryQueryResult result = await reader.QueryAsync(
                "app.example",
                MetricHistoryMetric.CpuPercent,
                captured.AddMinutes(-1),
                captured.AddMinutes(1),
                TestCancellation);
            Assert.Equal(25, Assert.Single(result.Points).Value);
        }

        await using SqliteConnection connection = new($"Data Source={database.Path}");
        await connection.OpenAsync(TestCancellation);
        await using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, (long)(await version.ExecuteScalarAsync(TestCancellation))!);
    }

    [Fact]
    public async Task ListsPersistedApplicationsForHistorySelection()
    {
        using TestDatabase database = new();
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        await using SqliteMetricHistoryStore store = database.Store();
        await store.EnqueueAsync([Snapshot(captured, 25)], TestCancellation);
        await store.FlushAsync(TestCancellation);

        MetricHistoryApplicationsResult result = await store.ListApplicationsAsync(TestCancellation);

        MetricHistoryApplication application = Assert.Single(result.Applications);
        Assert.True(result.IsAvailable);
        Assert.Equal("app.example", application.LogicalApplicationId);
        Assert.Equal("Example", application.DisplayName);
        Assert.Equal(ApplicationDisposition.Installed, application.Disposition);
        Assert.Equal(captured, application.UpdatedUtc);
    }

    [Fact]
    public async Task QueryOrdersSamplesAndKeepsAvailabilityAndMetricSeparation()
    {
        using TestDatabase database = new();
        DateTimeOffset first = DateTimeOffset.UtcNow.AddSeconds(-2);
        await using SqliteMetricHistoryStore store = database.Store();
        await store.EnqueueAsync(
            [Snapshot(first, 10, MetricAvailability.Partial), Snapshot(first.AddSeconds(1), 20)],
            TestCancellation);
        await store.FlushAsync(TestCancellation);

        MetricHistoryQueryResult cpu = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            first.AddSeconds(-1),
            first.AddSeconds(2),
            TestCancellation);
        MetricHistoryQueryResult disk = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.PhysicalDiskReadBytesPerSecond,
            first.AddSeconds(-1),
            first.AddSeconds(2),
            TestCancellation);

        Assert.Equal([10, 20], cpu.Points.Select(point => point.Value).ToArray());
        Assert.Equal(MetricAvailability.Partial, cpu.Points[0].Availability);
        Assert.Equal([4d, 4d], disk.Points.Select(point => point.Value).ToArray());
    }

    [Fact]
    public async Task UnavailableMetricPersistsNullWithoutFakeZero()
    {
        using TestDatabase database = new();
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        await using SqliteMetricHistoryStore store = database.Store();
        await store.EnqueueAsync(
            [Snapshot(captured, 0, MetricAvailability.Unavailable)],
            TestCancellation);
        await store.FlushAsync(TestCancellation);

        MetricHistoryPoint point = Assert.Single((await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            captured.AddMinutes(-1),
            captured.AddMinutes(1),
            TestCancellation)).Points);

        Assert.Null(point.Value);
        Assert.Equal(MetricAvailability.Unavailable, point.Availability);
    }

    [Fact]
    public async Task RelaunchAndPidReuseHaveSeparateProcessLifetimeKeys()
    {
        using TestDatabase database = new();
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        await using SqliteMetricHistoryStore store = database.Store();
        await store.EnqueueAsync(
            [Snapshot(captured, 10, processId: 10), Snapshot(captured.AddSeconds(1), 11, processId: 11)],
            TestCancellation);
        await store.FlushAsync(TestCancellation);

        MetricHistoryQueryResult result = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            captured.AddMinutes(-1),
            captured.AddMinutes(1),
            TestCancellation);

        Assert.Equal(2, result.Points.Count);
        Assert.NotEqual(result.Points[0].ProcessLifetimeKey, result.Points[1].ProcessLifetimeKey);
    }

    [Fact]
    public async Task DownsamplesOldRawRowsIntoFiveMinuteBuckets()
    {
        using TestDatabase database = new();
        DateTimeOffset old = DateTimeOffset.UtcNow.AddSeconds(-2);
        SqliteMetricHistoryOptions options = new(database.Path)
        {
            Retention = TimeSpan.FromSeconds(10),
            RawSampleRetention = TimeSpan.FromMilliseconds(1),
            DownsampleBucket = TimeSpan.FromSeconds(1),
            CleanupInterval = TimeSpan.FromMilliseconds(1)
        };
        await using SqliteMetricHistoryStore store = new(options);
        await Task.Delay(10, TestCancellation);
        await store.EnqueueAsync([Snapshot(old, 30)], TestCancellation);
        await store.FlushAsync(TestCancellation);
        Assert.True(
            store.Diagnostics.BatchesWritten > 0,
            $"writes={store.Diagnostics.BatchesWritten}; failures={store.Diagnostics.WriteFailures}; error={store.Diagnostics.LastError}");
        MetricHistoryQueryResult result = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            old.AddSeconds(-1),
            DateTimeOffset.UtcNow.AddSeconds(1),
            TestCancellation);

        Assert.True(
            result.Points.Count == 1,
            $"points={result.Points.Count}; error={result.Error}; store={store.Diagnostics.LastError}");
        MetricHistoryPoint point = result.Points[0];
        Assert.True(point.IsDownsampled);
        Assert.Equal(30, point.Value);
        Assert.Contains("averaged", point.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownsamplingWaitsForAllChunksAndCreatesOneBucket()
    {
        using TestDatabase database = new();
        DateTimeOffset bucket = DateTimeOffset.FromUnixTimeSeconds(
            (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 10 * 10) - 20);
        SqliteMetricHistoryOptions options = new(database.Path)
        {
            Retention = TimeSpan.FromMinutes(1),
            RawSampleRetention = TimeSpan.FromMilliseconds(1),
            DownsampleBucket = TimeSpan.FromSeconds(10),
            CleanupInterval = TimeSpan.FromMilliseconds(1),
            BatchSize = 2
        };
        await using SqliteMetricHistoryStore store = new(options);
        await Task.Delay(10, TestCancellation);
        ApplicationMetricSnapshot[] snapshots = Enumerable.Range(0, 5)
            .Select(index => Snapshot(
                bucket.AddMilliseconds(index),
                index,
                processStartTime: DateTimeOffset.UnixEpoch))
            .ToArray();

        await store.EnqueueAsync(snapshots, TestCancellation);
        await store.FlushAsync(TestCancellation);
        MetricHistoryQueryResult bucketResult = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            bucket.AddSeconds(-1),
            bucket.AddSeconds(11),
            TestCancellation);
        Assert.True(
            bucketResult.Points.Count == 1,
            $"points={bucketResult.Points.Count}; cleanup={store.Diagnostics.CleanupRuns}; batches={store.Diagnostics.BatchesWritten}; error={store.Diagnostics.LastError}; values={string.Join(",", bucketResult.Points.Select(point => $"{point.Value}:{point.IsDownsampled}:{point.TimestampUtc:O}"))}");
        MetricHistoryPoint point = bucketResult.Points[0];

        Assert.True(point.IsDownsampled);
        Assert.Equal(2, point.Value);
        Assert.Equal(1, store.Diagnostics.CleanupRuns);
    }

    [Fact]
    public async Task RetentionRemovesExpiredSamples()
    {
        using TestDatabase database = new();
        DateTimeOffset expired = DateTimeOffset.UtcNow.AddSeconds(-2);
        await using SqliteMetricHistoryStore store = new(new(database.Path)
        {
            Retention = TimeSpan.FromSeconds(1),
            RawSampleRetention = TimeSpan.FromMilliseconds(1),
            DownsampleBucket = TimeSpan.FromMilliseconds(100),
            CleanupInterval = TimeSpan.FromMilliseconds(1)
        });
        await Task.Delay(10, TestCancellation);

        await store.EnqueueAsync([Snapshot(expired, 10)], TestCancellation);
        await store.FlushAsync(TestCancellation);
        MetricHistoryQueryResult result = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            expired.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            TestCancellation);

        Assert.Empty(result.Points);
    }

    [Fact]
    public async Task RetentionChangeAppliesToFutureMaintenanceWithoutMigration()
    {
        using TestDatabase database = new();
        DateTimeOffset expiredUnderNewSetting = DateTimeOffset.UtcNow.AddHours(-7);
        await using SqliteMetricHistoryStore store = new(new(database.Path)
        {
            Retention = TimeSpan.FromHours(24),
            RawSampleRetention = TimeSpan.FromMilliseconds(1),
            DownsampleBucket = TimeSpan.FromMinutes(5),
            CleanupInterval = TimeSpan.FromMilliseconds(1)
        });
        await Task.Delay(10, TestCancellation);

        MetricHistoryRetentionResult updated = await store.UpdateRetentionAsync(
            TimeSpan.FromHours(6),
            TestCancellation);
        await store.EnqueueAsync(
            [Snapshot(expiredUnderNewSetting, 10)],
            TestCancellation);
        await store.FlushAsync(TestCancellation);
        MetricHistoryQueryResult result = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            expiredUnderNewSetting.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            TestCancellation);

        Assert.True(updated.Succeeded);
        Assert.Empty(result.Points);
    }

    [Fact]
    public async Task InvalidRetentionIsRejectedWithoutChangingMaintenancePolicy()
    {
        using TestDatabase database = new();
        await using SqliteMetricHistoryStore store = database.Store();

        MetricHistoryRetentionResult result = await store.UpdateRetentionAsync(
            TimeSpan.FromMinutes(30),
            TestCancellation);

        Assert.False(result.Succeeded);
        Assert.Contains("raw-sample", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SplitsCapturesIntoBoundedTransactionalBatches()
    {
        using TestDatabase database = new();
        await using SqliteMetricHistoryStore store = new(new(database.Path)
        {
            BatchSize = 2
        });
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        ApplicationMetricSnapshot[] snapshots = Enumerable.Range(0, 5)
            .Select(index => Snapshot(
                captured.AddMilliseconds(index),
                index,
                processId: index + 10))
            .ToArray();

        MetricHistoryWriteResult write = await store.EnqueueAsync(snapshots, TestCancellation);
        await store.FlushAsync(TestCancellation);

        Assert.True(write.Accepted);
        Assert.Equal(3, store.Diagnostics.BatchesWritten);
        Assert.Equal(5, store.Diagnostics.SamplesWritten);
    }

    [Fact]
    public async Task ConcurrentQueriesAndWritesRemainOrdered()
    {
        using TestDatabase database = new();
        await using SqliteMetricHistoryStore store = database.Store();
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        Task[] writes = Enumerable.Range(0, 8)
            .Select(index => store.EnqueueAsync(
                [Snapshot(captured.AddMilliseconds(index), index)],
                TestCancellation).AsTask())
            .ToArray();

        await Task.WhenAll(writes);
        MetricHistoryQueryResult result = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            captured.AddSeconds(-1),
            captured.AddSeconds(1),
            TestCancellation);

        Assert.Equal(8, result.Points.Count);
        Assert.Equal(
            result.Points.OrderBy(point => point.TimestampUtc),
            result.Points);
    }

    [Fact]
    public async Task QueueDropsAreBoundedAndDiagnosed()
    {
        using TestDatabase database = new();
        await using SqliteMetricHistoryStore store = new(new(database.Path)
        {
            QueueCapacity = 1
        });

        for (int index = 0; index < 512; index++)
        {
            await store.EnqueueAsync([Snapshot(DateTimeOffset.UtcNow, index)], TestCancellation);
        }

        await store.FlushAsync(TestCancellation);
        Assert.True(store.Diagnostics.QueueDrops > 0);
        Assert.True(store.Diagnostics.QueueDepth <= 1);
    }

    [Fact]
    public async Task CancellationAndIdempotentDisposalAreSafe()
    {
        using TestDatabase database = new();
        await using SqliteMetricHistoryStore store = database.Store();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.EnqueueAsync([Snapshot(DateTimeOffset.UtcNow, 1)], cancelled.Token));
        await store.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task CorruptionIsRecoveredAndReportedWithoutFakeData()
    {
        using TestDatabase database = new();
        await File.WriteAllTextAsync(database.Path, "not sqlite", TestCancellation);
        await using SqliteMetricHistoryStore store = database.Store();

        MetricHistoryQueryResult result = await store.QueryAsync(
            "app.example",
            MetricHistoryMetric.CpuPercent,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1),
            TestCancellation);

        Assert.True(result.IsAvailable, $"{result.Error}; {store.Diagnostics.LastError}");
        Assert.Empty(result.Points);
        Assert.Contains(
            Directory.GetFiles(database.DirectoryPath),
            path => path.Contains(".corrupt-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LockedOrInvalidDatabaseProducesDiagnostics()
    {
        using TestDatabase database = new();
        string invalidPath = Path.Combine(database.DirectoryPath, "as-directory");
        Directory.CreateDirectory(invalidPath);
        await using SqliteMetricHistoryStore store = new(new(invalidPath));
        await store.EnqueueAsync([Snapshot(DateTimeOffset.UtcNow, 1)], TestCancellation);
        await store.FlushAsync(TestCancellation);

        Assert.True(store.Diagnostics.WriteFailures > 0);
        Assert.NotNull(store.Diagnostics.LastError);
    }

    private static ApplicationMetricSnapshot Snapshot(
        DateTimeOffset capturedAt,
        double cpu,
        MetricAvailability availability = MetricAvailability.Available,
        int processId = 10,
        DateTimeOffset? processStartTime = null)
    {
        DateTimeOffset start = processStartTime ?? capturedAt.AddMinutes(-1);
        ProcessDescriptor process = new(
            new ProcessInstanceId(processId, start),
            "example.exe",
            @"C:\Apps\example.exe",
            "Example",
            "Example",
            "Example",
            "Example",
            null,
            false,
            true);
        MetricValue<double> cpuMetric = availability switch
        {
            MetricAvailability.Available => MetricValue<double>.Available(cpu),
            MetricAvailability.Partial => MetricValue<double>.Partial(cpu, "test"),
            MetricAvailability.Unavailable => MetricValue<double>.Unavailable(
                MetricAvailability.Unavailable,
                "test"),
            _ => throw new ArgumentOutOfRangeException(nameof(availability))
        };
        ApplicationMetricSnapshot snapshot = new(
            new ApplicationIdentity(
                "app.example",
                "Example",
                "Example",
                ApplicationDisposition.Installed,
                @"C:\Apps",
                ClassificationConfidence.High,
                "test"),
            capturedAt,
            cpuMetric,
            MetricValue<long>.Available(1024),
            MetricValue<double>.Available(2),
            MetricValue<double>.Available(3),
            MetricValue<ulong>.Available(10),
            MetricValue<ulong>.Available(20),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            1,
            [process])
        {
            PhysicalDisk = new(
                MetricValue<double>.Available(4),
                MetricValue<double>.Available(5),
                MetricValue<ulong>.Available(6),
                MetricValue<ulong>.Available(7),
                MetricValue<ulong>.Available(8),
                MetricValue<ulong>.Available(9),
                new PhysicalDiskCollectorDiagnostics(0, 0, 0, 0, CollectorStatus: MetricAvailability.Available)),
            Network = new(
                MetricValue<double>.Available(12),
                MetricValue<double>.Available(13),
                MetricValue<ulong>.Available(14),
                MetricValue<ulong>.Available(15),
                MetricValue<int>.Available(1),
                MetricValue<int>.Available(1),
                new NetworkCollectorDiagnostics { CollectorStatus = MetricAvailability.Available }),
            Gpu = new(
                MetricValue<double>.Available(45),
                MetricValue<ulong>.Available(100),
                MetricValue<ulong>.Available(200),
                null,
                new GpuCollectorDiagnostics
                {
                    CollectorStatus = MetricAvailability.Available
                })
        };
        return snapshot;
    }

    private sealed class TestDatabase : IDisposable
    {
        public TestDatabase()
        {
            DirectoryPath = global::System.IO.Path.Combine(
                global::System.IO.Path.GetTempPath(),
                $"MonitoringXS.History.{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            Path = global::System.IO.Path.Combine(DirectoryPath, "history.db");
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public SqliteMetricHistoryStore Store() => new(new(Path));

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup retries are unnecessary; the test process owns all connections.
            }
        }
    }
}
