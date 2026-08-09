using Microsoft.Data.Sqlite;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using System.Diagnostics;
using System.Globalization;

namespace MonitoringXS.Storage.History;

public sealed class SqliteMetricHistoryStore :
    IMetricHistoryStore,
    IMetricHistoryRetentionController
{
    public const int SchemaVersion = 2;
    private const int RawSampleKind = 0;
    private const int DownsampledSampleKind = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private readonly SqliteMetricHistoryOptions _options;
    private readonly Queue<IReadOnlyList<ApplicationMetricSnapshot>> _queue = new();
    private readonly object _queueGate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private TaskCompletionSource _currentDrain = CompletedSource();
    private bool _disposed;
    private bool _writing;
    private long _batchesEnqueued;
    private long _batchesWritten;
    private long _samplesWritten;
    private long _queueDrops;
    private long _writeFailures;
    private long _databaseBytes;
    private long _cleanupRuns;
    private long _lastCleanupMicroseconds;
    private long _retentionTicks;
    private string? _lastError;
    private DateTimeOffset _lastCleanupUtc;

    public SqliteMetricHistoryStore(SqliteMetricHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _retentionTicks = options.Retention.Ticks;
        _lastCleanupUtc = DateTimeOffset.UtcNow;
        _worker = Task.Run(WorkerAsync);
    }

    public MetricHistoryStoreDiagnostics Diagnostics
    {
        get
        {
            lock (_queueGate)
            {
                return new(
                    Interlocked.Read(ref _batchesEnqueued),
                    Interlocked.Read(ref _batchesWritten),
                    Interlocked.Read(ref _samplesWritten),
                    Interlocked.Read(ref _queueDrops),
                    Interlocked.Read(ref _writeFailures),
                    _queue.Count + (_writing ? 1 : 0),
                    Interlocked.Read(ref _databaseBytes),
                    Interlocked.Read(ref _cleanupRuns),
                    Interlocked.Read(ref _lastCleanupMicroseconds),
                    _lastError);
            }
        }
    }

    public string DatabasePath => _options.DatabasePath;

    public int QueueCapacity => _options.QueueCapacity;

    public TimeSpan Retention => TimeSpan.FromTicks(Interlocked.Read(ref _retentionTicks));

    public ValueTask<MetricHistoryWriteResult> EnqueueAsync(
        IReadOnlyList<ApplicationMetricSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return ValueTask.FromResult(MetricHistoryWriteResult.Success);
        }

        ApplicationMetricSnapshot[] copy = snapshots.ToArray();
        lock (_queueGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool accepted = true;
            foreach (ApplicationMetricSnapshot[] batch in copy
                .Chunk(_options.BatchSize)
                .Select(chunk => chunk.ToArray()))
            {
                if (_queue.Count >= _options.QueueCapacity)
                {
                    Interlocked.Increment(ref _queueDrops);
                    SetError($"History write queue is full; dropped batch of {batch.Length} samples.");
                    accepted = false;
                    continue;
                }

                if (_queue.Count == 0 && !_writing)
                {
                    _currentDrain = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _queue.Enqueue(batch);
                Interlocked.Increment(ref _batchesEnqueued);
                _signal.Release();
            }

            return ValueTask.FromResult(
                accepted
                    ? MetricHistoryWriteResult.Success
                    : MetricHistoryWriteResult.DroppedResult("The history write queue is full."));
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        Task drain;
        lock (_queueGate)
        {
            drain = _queue.Count == 0 && !_writing
                ? Task.CompletedTask
                : _currentDrain.Task;
        }

        await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MetricHistoryRetentionResult> UpdateRetentionAsync(
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (retention <= _options.RawSampleRetention)
        {
            return ValueTask.FromResult(
                new MetricHistoryRetentionResult(
                    false,
                    "History retention must exceed raw-sample retention."));
        }

        lock (_queueGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Exchange(ref _retentionTicks, retention.Ticks);
        }

        return ValueTask.FromResult(MetricHistoryRetentionResult.Success);
    }

    public async ValueTask<MetricHistoryQueryResult> QueryAsync(
        string logicalApplicationId,
        MetricHistoryMetric metric,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logicalApplicationId))
        {
            return new([], false, "A logical application ID is required.");
        }

        if (fromUtc > toUtc)
        {
            return new([], false, "The history time range is invalid.");
        }

        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenInitializedConnectionAsync(
                cancellationToken).ConfigureAwait(false);
            string column = MetricColumn(metric);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT process_lifetime_key, timestamp_utc, {column}_value,
                       {column}_availability, {column}_detail, sample_kind
                FROM metric_samples
                WHERE logical_application_id = $application
                  AND timestamp_utc >= $from_utc
                  AND timestamp_utc <= $to_utc
                ORDER BY timestamp_utc, id;
                """;
            command.Parameters.AddWithValue("$application", logicalApplicationId);
            command.Parameters.AddWithValue("$from_utc", ToDatabaseTimestamp(fromUtc));
            command.Parameters.AddWithValue("$to_utc", ToDatabaseTimestamp(toUtc));

            List<MetricHistoryPoint> points = [];
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                points.Add(new(
                    logicalApplicationId,
                    reader.GetString(0),
                    ParseDatabaseTimestamp(reader.GetString(1)),
                    metric,
                    reader.IsDBNull(2) ? null : reader.GetDouble(2),
                    (MetricAvailability)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5) == DownsampledSampleKind));
            }

            return new(points, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            SetError(Describe(exception));
            return new([], false, "Metric history is unavailable.");
        }
    }

    public async ValueTask<MetricHistoryApplicationsResult> ListApplicationsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenInitializedConnectionAsync(
                cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT logical_application_id, display_name, disposition, updated_utc
                FROM applications
                ORDER BY display_name COLLATE NOCASE, logical_application_id;
                """;
            List<MetricHistoryApplication> applications = [];
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                applications.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    (ApplicationDisposition)reader.GetInt32(2),
                    ParseDatabaseTimestamp(reader.GetString(3))));
            }

            return new(applications, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            SetError(Describe(exception));
            return new([], false, "Metric history is unavailable.");
        }
    }

    public void Dispose() =>
        DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        lock (_queueGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            SetError(Describe(exception));
        }

        lock (_queueGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        _signal.Release();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _signal.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }

            IReadOnlyList<ApplicationMetricSnapshot>? batch;
            lock (_queueGate)
            {
                if (_queue.Count == 0)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }

                batch = _queue.Dequeue();
                _writing = true;
            }

            try
            {
                await WriteBatchAsync(batch, _shutdown.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _batchesWritten);
                Interlocked.Add(ref _samplesWritten, batch.Count);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                Interlocked.Increment(ref _writeFailures);
                SetError(Describe(exception));
            }
            finally
            {
                bool idle;
                lock (_queueGate)
                {
                    idle = _queue.Count == 0;
                }
                if (idle)
                {
                    try
                    {
                        // Let all chunks released by one capture reach the queue before maintenance.
                        await Task.Delay(TimeSpan.FromMilliseconds(25), _shutdown.Token)
                            .ConfigureAwait(false);
                        lock (_queueGate)
                        {
                            idle = _queue.Count == 0;
                        }
                        if (idle)
                        {
                            await RunCleanupIfDueAsync(_shutdown.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception) when (IsStorageException(exception))
                    {
                        Interlocked.Increment(ref _writeFailures);
                        SetError(Describe(exception));
                    }
                }

                lock (_queueGate)
                {
                    _writing = false;
                    if (_queue.Count == 0)
                    {
                        _currentDrain.TrySetResult();
                    }
                }
            }
        }
    }

    private async Task WriteBatchAsync(
        IReadOnlyList<ApplicationMetricSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenInitializedConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand applicationCommand = connection.CreateCommand();
        applicationCommand.Transaction = transaction;
        applicationCommand.CommandText = """
            INSERT INTO applications (
                logical_application_id, display_name, publisher, disposition,
                installation_path, confidence, classification_reason, updated_utc)
            VALUES ($id, $display_name, $publisher, $disposition, $path,
                    $confidence, $reason, $updated_utc)
            ON CONFLICT(logical_application_id) DO UPDATE SET
                display_name = excluded.display_name,
                publisher = excluded.publisher,
                disposition = excluded.disposition,
                installation_path = excluded.installation_path,
                confidence = excluded.confidence,
                classification_reason = excluded.classification_reason,
                updated_utc = excluded.updated_utc;
            """;

        await using SqliteCommand sampleCommand = connection.CreateCommand();
        sampleCommand.Transaction = transaction;
        sampleCommand.CommandText = """
            INSERT INTO metric_samples (
                logical_application_id, process_lifetime_key, timestamp_utc,
                sample_kind, bucket_seconds, completeness_availability,
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
                gpu_shared_value, gpu_shared_availability, gpu_shared_detail)
            VALUES (
                $id, $lifetime, $timestamp, $sample_kind, $bucket_seconds, $completeness,
                $cpu_value, $cpu_availability, $cpu_detail,
                $working_set_value, $working_set_availability, $working_set_detail,
                $process_io_read_value, $process_io_read_availability, $process_io_read_detail,
                $process_io_write_value, $process_io_write_availability, $process_io_write_detail,
                $disk_read_value, $disk_read_availability, $disk_read_detail,
                $disk_write_value, $disk_write_availability, $disk_write_detail,
                $network_download_value, $network_download_availability, $network_download_detail,
                $network_upload_value, $network_upload_availability, $network_upload_detail,
                $gpu_util_value, $gpu_util_availability, $gpu_util_detail,
                $gpu_dedicated_value, $gpu_dedicated_availability, $gpu_dedicated_detail,
                $gpu_shared_value, $gpu_shared_availability, $gpu_shared_detail);
            """;

        foreach (ApplicationMetricSnapshot snapshot in snapshots)
        {
            DateTimeOffset timestamp = snapshot.CapturedAt.ToUniversalTime();
            string lifetime = ProcessLifetimeKey(snapshot);
            SetApplicationParameters(applicationCommand, snapshot, timestamp);
            await applicationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            SetSampleParameters(sampleCommand, snapshot, lifetime, timestamp);
            await sampleCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _databaseBytes, DatabaseBytes());
    }

    private async Task RunCleanupIfDueAsync(CancellationToken cancellationToken)
    {
        lock (_queueGate)
        {
            if (_queue.Count > 0
                || _signal.CurrentCount > 0
                || DateTimeOffset.UtcNow - _lastCleanupUtc < _options.CleanupInterval)
            {
                return;
            }
        }

        Stopwatch cleanup = Stopwatch.StartNew();
        await using SqliteConnection connection = await OpenInitializedConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await CleanupAsync(connection, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _cleanupRuns);
        Interlocked.Exchange(
            ref _lastCleanupMicroseconds,
            cleanup.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
        _lastCleanupUtc = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _databaseBytes, DatabaseBytes());
    }

    private async Task CleanupAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string rawCutoff = ToDatabaseTimestamp(now - _options.RawSampleRetention);
        TimeSpan retention = TimeSpan.FromTicks(Interlocked.Read(ref _retentionTicks));
        string retentionCutoff = ToDatabaseTimestamp(now - retention);
        int bucketSeconds = checked((int)_options.DownsampleBucket.TotalSeconds);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand downsample = connection.CreateCommand();
        downsample.Transaction = transaction;
        downsample.CommandText = """
            INSERT INTO metric_samples (
                logical_application_id, process_lifetime_key, timestamp_utc,
                sample_kind, bucket_seconds, completeness_availability,
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
                gpu_shared_value, gpu_shared_availability, gpu_shared_detail)
            SELECT logical_application_id, process_lifetime_key,
                MIN(timestamp_utc),
                $sample_kind, $bucket_seconds, MAX(completeness_availability),
                AVG(cpu_value), MAX(cpu_availability), $detail,
                AVG(working_set_value), MAX(working_set_availability), $detail,
                AVG(process_io_read_value), MAX(process_io_read_availability), $detail,
                AVG(process_io_write_value), MAX(process_io_write_availability), $detail,
                AVG(disk_read_value), MAX(disk_read_availability), $detail,
                AVG(disk_write_value), MAX(disk_write_availability), $detail,
                AVG(network_download_value), MAX(network_download_availability), $detail,
                AVG(network_upload_value), MAX(network_upload_availability), $detail,
                AVG(gpu_util_value), MAX(gpu_util_availability), $detail,
                AVG(gpu_dedicated_value), MAX(gpu_dedicated_availability), $detail,
                AVG(gpu_shared_value), MAX(gpu_shared_availability), $detail
            FROM metric_samples
            WHERE sample_kind = $raw_kind
              AND timestamp_utc < $raw_cutoff
              AND timestamp_utc >= $retention_cutoff
            GROUP BY logical_application_id, process_lifetime_key,
                (CAST(strftime('%s', timestamp_utc) AS INTEGER)
                    / $bucket_seconds);
            """;
        downsample.Parameters.AddWithValue("$bucket_seconds", bucketSeconds);
        downsample.Parameters.AddWithValue("$sample_kind", DownsampledSampleKind);
        downsample.Parameters.AddWithValue("$raw_kind", RawSampleKind);
        downsample.Parameters.AddWithValue("$raw_cutoff", rawCutoff);
        downsample.Parameters.AddWithValue("$retention_cutoff", retentionCutoff);
        downsample.Parameters.AddWithValue(
            "$detail",
            "Downsampled five-minute bucket; rate and gauge values averaged.");
        await downsample.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand deleteRaw = connection.CreateCommand();
        deleteRaw.Transaction = transaction;
        deleteRaw.CommandText = """
            DELETE FROM metric_samples
            WHERE sample_kind = $raw_kind
              AND timestamp_utc < $raw_cutoff;
            """;
        deleteRaw.Parameters.AddWithValue("$raw_cutoff", rawCutoff);
        deleteRaw.Parameters.AddWithValue("$raw_kind", RawSampleKind);
        await deleteRaw.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand deleteRetention = connection.CreateCommand();
        deleteRetention.Transaction = transaction;
        deleteRetention.CommandText = """
            DELETE FROM metric_samples
            WHERE timestamp_utc < $retention_cutoff;
            """;
        deleteRetention.Parameters.AddWithValue("$retention_cutoff", retentionCutoff);
        await deleteRetention.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (DatabaseBytes() > _options.MaximumDatabaseBytes)
        {
            await PruneDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PruneDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        for (int pass = 0; pass < 32 && DatabaseBytes() > _options.MaximumDatabaseBytes; pass++)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM metric_samples
                WHERE id IN (
                    SELECT id FROM metric_samples
                    ORDER BY sample_kind DESC, timestamp_utc
                    LIMIT 128);
                """;
            int deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (deleted == 0)
            {
                break;
            }
        }

        SetError("History database exceeded size limit; oldest samples were pruned.");
        Interlocked.Exchange(ref _databaseBytes, DatabaseBytes());
    }

    private async Task<SqliteConnection> OpenInitializedConnectionAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
        try
        {
            return await OpenAndInitializeConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsCorruption(exception))
        {
            RecoverCorruptDatabase();
            return await OpenAndInitializeConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAndInitializeConnectionAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
            Pooling = false
        };
        SqliteConnection connection = new(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
        try
        {
            object? mode = await ExecuteScalarAsync(
                connection,
                "PRAGMA journal_mode = WAL;",
                cancellationToken);
            if (!string.Equals(
                Convert.ToString(mode, CultureInfo.InvariantCulture),
                "wal",
                StringComparison.OrdinalIgnoreCase))
            {
                SetError("SQLite WAL mode is unavailable; using SQLite fallback journal mode.");
            }
        }
        catch (SqliteException exception)
        {
            SetError($"SQLite WAL mode is unavailable ({exception.SqliteErrorCode}).");
        }

        await ExecutePragmaAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        int version = Convert.ToInt32(
            await ExecuteScalarAsync(connection, "PRAGMA user_version;", cancellationToken),
            CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidDataException("The metric history schema is newer than this application.");
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (version < 1)
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS applications (
                    logical_application_id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    publisher TEXT,
                    disposition INTEGER NOT NULL,
                    installation_path TEXT,
                    confidence INTEGER NOT NULL,
                    classification_reason TEXT NOT NULL,
                    updated_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS metric_samples (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    logical_application_id TEXT NOT NULL
                        REFERENCES applications(logical_application_id) ON DELETE CASCADE,
                    process_lifetime_key TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    completeness_availability INTEGER NOT NULL,
                    cpu_value REAL,
                    cpu_availability INTEGER NOT NULL,
                    cpu_detail TEXT,
                    working_set_value REAL,
                    working_set_availability INTEGER NOT NULL,
                    working_set_detail TEXT,
                    process_io_read_value REAL,
                    process_io_read_availability INTEGER NOT NULL,
                    process_io_read_detail TEXT,
                    process_io_write_value REAL,
                    process_io_write_availability INTEGER NOT NULL,
                    process_io_write_detail TEXT,
                    disk_read_value REAL,
                    disk_read_availability INTEGER NOT NULL,
                    disk_read_detail TEXT,
                    disk_write_value REAL,
                    disk_write_availability INTEGER NOT NULL,
                    disk_write_detail TEXT,
                    network_download_value REAL,
                    network_download_availability INTEGER NOT NULL,
                    network_download_detail TEXT,
                    network_upload_value REAL,
                    network_upload_availability INTEGER NOT NULL,
                    network_upload_detail TEXT,
                    gpu_util_value REAL,
                    gpu_util_availability INTEGER NOT NULL,
                    gpu_util_detail TEXT,
                    gpu_dedicated_value REAL,
                    gpu_dedicated_availability INTEGER NOT NULL,
                    gpu_dedicated_detail TEXT,
                    gpu_shared_value REAL,
                    gpu_shared_availability INTEGER NOT NULL,
                    gpu_shared_detail TEXT);
                INSERT OR IGNORE INTO schema_migrations(version, applied_utc)
                VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                PRAGMA user_version = 1;
                """, cancellationToken);
            version = 1;
        }

        if (version < 2)
        {
            await ExecuteAsync(connection, transaction, """
                ALTER TABLE metric_samples
                    ADD COLUMN sample_kind INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE metric_samples
                    ADD COLUMN bucket_seconds INTEGER NOT NULL DEFAULT 0;
                CREATE INDEX IF NOT EXISTS ix_metric_samples_app_time
                    ON metric_samples(logical_application_id, timestamp_utc);
                CREATE INDEX IF NOT EXISTS ix_metric_samples_raw_time
                    ON metric_samples(sample_kind, timestamp_utc);
                INSERT OR IGNORE INTO schema_migrations(version, applied_utc)
                VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                PRAGMA user_version = 2;
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void SetApplicationParameters(
        SqliteCommand command,
        ApplicationMetricSnapshot snapshot,
        DateTimeOffset timestamp)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", snapshot.Application.LogicalApplicationId);
        command.Parameters.AddWithValue("$display_name", snapshot.Application.DisplayName);
        command.Parameters.AddWithValue("$publisher", (object?)snapshot.Application.Publisher ?? DBNull.Value);
        command.Parameters.AddWithValue("$disposition", (int)snapshot.Application.Disposition);
        command.Parameters.AddWithValue(
            "$path",
            (object?)snapshot.Application.InstallationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", (int)snapshot.Application.Confidence);
        command.Parameters.AddWithValue("$reason", snapshot.Application.ClassificationReason);
        command.Parameters.AddWithValue("$updated_utc", ToDatabaseTimestamp(timestamp));
    }

    private static void SetSampleParameters(
        SqliteCommand command,
        ApplicationMetricSnapshot snapshot,
        string lifetime,
        DateTimeOffset timestamp)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", snapshot.Application.LogicalApplicationId);
        command.Parameters.AddWithValue("$lifetime", lifetime);
        command.Parameters.AddWithValue("$timestamp", ToDatabaseTimestamp(timestamp));
        command.Parameters.AddWithValue("$sample_kind", RawSampleKind);
        command.Parameters.AddWithValue("$bucket_seconds", 0);
        command.Parameters.AddWithValue("$completeness", (int)Completeness(snapshot));
        AddMetric(command, "cpu", snapshot.CpuPercent, value => value);
        AddMetric(command, "working_set", snapshot.WorkingSetBytes, value => value);
        AddMetric(command, "process_io_read", snapshot.IoReadBytesPerSecond, value => value);
        AddMetric(command, "process_io_write", snapshot.IoWriteBytesPerSecond, value => value);
        AddMetric(command, "disk_read", snapshot.PhysicalDisk.ReadBytesPerSecond, value => value);
        AddMetric(command, "disk_write", snapshot.PhysicalDisk.WriteBytesPerSecond, value => value);
        AddMetric(command, "network_download", snapshot.Network.DownloadBytesPerSecond, value => value);
        AddMetric(command, "network_upload", snapshot.Network.UploadBytesPerSecond, value => value);
        AddMetric(command, "gpu_util", snapshot.Gpu.UtilizationPercent, value => value);
        AddMetric(command, "gpu_dedicated", snapshot.Gpu.DedicatedMemoryBytes, Convert.ToDouble);
        AddMetric(command, "gpu_shared", snapshot.Gpu.SharedMemoryBytes, Convert.ToDouble);
    }

    private static void AddMetric<T>(
        SqliteCommand command,
        string name,
        MetricValue<T> metric,
        Func<T, double> convert)
        where T : struct
    {
        command.Parameters.AddWithValue(
            $"${name}_value",
            metric.Value.HasValue ? convert(metric.Value.Value) : DBNull.Value);
        command.Parameters.AddWithValue($"${name}_availability", (int)metric.Availability);
        command.Parameters.AddWithValue(
            $"${name}_detail",
            (object?)BoundDetail(metric.Detail) ?? DBNull.Value);
    }

    private static MetricAvailability Completeness(ApplicationMetricSnapshot snapshot)
    {
        MetricAvailability[] states =
        [
            snapshot.CpuPercent.Availability,
            snapshot.WorkingSetBytes.Availability,
            snapshot.IoReadBytesPerSecond.Availability,
            snapshot.IoWriteBytesPerSecond.Availability,
            snapshot.PhysicalDisk.ReadBytesPerSecond.Availability,
            snapshot.PhysicalDisk.WriteBytesPerSecond.Availability,
            snapshot.Network.DownloadBytesPerSecond.Availability,
            snapshot.Network.UploadBytesPerSecond.Availability,
            snapshot.Gpu.UtilizationPercent.Availability,
            snapshot.Gpu.DedicatedMemoryBytes.Availability,
            snapshot.Gpu.SharedMemoryBytes.Availability
        ];
        foreach (MetricAvailability state in new[]
        {
            MetricAvailability.Error,
            MetricAvailability.AccessDenied,
            MetricAvailability.Unsupported,
            MetricAvailability.Unavailable,
            MetricAvailability.WarmingUp,
            MetricAvailability.Partial
        })
        {
            if (states.Contains(state))
            {
                return state;
            }
        }

        return MetricAvailability.Available;
    }

    private static string ProcessLifetimeKey(ApplicationMetricSnapshot snapshot)
    {
        string material = string.Join(
            "|",
            snapshot.Processes
                .Select(process => $"{process.InstanceId.ProcessId}:{process.InstanceId.StartTimeUtc.UtcTicks}")
                .OrderBy(value => value, StringComparer.Ordinal));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material)));
    }

    private static string MetricColumn(MetricHistoryMetric metric) => metric switch
    {
        MetricHistoryMetric.CpuPercent => "cpu",
        MetricHistoryMetric.WorkingSetBytes => "working_set",
        MetricHistoryMetric.ProcessIoReadBytesPerSecond => "process_io_read",
        MetricHistoryMetric.ProcessIoWriteBytesPerSecond => "process_io_write",
        MetricHistoryMetric.PhysicalDiskReadBytesPerSecond => "disk_read",
        MetricHistoryMetric.PhysicalDiskWriteBytesPerSecond => "disk_write",
        MetricHistoryMetric.NetworkDownloadBytesPerSecond => "network_download",
        MetricHistoryMetric.NetworkUploadBytesPerSecond => "network_upload",
        MetricHistoryMetric.GpuUtilizationPercent => "gpu_util",
        MetricHistoryMetric.GpuDedicatedMemoryBytes => "gpu_dedicated",
        MetricHistoryMetric.GpuSharedMemoryBytes => "gpu_shared",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    private long DatabaseBytes()
    {
        try
        {
            long bytes = new FileInfo(_options.DatabasePath).Length;
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                string sidecar = _options.DatabasePath + suffix;
                if (File.Exists(sidecar))
                {
                    bytes += new FileInfo(sidecar).Length;
                }
            }

            return bytes;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void RecoverCorruptDatabase()
    {
        if (!File.Exists(_options.DatabasePath))
        {
            return;
        }

        string backup = $"{_options.DatabasePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            File.Move(_options.DatabasePath, backup);
        }
        catch (IOException)
        {
            File.Copy(_options.DatabasePath, backup);
            File.Delete(_options.DatabasePath);
        }

        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string sidecar = _options.DatabasePath + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        SetError("SQLite database was corrupt; a recovered database was created.");
    }

    private void SetError(string error)
    {
        lock (_queueGate)
        {
            _lastError = error;
        }
    }

    private static string ToDatabaseTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss.fffffff",
            CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDatabaseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string? BoundDetail(string? detail) =>
        detail is { Length: > 512 } ? detail[..512] : detail;

    private static bool IsCorruption(SqliteException exception) =>
        exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase
            || exception.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase);

    private static bool IsStorageException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or SqliteException
            or NotSupportedException;

    private static string Describe(Exception exception) =>
        exception is SqliteException sqlite
            ? $"SQLite error {sqlite.SqliteErrorCode} ({sqlite.SqliteExtendedErrorCode})."
            : $"Metric history storage failed ({exception.GetType().Name}).";

    private static TaskCompletionSource CompletedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

}
