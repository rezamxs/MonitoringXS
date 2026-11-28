# Changelog

All notable changes follow Keep a Changelog principles. The project uses semantic versioning once public releases begin.

## [Unreleased]

## [1.0.1] - 2026-11-28

### Fixed

- Physical Disk and Network were unavailable on every install because the unsigned Privileged Metrics Service rejected the unsigned app (shown as a protocol mismatch). The app now explains this on first launch with a consent dialog; **Allow** asks for administrator approval (UAC) and records an admin-only opt-in, and **Not now** keeps everything else working. Settings → Privileged metrics shows the state with Allow/Revoke. A signed service still requires the same publisher.
- Numbers no longer pick up Persian decimal and digit formatting in places that expect invariant values.
- Overview: missing icons, layout glitches, and duplicate application rows.
- History: chart axis labels and scaling.
- Settings: inconsistent gutter and spacing.

### Changed

- Lower overhead: sampling is throttled while the window is minimized, only the visible page updates, the default sampling interval is 2 s, the app uses non-concurrent GC, and the service is smaller. On a typical desktop this measured about 1.2% CPU and 96 MB RAM, down from 3–4% CPU and about 250 MB.
- The MSI shrank from 111 MB to 84 MB.
- About and Diagnostics now show the release version (1.0.1) without a source-revision suffix.

## [1.0.0] - 2026-11-27

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
