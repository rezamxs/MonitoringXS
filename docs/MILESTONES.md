# Milestones

## Milestone 0 - Repository foundation

Status: complete and validated; see `VALIDATION.md`.

- [x] Inspect and select available skills.
- [x] Define repository rules and core documentation.
- [x] Scaffold the modular solution.
- [x] Configure dependency injection, logging, tests, design tokens, VS Code, and Git hygiene files.
- [x] Install and validate the .NET 10 SDK.
- [x] Restore and build the complete solution, including WinUI XAML compilation.
- [x] Run every automated test and launch the x64 WinUI application.

## Milestone 1 - Application discovery

Status: complete and validated; see `VALIDATION.md` for the successful smoke run, 44 passing tests, and the final environment-specific NuGet warning/launch-attempt notes.

- [x] Enumerate processes with PID/start-time instance identity and cached executable metadata.
- [x] Filter known infrastructure and service-session processes.
- [x] Keep known user-facing Microsoft applications visible.
- [x] Implement initial logical grouping, game/launcher separation, VS Code ancestry rules, and portable path heuristics.
- [x] Add focused attribution tests.
- [x] Add bounded installed Win32 and MSIX application catalogs.
- [x] Map process package family/full-name identity and AppUserModelID to package applications.
- [x] Add bounded executable metadata, embedded-signature, and icon extraction caches.
- [x] Persist bounded user attribution overrides with validated, atomic JSON updates.
- [x] Replace path-only installed/portable decisions with catalog-backed evidence and report confidence plus human-readable reasons.
- [x] Add automated coverage for catalogs, package mapping, caches, overrides, classification, PID reuse identity, and false-positive boundaries.

## Milestone 2 - Core metrics

Status: in progress.

- [x] Real normalized CPU deltas and working-set memory.
- [x] Real process-wide I/O counters, read/write rates, cumulative bytes, and operation counts.
- [x] Logical-application aggregation with explicit partial/lower-bound semantics.
- [x] One-second live loop and bounded one-minute CPU history.
- [x] Add bounded physical-disk ETW attribution with read/write rates, operation counts, session totals, and explicit separation from Process I/O.
- [x] Normalize ETW and process-start timestamps to UTC before PID-reuse checks; discard ambiguous batches after ETW loss.
- [x] Add collector diagnostics and deterministic warming-up, zero, partial, access-denied, cancellation, and PID-reuse coverage.
- [x] Complete a one-time elevated runtime smoke with real `Available` physical-disk rates, logical-workload attribution, visible lower-bound semantics, and verified ETW-session release after normal shutdown.
- [ ] Exercise an actual OS PID-reuse occurrence during an elevated runtime capture; the UTC-domain rejection path is deterministic-test-covered, but no PID reuse occurred in the recorded manual smoke.

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

Implementation status: stable. Runtime/visual/keyboard/Automation smoke testing populated real application cards, kept section labels out of the tab order, opened a logical application tab by keyboard, exposed live metrics and classification evidence, stayed responsive, and met the steady CPU/working-set targets on the recorded validation machine. Final restore/build/test/launch results are recorded in `VALIDATION.md`.
