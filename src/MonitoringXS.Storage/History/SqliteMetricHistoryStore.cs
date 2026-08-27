using Microsoft.Data.Sqlite;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using System.Diagnostics;
using System.Globalization;

namespace MonitoringXS.Storage.History;

public sealed class SqliteMetricHistoryStore :
    IMetricHistoryStore,
    IMetricHistoryRetentionController,
    IMetricHistoryDiagnostics
{
    public const int SchemaVersion = 3;
    private const int RawSampleKind = 0;
    private const int DownsampledSampleKind = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private readonly SqliteMetricHistoryOptions _options;
    private readonly Queue<MetricHistoryCapture> _queue = new();
    private readonly SqliteHistorySessionReconciler _sessionReconciler = new();
    private readonly object _queueGate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<MetricHistoryCapture, CancellationToken, Task<int>> _writeBatchAsync;
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
    private bool _initialized;
    private Exception? _terminalFailure;

    public SqliteMetricHistoryStore(SqliteMetricHistoryOptions options)
        : this(options, null)
    {
    }

    internal SqliteMetricHistoryStore(
        SqliteMetricHistoryOptions options,
        Func<MetricHistoryCapture, CancellationToken, Task<int>>? writeBatchAsync)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _retentionTicks = options.Retention.Ticks;
        _lastCleanupUtc = DateTimeOffset.UtcNow;
        _writeBatchAsync = writeBatchAsync ?? WriteBatchAsync;
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

    public async ValueTask<MetricHistoryWriteResult> EnqueueAsync(
        IReadOnlyList<ApplicationMetricSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return MetricHistoryWriteResult.Success;
        }

        bool accepted = true;
        foreach (ApplicationMetricSnapshot[] captureApplications in snapshots
            .GroupBy(snapshot => snapshot.CapturedAt)
            .Select(group => group.ToArray()))
        {
            ProcessDescriptor[] processes = captureApplications
                .SelectMany(snapshot => snapshot.Processes)
                .DistinctBy(process => process.InstanceId)
                .ToArray();
            MetricHistoryWriteResult result = await EnqueueAsync(
                new MetricHistoryCapture(
                    captureApplications[0].CapturedAt,
                    new ProcessDiscoverySnapshot(
                        processes.Select(process => process.InstanceId.ProcessId).Distinct().ToArray(),
                        processes,
                        []),
                    captureApplications),
                cancellationToken).ConfigureAwait(false);
            accepted &= result.Accepted;
        }

        return accepted
            ? MetricHistoryWriteResult.Success
            : MetricHistoryWriteResult.DroppedResult("The history write queue is full.");
    }

    public ValueTask<MetricHistoryWriteResult> EnqueueAsync(
        MetricHistoryCapture capture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(capture.Discovery);
        ArgumentNullException.ThrowIfNull(capture.Applications);
        MetricHistoryCapture copy = capture with
        {
            ObservedAtUtc = capture.ObservedAtUtc.ToUniversalTime(),
            Discovery = capture.Discovery with
            {
                ObservedProcessIds = capture.Discovery.ObservedProcessIds.ToArray(),
                Processes = capture.Discovery.Processes.ToArray(),
                Issues = capture.Discovery.Issues.ToArray()
            },
            Applications = capture.Applications.ToArray()
        };
        lock (_queueGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_terminalFailure is not null)
            {
                return ValueTask.FromException<MetricHistoryWriteResult>(
                    new InvalidOperationException("The history worker has stopped.", _terminalFailure));
            }

            if (_queue.Count >= _options.QueueCapacity)
            {
                Interlocked.Increment(ref _queueDrops);
                SetError($"History write queue is full; dropped capture of {copy.Applications.Count} samples.");
                return ValueTask.FromResult(
                    MetricHistoryWriteResult.DroppedResult("The history write queue is full."));
            }

            if (_queue.Count == 0 && !_writing)
            {
                _currentDrain = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _queue.Enqueue(copy);
            Interlocked.Increment(ref _batchesEnqueued);
            _signal.Release();
            return ValueTask.FromResult(MetricHistoryWriteResult.Success);
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        Task drain;
        lock (_queueGate)
        {
            drain = _terminalFailure is not null
                ? Task.FromException(new InvalidOperationException("The history worker has stopped.", _terminalFailure))
                : _queue.Count == 0 && !_writing
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
        IReadOnlyDictionary<MetricHistoryMetric, MetricHistoryQueryResult> results =
            await QueryManyAsync(
                logicalApplicationId,
                [metric],
                fromUtc,
                toUtc,
                cancellationToken).ConfigureAwait(false);
        return results[metric];
    }

    public async ValueTask<IReadOnlyDictionary<MetricHistoryMetric, MetricHistoryQueryResult>> QueryManyAsync(
        string logicalApplicationId,
        IReadOnlyList<MetricHistoryMetric> metrics,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        MetricHistoryMetric[] requested = metrics.Distinct().ToArray();
        if (string.IsNullOrWhiteSpace(logicalApplicationId))
        {
            return FailedQueries(requested, "A logical application ID is required.");
        }

        if (fromUtc > toUtc)
        {
            return FailedQueries(requested, "The history time range is invalid.");
        }

        if (requested.Length == 0)
        {
            return new Dictionary<MetricHistoryMetric, MetricHistoryQueryResult>();
        }

        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenInitializedConnectionAsync(
                cancellationToken).ConfigureAwait(false);
            string selectedColumns = string.Join(", ", requested.Select(metric =>
            {
                string column = MetricColumn(metric);
                return $"{column}_value, {column}_availability, {column}_detail";
            }));
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT application_session_id, legacy_continuity_key, timestamp_utc,
                       {selectedColumns}, sample_kind
                FROM metric_samples
                WHERE logical_application_id = $application
                  AND timestamp_utc >= $from_utc
                  AND timestamp_utc <= $to_utc
                ORDER BY timestamp_utc, id;
                """;
            command.Parameters.AddWithValue("$application", logicalApplicationId);
            command.Parameters.AddWithValue("$from_utc", ToDatabaseTimestamp(fromUtc));
            command.Parameters.AddWithValue("$to_utc", ToDatabaseTimestamp(toUtc));

            Dictionary<MetricHistoryMetric, List<MetricHistoryPoint>> points = requested
                .ToDictionary(metric => metric, _ => new List<MetricHistoryPoint>());
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long? sessionId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                string? continuityKey = reader.IsDBNull(1) ? null : reader.GetString(1);
                DateTimeOffset timestamp = ParseDatabaseTimestamp(reader.GetString(2));
                bool downsampled = reader.GetInt32(3 + (requested.Length * 3)) == DownsampledSampleKind;
                for (int index = 0; index < requested.Length; index++)
                {
                    MetricHistoryMetric metric = requested[index];
                    int valueIndex = 3 + (index * 3);
                    points[metric].Add(new(
                        logicalApplicationId,
                        sessionId,
                        continuityKey,
                        timestamp,
                        metric,
                        reader.IsDBNull(valueIndex) ? null : reader.GetDouble(valueIndex),
                        (MetricAvailability)reader.GetInt32(valueIndex + 1),
                        reader.IsDBNull(valueIndex + 2) ? null : reader.GetString(valueIndex + 2),
                        downsampled));
                }
            }

            return points.ToDictionary(
                pair => pair.Key,
                pair => new MetricHistoryQueryResult(pair.Value, true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetError(Describe(exception));
            return FailedQueries(requested, "Metric history is unavailable.");
        }
    }

    private static IReadOnlyDictionary<MetricHistoryMetric, MetricHistoryQueryResult> FailedQueries(
        IEnumerable<MetricHistoryMetric> metrics,
        string error) => metrics.ToDictionary(
            metric => metric,
            _ => new MetricHistoryQueryResult([], false, error));

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
            _initializationGate.Dispose();
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

            MetricHistoryCapture capture;
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

                capture = _queue.Dequeue();
                _writing = true;
            }

            Exception? terminalFailure = null;
            try
            {
                int written = await _writeBatchAsync(capture, _shutdown.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _batchesWritten);
                Interlocked.Add(ref _samplesWritten, written);
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
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                terminalFailure = exception;
                Interlocked.Increment(ref _writeFailures);
                SetError(Describe(exception));
                lock (_queueGate)
                {
                    _terminalFailure = exception;
                    _queue.Clear();
                    _currentDrain.TrySetException(
                        new InvalidOperationException("The history worker stopped unexpectedly.", exception));
                }
            }
            finally
            {
                bool idle;
                lock (_queueGate)
                {
                    idle = _queue.Count == 0;
                }
                if (idle && terminalFailure is null)
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

            if (terminalFailure is not null)
            {
                return;
            }
        }
    }

    private async Task<int> WriteBatchAsync(
        MetricHistoryCapture capture,
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

        ApplicationMetricSnapshot[] distinctApplications = capture.Applications
            .DistinctBy(snapshot => snapshot.Application.LogicalApplicationId)
            .ToArray();
        foreach (ApplicationMetricSnapshot snapshot in distinctApplications)
        {
            SetApplicationParameters(applicationCommand, snapshot, snapshot.CapturedAt.ToUniversalTime());
            await applicationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        SqliteHistorySessionReconciler.SessionReconciliation reconciliation =
            await _sessionReconciler.ReconcileAsync(
            connection,
            transaction,
            capture with { Applications = distinctApplications },
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand sampleCommand = connection.CreateCommand();
        sampleCommand.Transaction = transaction;
        sampleCommand.CommandText = """
            INSERT INTO metric_samples (
                logical_application_id, application_session_id, legacy_continuity_key, timestamp_utc,
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
                $id, $session_id, NULL, $timestamp, $sample_kind, $bucket_seconds, $completeness,
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
                $gpu_shared_value, $gpu_shared_availability, $gpu_shared_detail)
            ON CONFLICT DO NOTHING;
            """;

        int written = 0;
        foreach (ApplicationMetricSnapshot snapshot in distinctApplications)
        {
            DateTimeOffset timestamp = snapshot.CapturedAt.ToUniversalTime();
            SetSampleParameters(
                sampleCommand,
                snapshot,
                reconciliation.SessionIds[snapshot.Application.LogicalApplicationId],
                timestamp);
            written += await sampleCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _sessionReconciler.Accept(reconciliation);
        Interlocked.Exchange(ref _databaseBytes, DatabaseBytes());
        return written;
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
                logical_application_id, application_session_id, legacy_continuity_key, timestamp_utc,
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
            SELECT logical_application_id, application_session_id, legacy_continuity_key,
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
            GROUP BY logical_application_id, application_session_id, legacy_continuity_key,
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

        await using SqliteCommand pruneSessions = connection.CreateCommand();
        pruneSessions.Transaction = transaction;
        pruneSessions.CommandText = """
            DELETE FROM application_sessions
            WHERE ended_observed_utc IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM metric_samples
                  WHERE metric_samples.application_session_id = application_sessions.id);
            """;
        await pruneSessions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            _initialized = false;
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
            await EnsureInitializedAsync(connection, cancellationToken).ConfigureAwait(false);
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
        await ExecutePragmaAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);
    }

    private async Task EnsureInitializedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

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

            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializationGate.Release();
        }
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

        if (version == SchemaVersion)
        {
            return;
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
            version = 2;
        }

        if (version < 3)
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE application_sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    logical_application_id TEXT NOT NULL
                        REFERENCES applications(logical_application_id) ON DELETE CASCADE,
                    first_observed_utc TEXT NOT NULL,
                    last_observed_utc TEXT NOT NULL,
                    ended_observed_utc TEXT,
                    end_reason TEXT);
                CREATE UNIQUE INDEX ux_application_sessions_open
                    ON application_sessions(logical_application_id)
                    WHERE ended_observed_utc IS NULL;
                CREATE INDEX ix_application_sessions_app_time
                    ON application_sessions(logical_application_id, first_observed_utc);

                CREATE TABLE process_sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    application_session_id INTEGER NOT NULL
                        REFERENCES application_sessions(id) ON DELETE CASCADE,
                    pid INTEGER NOT NULL,
                    process_start_utc TEXT NOT NULL,
                    first_observed_utc TEXT NOT NULL,
                    last_observed_utc TEXT NOT NULL,
                    ended_observed_utc TEXT,
                    process_name TEXT NOT NULL,
                    executable_path TEXT,
                    publisher TEXT,
                    end_reason TEXT,
                    UNIQUE(pid, process_start_utc));
                CREATE INDEX ix_process_sessions_application
                    ON process_sessions(application_session_id, ended_observed_utc);
                CREATE INDEX ix_process_sessions_pid
                    ON process_sessions(pid, process_start_utc);

                CREATE TABLE metric_samples_v3 (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    logical_application_id TEXT NOT NULL
                        REFERENCES applications(logical_application_id) ON DELETE CASCADE,
                    application_session_id INTEGER
                        REFERENCES application_sessions(id) ON DELETE SET NULL,
                    legacy_continuity_key TEXT,
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
                    gpu_shared_detail TEXT,
                    sample_kind INTEGER NOT NULL DEFAULT 0,
                    bucket_seconds INTEGER NOT NULL DEFAULT 0);

                INSERT INTO metric_samples_v3 (
                    id, logical_application_id, application_session_id,
                    legacy_continuity_key, timestamp_utc, completeness_availability,
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
                    sample_kind, bucket_seconds)
                SELECT id, logical_application_id, NULL,
                    process_lifetime_key, timestamp_utc, completeness_availability,
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
                    sample_kind, bucket_seconds
                FROM metric_samples;

                DROP TABLE metric_samples;
                ALTER TABLE metric_samples_v3 RENAME TO metric_samples;
                CREATE INDEX ix_metric_samples_app_time
                    ON metric_samples(logical_application_id, timestamp_utc);
                CREATE INDEX ix_metric_samples_raw_time
                    ON metric_samples(sample_kind, timestamp_utc);
                CREATE UNIQUE INDEX ux_metric_samples_session_time_kind
                    ON metric_samples(logical_application_id, application_session_id,
                                      timestamp_utc, sample_kind)
                    WHERE application_session_id IS NOT NULL;
                INSERT INTO schema_migrations(version, applied_utc)
                VALUES (3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                PRAGMA user_version = 3;
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
        long applicationSessionId,
        DateTimeOffset timestamp)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", snapshot.Application.LogicalApplicationId);
        command.Parameters.AddWithValue("$session_id", applicationSessionId);
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
