# ADR 0005: SQLite metric history backend

Status: accepted for backend checkpoint; History UI remains deferred.

## Decision

Store local application metric snapshots in `%LOCALAPPDATA%\MonitoringXS\history.db`
through `SqliteMetricHistoryStore`. Core exposes `IMetricHistoryStore`; Storage
owns SQLite. The coordinator makes a best-effort bounded enqueue after a live
capture. Storage failure, queue overflow, cancellation, or disposal never
interrupts CPU, memory, Process I/O, Physical Disk, Network, or GPU collection.

Schema version 2 uses a migration table and `PRAGMA user_version`. Applications
are keyed by stable logical application ID. Samples additionally store a
SHA-256 process-lifetime key derived from sorted PID plus UTC `StartTimeUtc`
values. A relaunch therefore remains queryable under the same logical
application without merging unrelated PID lifetimes.

Raw rows are retained for one hour. Maintenance groups older raw rows into
five-minute buckets until the default 24-hour retention cutoff. Bucket values
for rates and gauges are arithmetic means; availability is the worst state in
the bucket; the earliest source timestamp represents the bucket; missing values
remain SQL `NULL`. Cumulative totals are not averaged into rate history. A 64 MiB database-size policy prunes oldest
downsampled rows first, then raw rows, with a diagnostic.

Writes use parameterized SQL, WAL when supported, `synchronous=NORMAL`, a
five-second busy timeout, bounded queue capacity 256, and transactions of at
most 32 snapshots. Queries are parameterized and ordered by UTC timestamp.
Corrupt databases are quarantined as `.corrupt-*` and recreated. Locked,
read-only, disk-full, newer-schema, and other SQLite failures are surfaced in
diagnostics; no zero is fabricated.

## Privacy and security

Only application metadata, lifetime keys, numeric metric values, UTC timestamps,
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
