# ADR 0005: SQLite metric history backend

Status: accepted for backend checkpoint; History UI remains deferred.

## Decision

Store local application metric snapshots in `%LOCALAPPDATA%\MonitoringXS\history.db`
through `SqliteMetricHistoryStore`. Core exposes `IMetricHistoryStore`; Storage
owns SQLite. The coordinator makes a best-effort bounded enqueue after a live
capture. Storage failure, queue overflow, cancellation, or disposal never
interrupts CPU, memory, Process I/O, Physical Disk, Network, or GPU collection.

Schema version 3 uses a migration table and `PRAGMA user_version`. Applications
are keyed by stable logical application ID. Each observed run has one application
session; each OS lifetime has one process session keyed uniquely by PID plus UTC
`StartTimeUtc`. Executable path remains optional metadata. Samples retain logical
application ID and optionally reference an application session. Transactional v2
migration preserves every old sample, renames its SHA-256 hash to
`legacy_continuity_key`, leaves `application_session_id` null, and creates no fake sessions.

Raw rows are retained for one hour. Maintenance groups older raw rows into
five-minute buckets until the default 24-hour retention cutoff. Bucket values
for rates and gauges are arithmetic means; availability is the worst state in
the bucket; the earliest source timestamp represents the bucket; missing values
remain SQL `NULL`. Cumulative totals are not averaged into rate history. A 64 MiB database-size policy prunes oldest
downsampled rows first, then raw rows, with a diagnostic.

Writes use parameterized SQL, WAL when supported, `synchronous=NORMAL`, a
five-second busy timeout, bounded capture queue capacity 256, and one transaction
per accepted snapshot. Session heartbeats are coalesced to 30 seconds. Queries are parameterized and ordered by UTC timestamp.
Corrupt databases are quarantined as `.corrupt-*` and recreated. Locked,
read-only, disk-full, newer-schema, and other SQLite failures are surfaced in
diagnostics; no zero is fabricated.

## Privacy and security

Only application metadata, session timestamps/reasons, PID/start identity,
process name, optional executable path/publisher, numeric metric values, UTC timestamps,
availability, and diagnostic detail capped at 512 characters are persisted. Packet payloads,
URLs, hosts, IPs, ports, command lines, secrets, and raw ETW events are not
stored. The database is local and follows the user's local application-data
access controls.

## Alternatives

- JSON was rejected for concurrent range queries, transactions, WAL, and
  bounded retention maintenance.
- A server database was rejected because history is local, offline, and
  privacy-sensitive.
- A UI-owned history cache was rejected because it cannot survive restart and
  would couple persistence to presentation.

## Dependency note

`Microsoft.Data.Sqlite` 10.0.10 is centrally pinned. Its compatible
`SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 security update is also centrally pinned,
so NuGet does not resolve the deprecated vulnerable 2.1.11 native library.
