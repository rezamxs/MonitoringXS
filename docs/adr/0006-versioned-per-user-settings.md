# ADR 0006: Versioned per-user application settings

## Status

Accepted; implementation and runtime accessibility validation complete.

## Context

Sampling cadence, history retention, and theme must persist for one Windows
user without introducing Registry state or competing preference stores.
Settings failure must remain outside the live metrics, history, and privileged
Broker paths.

## Decision

Use one typed version-1 JSON document at
`%LOCALAPPDATA%\MonitoringXS\settings.json`, owned by
`MonitoringXS.Storage`. The Core model allowlists 1/2/5-second sampling,
6/24/72/168-hour retention, and System/Light/Dark theme. Unknown fields are
ignored for version 1; newer versions are preserved and rejected. Invalid or
corrupt documents are quarantined and safe defaults are returned.

Serialize writes through one async gate to a temporary file, flush it, then
atomically replace the prior document. Settings contain no secrets, SIDs,
paths, or Broker security details.

Cadence changes wake the existing single-execution refresh loop. Retention
changes update an in-memory policy consumed by future bounded SQLite
maintenance; they do not block the UI, clear history synchronously, or require
a schema migration. Theme changes use WinUI's existing requested-theme
mechanism. Broker status reuses the existing SCM/protocol probe and exposes
only safe text; Settings performs no service mutation or elevation.

## Consequences

The prior standalone appearance preference is replaced by the single settings
document. A first run after upgrade uses System theme until the user saves a
setting; the old `appearance.txt` is not read or modified. Clear History is
deferred because the current history abstraction has no safe metric-only clear
operation. Runtime Broker installation remains an explicit operator action
through `scripts/privileged-broker/Manage-PrivilegedBroker.ps1`.
