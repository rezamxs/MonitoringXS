# Monitoring XS repository rules

## Product contract

- Monitoring XS is a native Windows 10 (1809+) and Windows 11 application-centric resource monitor.
- Show logical user-facing applications, not raw Windows infrastructure processes.
- Never fabricate production metrics. Represent unavailable data explicitly.
- Keep services excluded from application totals unless the user explicitly enables the advanced option.
- Keep portable and unregistered applications separate from installed applications.

## Architecture

- `MonitoringXS.Core` owns immutable models and interfaces and has no UI, SQLite, or WinUI dependency.
- `MonitoringXS.Application` owns orchestration and use cases; it must not call Win32 directly.
- `MonitoringXS.Platform.Windows` isolates Windows discovery, metadata, safety checks, and P/Invoke.
- `MonitoringXS.Collectors` owns real metric sampling and aggregation and does not reference ViewModels.
- `MonitoringXS.Storage` owns persistence, migrations, retention, and downsampling.
- `MonitoringXS.DesignSystem` owns Precision Glass tokens and reusable visual resources.
- `MonitoringXS.App` owns WinUI views, ViewModels, navigation, tabs, and dependency composition.
- `MonitoringXS.ElevatedHelper` must remain an on-demand, single-operation process with validated input.
- Dependencies flow inward. Core never references an outer project.

## Coding conventions

- Follow `docs/ENGINEERING_STYLE.md` for naming, file organization, expected failures, logging, and comments.
- Use C# with nullable reference types, implicit usings, deterministic builds, and warnings as errors in CI.
- Prefer immutable records and explicit result types for expected failures.
- Use asynchronous APIs and cancellation tokens for I/O and long-running work.
- Do not use exceptions for expected process-exit, access-denied, or unavailable-counter paths.
- Never block the UI thread with discovery, metadata, storage, or metric collection.
- Bound every live queue, cache, and history buffer; document eviction behavior.
- Cache icons, executable metadata, signatures, package identity, and classifications.
- Treat command lines, paths, network endpoints, and diagnostic exports as sensitive.
- Do not execute strings through a shell when a direct Windows API exists.

## UI and accessibility

- Use the Precision Glass design tokens; do not hardcode ad-hoc colors in views.
- Use native WinUI controls and semantics before custom keyboard/focus behavior.
- Give every interactive control an accessible name and a visible keyboard focus state.
- Do not rely on color alone. Support high contrast, system theme, and reduced motion.
- Keep motion purposeful, generally 120-200 ms, and limited to opacity/transform where possible.
- Avoid continuous decorative animation, large animated blur, and full-tree redraws.

## Performance and safety targets

- Target idle CPU below 1% and working set below approximately 200 MB on a typical modern system.
- Default sampling is approximately one second; metadata is not reloaded on every sample.
- Never terminate critical/protected Windows processes.
- Force-stop requires an explicit warning and confident attribution.
- Do not require permanent elevation, a service, or a kernel driver.
- Sanitize logs and never log secrets or complete sensitive command lines.

## Validation

Run from the repository root:

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

When WinUI packaging is available, also validate the x64 packaged profile. Record commands and actual results; never claim unexecuted validation.

## Repository hygiene

- Preserve user work and keep changes scoped to the active milestone.
- Do not commit generated `bin/`, `obj/`, packages, local databases, logs, or diagnostic exports.
- Update architecture, metric semantics, security, troubleshooting, and milestone documentation when behavior changes.
- Stable dependencies only. Document any unavoidable exception before adding it.
