using System.Globalization;
using Microsoft.Data.Sqlite;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Storage.History;

internal sealed class SqliteHistorySessionReconciler
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private Dictionary<string, ActiveApplication> _active = new(StringComparer.Ordinal);
    private bool _loaded;

    public async Task<SessionReconciliation> ReconcileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MetricHistoryCapture capture,
        CancellationToken cancellationToken)
    {
        DateTimeOffset observedAt = capture.ObservedAtUtc.ToUniversalTime();
        Dictionary<string, ActiveApplication> active = _loaded
            ? Clone(_active)
            : await LoadActiveAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        bool restartReconciliation = !_loaded;
        HashSet<int> observedPids = capture.Discovery.ObservedProcessIds.ToHashSet();
        Dictionary<int, ProcessDescriptor> descriptorsByPid = capture.Discovery.Processes
            .GroupBy(process => process.InstanceId.ProcessId)
            .ToDictionary(group => group.Key, group => group.Last());
        Dictionary<string, ApplicationMetricSnapshot> observedApplications = capture.Applications
            .ToDictionary(snapshot => snapshot.Application.LogicalApplicationId, StringComparer.Ordinal);

        if (restartReconciliation)
        {
            foreach ((string logicalId, ApplicationMetricSnapshot snapshot) in observedApplications)
            {
                if (!active.TryGetValue(logicalId, out ActiveApplication? previous)
                    || previous.Processes.Values.Any(process =>
                        IsIdentityAlive(process.InstanceId, observedPids, descriptorsByPid)))
                {
                    continue;
                }

                await EndApplicationAsync(
                    connection,
                    transaction,
                    previous,
                    observedAt,
                    "NoLongerObserved",
                    descriptorsByPid,
                    cancellationToken).ConfigureAwait(false);
                active.Remove(logicalId);
            }
        }
        foreach (ActiveApplication application in active.Values.ToArray())
        {
            foreach (ActiveProcess process in application.Processes.Values.ToArray())
            {
                string? reason = EndReason(process.InstanceId, observedPids, descriptorsByPid);
                if (reason is null)
                {
                    continue;
                }

                await EndProcessAsync(
                    connection,
                    transaction,
                    process.Id,
                    observedAt,
                    reason,
                    cancellationToken).ConfigureAwait(false);
                application.Processes.Remove(process.InstanceId);
            }
        }

        Dictionary<ProcessInstanceId, ActiveProcess> activeProcesses = active.Values
            .SelectMany(application => application.Processes.Values)
            .ToDictionary(process => process.InstanceId);

        Dictionary<string, long> sessionIds = new(StringComparer.Ordinal);
        foreach ((string logicalId, ApplicationMetricSnapshot snapshot) in observedApplications)
        {
            if (!active.TryGetValue(logicalId, out ActiveApplication? application))
            {
                application = await StartApplicationAsync(
                    connection,
                    transaction,
                    logicalId,
                    observedAt,
                    cancellationToken).ConfigureAwait(false);
                active.Add(logicalId, application);
            }

            foreach (ProcessDescriptor descriptor in snapshot.Processes.DistinctBy(item => item.InstanceId))
            {
                if (activeProcesses.TryGetValue(descriptor.InstanceId, out ActiveProcess? existing))
                {
                    await EnrichProcessAsync(
                        connection,
                        transaction,
                        existing,
                        descriptor,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ActiveProcess process = await StartProcessAsync(
                    connection,
                    transaction,
                    application.Id,
                    descriptor,
                    observedAt,
                    cancellationToken).ConfigureAwait(false);
                application.Processes.Add(process.InstanceId, process);
                activeProcesses.Add(process.InstanceId, process);
            }

            sessionIds.Add(logicalId, application.Id);
        }

        foreach ((string logicalId, ActiveApplication application) in active)
        {
            ActiveProcess[] liveProcesses = application.Processes.Values.Where(process =>
                IsIdentityAlive(process.InstanceId, observedPids, descriptorsByPid)).ToArray();
            if (observedApplications.ContainsKey(logicalId) || liveProcesses.Length > 0)
            {
                application.LastObservedUtc = observedAt;
                if (observedAt - application.LastPersistedUtc >= HeartbeatInterval)
                {
                    await UpdateApplicationHeartbeatAsync(
                        connection,
                        transaction,
                        application.Id,
                        observedAt,
                        cancellationToken).ConfigureAwait(false);
                    application.LastPersistedUtc = observedAt;
                }
            }

            foreach (ActiveProcess process in liveProcesses)
            {
                process.LastObservedUtc = observedAt;
                if (observedAt - process.LastPersistedUtc < HeartbeatInterval)
                {
                    continue;
                }

                await UpdateProcessHeartbeatAsync(
                    connection,
                    transaction,
                    process.Id,
                    observedAt,
                    cancellationToken).ConfigureAwait(false);
                process.LastPersistedUtc = observedAt;
            }
        }

        foreach ((string logicalId, ActiveApplication application) in active.ToArray())
        {
            if (observedApplications.ContainsKey(logicalId) || application.Processes.Count > 0)
            {
                continue;
            }

            await EndApplicationRowAsync(
                connection,
                transaction,
                application.Id,
                observedAt,
                "NoLongerObserved",
                cancellationToken).ConfigureAwait(false);
            active.Remove(logicalId);
        }

        return new SessionReconciliation(active, sessionIds);
    }

    public void Accept(SessionReconciliation reconciliation)
    {
        _active = reconciliation.Active;
        _loaded = true;
    }

    private static async Task<Dictionary<string, ActiveApplication>> LoadActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ActiveApplication> active = new(StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.id, a.logical_application_id, a.last_observed_utc,
                   p.id, p.pid, p.process_start_utc, p.last_observed_utc,
                   p.process_name, p.executable_path, p.publisher
            FROM application_sessions a
            LEFT JOIN process_sessions p
              ON p.application_session_id = a.id
             AND p.ended_observed_utc IS NULL
            WHERE a.ended_observed_utc IS NULL
            ORDER BY a.id, p.id;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long applicationId = reader.GetInt64(0);
            string logicalId = reader.GetString(1);
            DateTimeOffset applicationLast = ParseTimestamp(reader.GetString(2));
            if (!active.TryGetValue(logicalId, out ActiveApplication? application))
            {
                application = new(applicationId, logicalId, applicationLast, applicationLast, []);
                active.Add(logicalId, application);
            }

            if (reader.IsDBNull(3))
            {
                continue;
            }

            DateTimeOffset processLast = ParseTimestamp(reader.GetString(6));
            ActiveProcess process = new(
                reader.GetInt64(3),
                new ProcessInstanceId(reader.GetInt32(4), ParseTimestamp(reader.GetString(5))),
                processLast,
                processLast,
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));
            application.Processes.Add(process.InstanceId, process);
        }

        return active;
    }

    private static async Task<ActiveApplication> StartApplicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string logicalId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO application_sessions (
                logical_application_id, first_observed_utc, last_observed_utc)
            VALUES ($logical_id, $observed, $observed)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$logical_id", logicalId);
        command.Parameters.AddWithValue("$observed", Timestamp(observedAt));
        long id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new(id, logicalId, observedAt, observedAt, []);
    }

    private static async Task<ActiveProcess> StartProcessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long applicationSessionId,
        ProcessDescriptor descriptor,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO process_sessions (
                application_session_id, pid, process_start_utc,
                first_observed_utc, last_observed_utc, process_name,
                executable_path, publisher)
            VALUES ($application_session_id, $pid, $process_start,
                    $observed, $observed, $process_name, $path, $publisher)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$application_session_id", applicationSessionId);
        command.Parameters.AddWithValue("$pid", descriptor.InstanceId.ProcessId);
        command.Parameters.AddWithValue("$process_start", Timestamp(descriptor.InstanceId.StartTimeUtc));
        command.Parameters.AddWithValue("$observed", Timestamp(observedAt));
        command.Parameters.AddWithValue("$process_name", descriptor.ProcessName);
        command.Parameters.AddWithValue("$path", (object?)descriptor.ExecutablePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$publisher", (object?)descriptor.Publisher ?? DBNull.Value);
        long id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new(
            id,
            descriptor.InstanceId,
            observedAt,
            observedAt,
            descriptor.ProcessName,
            descriptor.ExecutablePath,
            descriptor.Publisher);
    }

    private static async Task EnrichProcessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveProcess process,
        ProcessDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        bool hasNewPath = process.ExecutablePath is null && descriptor.ExecutablePath is not null;
        bool hasNewPublisher = process.Publisher is null && descriptor.Publisher is not null;
        if (!hasNewPath && !hasNewPublisher)
        {
            return;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE process_sessions
            SET executable_path = COALESCE(executable_path, $path),
                publisher = COALESCE(publisher, $publisher)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", process.Id);
        command.Parameters.AddWithValue("$path", (object?)descriptor.ExecutablePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$publisher", (object?)descriptor.Publisher ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        process.ExecutablePath ??= descriptor.ExecutablePath;
        process.Publisher ??= descriptor.Publisher;
    }

    private static async Task EndApplicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActiveApplication application,
        DateTimeOffset observedAt,
        string reason,
        Dictionary<int, ProcessDescriptor> descriptorsByPid,
        CancellationToken cancellationToken)
    {
        foreach (ActiveProcess process in application.Processes.Values)
        {
            string processReason = descriptorsByPid.TryGetValue(process.InstanceId.ProcessId, out ProcessDescriptor? descriptor)
                && descriptor.InstanceId != process.InstanceId
                    ? "PidReused"
                    : reason;
            await EndProcessAsync(
                connection,
                transaction,
                process.Id,
                observedAt,
                processReason,
                cancellationToken).ConfigureAwait(false);
        }

        await EndApplicationRowAsync(
            connection,
            transaction,
            application.Id,
            observedAt,
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> EndApplicationRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        DateTimeOffset observedAt,
        string reason,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE application_sessions
            SET ended_observed_utc = $observed, end_reason = $reason
            WHERE id = $id AND ended_observed_utc IS NULL;
            """,
            id,
            observedAt,
            reason,
            cancellationToken);

    private static Task<int> EndProcessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        DateTimeOffset observedAt,
        string reason,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE process_sessions
            SET ended_observed_utc = $observed, end_reason = $reason
            WHERE id = $id AND ended_observed_utc IS NULL;
            """,
            id,
            observedAt,
            reason,
            cancellationToken);

    private static Task<int> UpdateApplicationHeartbeatAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            "UPDATE application_sessions SET last_observed_utc = $observed WHERE id = $id;",
            id,
            observedAt,
            null,
            cancellationToken);

    private static Task<int> UpdateProcessHeartbeatAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            "UPDATE process_sessions SET last_observed_utc = $observed WHERE id = $id;",
            id,
            observedAt,
            null,
            cancellationToken);

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        long id,
        DateTimeOffset observedAt,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$observed", Timestamp(observedAt));
        if (reason is not null)
        {
            command.Parameters.AddWithValue("$reason", reason);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? EndReason(
        ProcessInstanceId instanceId,
        HashSet<int> observedPids,
        Dictionary<int, ProcessDescriptor> descriptorsByPid)
    {
        if (descriptorsByPid.TryGetValue(instanceId.ProcessId, out ProcessDescriptor? descriptor)
            && descriptor.InstanceId != instanceId)
        {
            return "PidReused";
        }

        return observedPids.Contains(instanceId.ProcessId) ? null : "NoLongerObserved";
    }

    private static bool IsIdentityAlive(
        ProcessInstanceId instanceId,
        HashSet<int> observedPids,
        Dictionary<int, ProcessDescriptor> descriptorsByPid) =>
        descriptorsByPid.TryGetValue(instanceId.ProcessId, out ProcessDescriptor? descriptor)
            ? descriptor.InstanceId == instanceId
            : observedPids.Contains(instanceId.ProcessId);

    private static Dictionary<string, ActiveApplication> Clone(
        IReadOnlyDictionary<string, ActiveApplication> source) => source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString(
        "yyyy-MM-dd HH:mm:ss.fffffff",
        CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal sealed record SessionReconciliation(
        Dictionary<string, ActiveApplication> Active,
        IReadOnlyDictionary<string, long> SessionIds);

    internal sealed class ActiveApplication(
        long id,
        string logicalId,
        DateTimeOffset lastObservedUtc,
        DateTimeOffset lastPersistedUtc,
        Dictionary<ProcessInstanceId, ActiveProcess> processes)
    {
        public long Id { get; } = id;
        public string LogicalId { get; } = logicalId;
        public DateTimeOffset LastObservedUtc { get; set; } = lastObservedUtc;
        public DateTimeOffset LastPersistedUtc { get; set; } = lastPersistedUtc;
        public Dictionary<ProcessInstanceId, ActiveProcess> Processes { get; } = processes;

        public ActiveApplication Clone() => new(
            Id,
            LogicalId,
            LastObservedUtc,
            LastPersistedUtc,
            Processes.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()));
    }

    internal sealed class ActiveProcess(
        long id,
        ProcessInstanceId instanceId,
        DateTimeOffset lastObservedUtc,
        DateTimeOffset lastPersistedUtc,
        string processName,
        string? executablePath,
        string? publisher)
    {
        public long Id { get; } = id;
        public ProcessInstanceId InstanceId { get; } = instanceId;
        public DateTimeOffset LastObservedUtc { get; set; } = lastObservedUtc;
        public DateTimeOffset LastPersistedUtc { get; set; } = lastPersistedUtc;
        public string ProcessName { get; } = processName;
        public string? ExecutablePath { get; set; } = executablePath;
        public string? Publisher { get; set; } = publisher;

        public ActiveProcess Clone() => new(
            Id,
            InstanceId,
            LastObservedUtc,
            LastPersistedUtc,
            ProcessName,
            ExecutablePath,
            Publisher);
    }
}
