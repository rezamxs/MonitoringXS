# Milestones

## Milestone 0 - Repository foundation

Status: implementation complete; validation is partial because the Windows App SDK runtime package cannot currently be downloaded reliably; see `VALIDATION.md`.

- [x] Inspect and select available skills.
- [x] Define repository rules and core documentation.
- [x] Scaffold the modular solution.
- [x] Configure dependency injection, logging, tests, design tokens, VS Code, and Git hygiene files.
- [x] Install and validate the .NET 10 SDK for non-UI projects.
- [x] Build every non-UI project and run every automated test.
- [ ] Restore/build the WinUI project, launch it, and complete visual/keyboard validation after NuGet can download the Windows App SDK runtime.

## Milestone 1 - Application discovery

Status: in progress.

- [x] Enumerate processes with PID/start-time instance identity and cached executable metadata.
- [x] Filter known infrastructure and service-session processes.
- [x] Keep known user-facing Microsoft applications visible.
- [x] Implement initial logical grouping, game/launcher separation, VS Code ancestry rules, and portable path heuristics.
- [x] Add focused attribution tests.
- [ ] Add installed Win32 and MSIX catalogs, package identity/AppUserModelID, icon/signature caches, and user overrides.
- [ ] Replace remaining path-only installed/portable decisions with catalog-backed evidence.

## Milestone 2 - Core metrics

Status: in progress.

- [x] Real normalized CPU deltas and working-set memory.
- [x] Real process-wide I/O counters, read/write rates, cumulative bytes, and operation counts.
- [x] Logical-application aggregation with explicit partial/lower-bound semantics.
- [x] One-second live loop and bounded one-minute CPU history.
- [ ] Add physical-disk-only attribution (the current process I/O counters cover all process I/O and are not labelled as disk).
- [ ] Add collector diagnostics and broader process-exit/access-denied coverage.

## Milestone 3 - Network and GPU

- Per-process ETW/network attribution, GPU engine counters, multiple-adapter handling, and honest availability states.

## Milestone 4 - Product UI

- Dashboard, Running Apps, Portable Apps, logical app tabs, modes, charts, themes, scaling, keyboard, and screen reader validation.

## Milestone 5 - History

- SQLite, WAL/batching, 24-hour retention/downsampling, history charts, stopped tabs, and restart reconnection.

## Milestone 6 - Application actions

- Graceful close, force stop, restart, launch, file location, official uninstall, helper isolation, and critical-process safety.

## Milestone 7 - Optimization and release

- Profiling, security/accessibility review, packaging, benchmarks, release docs, and open-source readiness.

## First vertical slice exit criteria

Real process discovery, infrastructure filtering, multi-process grouping, portable separation, real CPU/memory, logical tab opening, real one-minute chart, Beginner/Advanced disclosure, successful Release build, and passing focused tests.

Implementation status: discovery, initial rules, CPU/memory/process-I/O collection, aggregation, bounded one-minute history, cards, logical tabs, disclosure, and focused tests are present. All 20 automated tests pass and every non-UI project builds without warnings. The complete solution build, application launch, XAML validation, and UI review remain blocked by unreliable download of `Microsoft.WindowsAppSDK.Runtime` from NuGet.
