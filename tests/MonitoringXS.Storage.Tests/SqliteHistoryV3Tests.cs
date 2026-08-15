using Microsoft.Data.Sqlite;
using MonitoringXS.Core.Models;
using MonitoringXS.Storage.History;

namespace MonitoringXS.Storage.Tests;

public sealed class SqliteHistoryV3Tests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task V2MigrationPreservesEverySampleFieldWithoutInventingSessions()
    {
        using TestDatabase database = new();
        DateTimeOffset timestamp = new(2026, 8, 15, 10, 11, 12, 345, TimeSpan.Zero);
        await CreateV2Async(database.Path, timestamp, malformed: false);

        await using (SqliteMetricHistoryStore store = database.Store())
        {
            MetricHistoryPoint point = Assert.Single((await store.QueryAsync(
                "legacy.app",
                MetricHistoryMetric.CpuPercent,
                timestamp.AddSeconds(-1),
                timestamp.AddSeconds(1),
                TestCancellation)).Points);
            Assert.Equal(timestamp, point.TimestampUtc);
            Assert.Equal(1.25, point.Value);
            Assert.Equal(MetricAvailability.Partial, point.Availability);
            Assert.Equal("cpu detail", point.Detail);
            Assert.Null(point.ApplicationSessionId);
            Assert.Equal("V2-HASH", point.LegacyContinuityKey);
        }

        await using SqliteConnection connection = await OpenAsync(database.Path);
        Assert.Equal(3L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM application_sessions;"));
        Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM process_sessions;"));
        await using SqliteCommand values = connection.CreateCommand();
        values.CommandText = """
            SELECT timestamp_utc, completeness_availability,
                   cpu_value, cpu_availability, cpu_detail,
                   working_set_value, working_set_availability, working_set_detail,
                   process_io_read_value, process_io_read_availability, process_io_read_detail,
                   process_io_write_value, process_io_write_availability, process_io_write_detail,
                   disk_read_value, disk_read_availability, disk_read_detail,
                   disk_write_value, disk_write_availability, disk_write_detail,
                   network_download_value, network_download_availability, network_download_detail,
                   network_upload_value, network_upload_availability, network_upload_detail,
                   gpu_util_value, gpu_util_availability, gpu_util_detail,
                   gpu_dedicated_value, gpu_dedicated_availability, gpu_dedicated_detail,
                   gpu_shared_value, gpu_shared_availability, gpu_shared_detail,
                   sample_kind, bucket_seconds, application_session_id, legacy_continuity_key
            FROM metric_samples;
            """;
        await using SqliteDataReader reader = await values.ExecuteReaderAsync(TestCancellation);
        Assert.True(await reader.ReadAsync(TestCancellation));
        Assert.Equal("2026-08-15 10:11:12.3450000", reader.GetString(0));
        Assert.Equal((int)MetricAvailability.Partial, reader.GetInt32(1));
        for (int metric = 0; metric < 11; metric++)
        {
            int offset = 2 + metric * 3;
            Assert.Equal(1.25 + metric, reader.GetDouble(offset));
            Assert.Equal((int)MetricAvailability.Partial + metric % 2, reader.GetInt32(offset + 1));
            Assert.Equal(metric == 0 ? "cpu detail" : $"detail {metric}", reader.GetString(offset + 2));
        }
        Assert.Equal(1, reader.GetInt32(35));
        Assert.Equal(300, reader.GetInt32(36));
        Assert.True(reader.IsDBNull(37));
        Assert.Equal("V2-HASH", reader.GetString(38));
    }

    [Fact]
    public async Task FailedMigrationRollsBackAndNewerSchemaIsRejectedWithoutReset()
    {
        using TestDatabase malformed = new();
        await CreateV2Async(malformed.Path, DateTimeOffset.UtcNow, malformed: true);
        await using (SqliteMetricHistoryStore store = malformed.Store())
        {
            MetricHistoryQueryResult result = await store.QueryAsync(
                "legacy.app",
                MetricHistoryMetric.CpuPercent,
                DateTimeOffset.MinValue,
                DateTimeOffset.MaxValue,
                TestCancellation);
            Assert.False(result.IsAvailable);
        }

        await using (SqliteConnection connection = await OpenAsync(malformed.Path))
        {
            Assert.Equal(2L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
            Assert.Equal(0L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='application_sessions';"));
        }
        Assert.Empty(Directory.GetFiles(malformed.DirectoryPath, "*.corrupt-*"));

        using TestDatabase newer = new();
        await using (SqliteConnection connection = await OpenAsync(newer.Path))
        {
            await ExecuteAsync(connection, "PRAGMA user_version = 4;");
        }
        await using SqliteMetricHistoryStore newerStore = newer.Store();
        MetricHistoryApplicationsResult newerResult = await newerStore.ListApplicationsAsync(TestCancellation);
        Assert.False(newerResult.IsAvailable);
        Assert.Contains("InvalidDataException", newerStore.Diagnostics.LastError, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(newer.DirectoryPath, "*.corrupt-*"));
    }

    [Fact]
    public async Task HelperChurnPartialDiscoveryAndRelaunchFollowApplicationLifetime()
    {
        using TestDatabase database = new();
        DateTimeOffset start = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
        ProcessDescriptor main = Process(100, start.AddMinutes(-1), "main.exe");
        ProcessDescriptor helper = Process(101, start, "helper.exe");
        ProcessDescriptor relaunched = Process(102, start.AddMinutes(10), "main.exe");
        await using SqliteMetricHistoryStore store = database.Store();

        await WriteAsync(store, start, [main], [main]);
        await WriteAsync(store, start.AddSeconds(1), [main, helper], [main, helper]);
        await WriteAsync(
            store,
            start.AddSeconds(2),
            [main],
            [main],
            observedPids: [100, 101],
            issues: [new(101, ProcessDiscoveryIssueKind.ProcessExited)]);
        await WriteAsync(store, start.AddSeconds(3), [main], [main]);
        await WriteAsync(store, start.AddSeconds(4), [], [], observedPids: []);
        await WriteAsync(store, start.AddSeconds(5), [relaunched], [relaunched]);
        await store.FlushAsync(TestCancellation);

        await using SqliteConnection connection = await OpenAsync(database.Path);
        Assert.Equal(2L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM application_sessions;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM application_sessions WHERE ended_observed_utc IS NULL;"));
        Assert.Equal(3L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM process_sessions;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM process_sessions WHERE pid=101 AND ended_observed_utc IS NOT NULL;"));
        Assert.Equal("NoLongerObserved", await ScalarStringAsync(connection, "SELECT end_reason FROM process_sessions WHERE pid=101;"));
        Assert.Equal("2026-08-15 08:00:04.0000000", await ScalarStringAsync(connection, "SELECT ended_observed_utc FROM application_sessions WHERE ended_observed_utc IS NOT NULL;"));
        Assert.Equal(5L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM metric_samples;"));
    }

    [Fact]
    public async Task PidReuseCreatesOneNewProcessWithoutSplittingOrDuplicatingApplicationSession()
    {
        using TestDatabase database = new();
        DateTimeOffset observed = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);
        ProcessDescriptor first = Process(200, observed.AddMinutes(-2), "app.exe", executablePath: null);
        ProcessDescriptor reused = Process(200, observed.AddMinutes(1), "app.exe");
        await using SqliteMetricHistoryStore store = database.Store();

        await WriteAsync(
            store,
            observed,
            [first],
            [first],
            issues: [new(200, ProcessDiscoveryIssueKind.AccessDenied)]);
        await WriteAsync(store, observed.AddSeconds(1), [reused], [reused]);
        await WriteAsync(store, observed.AddSeconds(1), [reused], [reused]);
        await store.FlushAsync(TestCancellation);

        await using SqliteConnection connection = await OpenAsync(database.Path);
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM application_sessions;"));
        Assert.Equal(2L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM process_sessions;"));
        Assert.Equal("PidReused", await ScalarStringAsync(connection, "SELECT end_reason FROM process_sessions WHERE ended_observed_utc IS NOT NULL;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM process_sessions WHERE ended_observed_utc IS NULL;"));
        Assert.Equal(2L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM metric_samples;"));
        Assert.Equal(2, store.Diagnostics.SamplesWritten);
    }

    [Fact]
    public async Task RestartAndSleepContinueOnlyWhenPreviousProcessIdentityIsAlive()
    {
        using TestDatabase continued = new();
        DateTimeOffset observed = new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        ProcessDescriptor process = Process(300, observed.AddHours(-1), "sleepy.exe");
        await using (SqliteMetricHistoryStore first = continued.Store())
        {
            await WriteAsync(first, observed, [process], [process]);
            await first.FlushAsync(TestCancellation);
        }
        await using (SqliteMetricHistoryStore restarted = continued.Store())
        {
            await WriteAsync(restarted, observed.AddHours(3), [process], [process]);
            await restarted.FlushAsync(TestCancellation);
        }
        await using (SqliteConnection connection = await OpenAsync(continued.Path))
        {
            Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM application_sessions;"));
            Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM process_sessions;"));
        }

        using TestDatabase replaced = new();
        ProcessDescriptor oldProcess = Process(400, observed.AddMinutes(-1), "restart.exe");
        ProcessDescriptor newProcess = Process(401, observed.AddMinutes(1), "restart.exe");
        await using (SqliteMetricHistoryStore first = replaced.Store())
        {
            await WriteAsync(first, observed, [oldProcess], [oldProcess]);
            await first.FlushAsync(TestCancellation);
        }
        await using (SqliteMetricHistoryStore restarted = replaced.Store())
        {
            await WriteAsync(restarted, observed.AddSeconds(5), [newProcess], [newProcess]);
            await restarted.FlushAsync(TestCancellation);
        }
        await using (SqliteConnection connection = await OpenAsync(replaced.Path))
        {
            Assert.Equal(2L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM application_sessions;"));
            Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM application_sessions WHERE ended_observed_utc IS NULL;"));
            Assert.Equal(2L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM process_sessions;"));
        }
    }

    private static async Task WriteAsync(
        SqliteMetricHistoryStore store,
        DateTimeOffset observedAt,
        IReadOnlyList<ProcessDescriptor> applicationProcesses,
        IReadOnlyList<ProcessDescriptor> materializedProcesses,
        IReadOnlyList<int>? observedPids = null,
        IReadOnlyList<ProcessDiscoveryIssue>? issues = null)
    {
        ApplicationMetricSnapshot[] applications = applicationProcesses.Count == 0
            ? []
            : [Snapshot(observedAt, applicationProcesses)];
        await store.EnqueueAsync(
            new MetricHistoryCapture(
                observedAt,
                new ProcessDiscoverySnapshot(
                    observedPids ?? materializedProcesses.Select(process => process.InstanceId.ProcessId).ToArray(),
                    materializedProcesses,
                    issues ?? []),
                applications),
            TestCancellation);
    }

    private static ApplicationMetricSnapshot Snapshot(
        DateTimeOffset capturedAt,
        IReadOnlyList<ProcessDescriptor> processes) => new(
            new ApplicationIdentity(
                "app.example",
                "Example",
                "Publisher",
                ApplicationDisposition.Installed,
                @"C:\Apps",
                ClassificationConfidence.High,
                "test"),
            capturedAt,
            MetricValue<double>.Available(10),
            MetricValue<long>.Available(1024),
            MetricValue<double>.Available(2),
            MetricValue<double>.Available(3),
            MetricValue<ulong>.Available(10),
            MetricValue<ulong>.Available(20),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(1),
            processes.Count,
            processes);

    private static ProcessDescriptor Process(
        int pid,
        DateTimeOffset start,
        string name,
        string? executablePath = @"C:\Apps\example.exe") => new(
            new ProcessInstanceId(pid, start),
            name,
            executablePath,
            "Example",
            "Example",
            "Publisher",
            null,
            null,
            false,
            true);

    private static async Task CreateV2Async(
        string path,
        DateTimeOffset timestamp,
        bool malformed)
    {
        await using SqliteConnection connection = await OpenAsync(path);
        string lastColumn = malformed ? "" : ", gpu_shared_detail TEXT";
        await ExecuteAsync(connection, $$"""
            CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
            INSERT INTO schema_migrations VALUES (1, '2026-01-01');
            INSERT INTO schema_migrations VALUES (2, '2026-01-02');
            CREATE TABLE applications (
                logical_application_id TEXT PRIMARY KEY, display_name TEXT NOT NULL,
                publisher TEXT, disposition INTEGER NOT NULL, installation_path TEXT,
                confidence INTEGER NOT NULL, classification_reason TEXT NOT NULL, updated_utc TEXT NOT NULL);
            INSERT INTO applications VALUES ('legacy.app','Legacy','Publisher',0,'C:\\Legacy',2,'fixture','2026-08-15 10:11:12.3450000');
            CREATE TABLE metric_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT, logical_application_id TEXT NOT NULL,
                process_lifetime_key TEXT NOT NULL, timestamp_utc TEXT NOT NULL,
                completeness_availability INTEGER NOT NULL,
                cpu_value REAL, cpu_availability INTEGER NOT NULL, cpu_detail TEXT,
                working_set_value REAL, working_set_availability INTEGER NOT NULL, working_set_detail TEXT,
                process_io_read_value REAL, process_io_read_availability INTEGER NOT NULL, process_io_read_detail TEXT,
                process_io_write_value REAL, process_io_write_availability INTEGER NOT NULL, process_io_write_detail TEXT,
                disk_read_value REAL, disk_read_availability INTEGER NOT NULL, disk_read_detail TEXT,
                disk_write_value REAL, disk_write_availability INTEGER NOT NULL, disk_write_detail TEXT,
                network_download_value REAL, network_download_availability INTEGER NOT NULL, network_download_detail TEXT,
                network_upload_value REAL, network_upload_availability INTEGER NOT NULL, network_upload_detail TEXT,
                gpu_util_value REAL, gpu_util_availability INTEGER NOT NULL, gpu_util_detail TEXT,
                gpu_dedicated_value REAL, gpu_dedicated_availability INTEGER NOT NULL, gpu_dedicated_detail TEXT,
                gpu_shared_value REAL, gpu_shared_availability INTEGER NOT NULL{{lastColumn}},
                sample_kind INTEGER NOT NULL DEFAULT 0, bucket_seconds INTEGER NOT NULL DEFAULT 0);
            PRAGMA user_version = 2;
            """);
        if (malformed)
        {
            return;
        }

        await ExecuteAsync(connection, """
            INSERT INTO metric_samples VALUES (
                1,'legacy.app','V2-HASH','2026-08-15 10:11:12.3450000',1,
                1.25,1,'cpu detail',2.25,2,'detail 1',3.25,1,'detail 2',
                4.25,2,'detail 3',5.25,1,'detail 4',6.25,2,'detail 5',
                7.25,1,'detail 6',8.25,2,'detail 7',9.25,1,'detail 8',
                10.25,2,'detail 9',11.25,1,'detail 10',1,300);
            """);
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestCancellation);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestCancellation);
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(TestCancellation))!;
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(TestCancellation))!;
    }

    private sealed class TestDatabase : IDisposable
    {
        public TestDatabase()
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MonitoringXS.HistoryV3.{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "history.db");
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
            }
        }
    }
}
