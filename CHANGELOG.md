# Changelog

All notable changes follow Keep a Changelog principles. The project uses semantic versioning once public releases begin.

## [Unreleased]

### Added

- Logical-application monitoring for CPU, memory, process I/O, physical disk, network, and GPU.
- Single capture runtime and snapshot hub; UI consumes published snapshots rather than owning collectors.
- SQLite History v3 with 24-hour retention, application/process sessions, and PID + start-time identity.
- History page with range selection, gap-aware charts, hover tooltips, and honest empty/unavailable states.
- Process Intelligence (details, search, publisher, file version, architecture, parent, threads/handles) and PID + start-time process actions.
- Diagnostics center, metric availability metadata, and English/Persian localization.
- Privileged ETW broker, x64 MSI installer packaging, and public issue-reporting templates.
- Repository foundation, architecture, product, security, privacy, metric, performance, and design documentation.

### Changed

- Public README and contributor docs now describe the integrated product line instead of the older `main` snapshot.

### Removed

- Agent-only local tooling (`.agents/`, `AGENTS.md`, `skills-lock.json`) from the integration tree. These were development aids, not product runtime.
- Phase E1 History UX: gap-aware charts, timestamp-based hover tooltips with availability/reason, honest empty/unavailable presentation, and range persistence on the History page view-model.
