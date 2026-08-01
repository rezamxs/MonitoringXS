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
- [x] Calculate physical-disk rates from monotonic elapsed time, retain sub-minimum-interval bytes, and keep incomplete session totals marked as lower bounds.
- [x] Preserve the original user initiator across split completions for one IRP, prevent PID 4 split-init overwrite, and expose events outside the current application set as unattributed.
- [x] Add collector diagnostics and deterministic warming-up, zero, partial, access-denied, cancellation, and PID-reuse coverage.
- [x] Complete a one-time elevated runtime smoke with real `Available` physical-disk rates, logical-workload attribution, visible lower-bound semantics, and verified ETW-session release after normal shutdown.
- [x] Reproduce and fix the advanced-detail WinUI layout-cycle fail-fast; retain keyboard/accessibility behavior and cover the equivalent scenario with a real UI Automation stress harness.
- [ ] Exercise an actual OS PID-reuse occurrence during an elevated runtime capture; the UTC-domain rejection path is deterministic-test-covered, but no PID reuse occurred in the recorded manual smoke.

## Milestone 3 - Network and GPU

### Milestone 3A - Network

Status: complete and accepted; see the final elevated validation in `VALIDATION.md`.

- [x] Add real per-process TCP/UDP ETW byte attribution with UTC PID-reuse protection.
- [x] Add monotonic rates, bounded queues, retained logical-application totals, endpoint counts, detailed diagnostics, and honest availability/lower-bound states.
- [x] Aggregate network values into existing logical applications without mixing them with Process I/O or physical disk.
- [x] Add focused Beginner/Advanced UI fields and deterministic tests.
- [x] Repeat the controlled elevated browser runtime after the retained-total regression fix. The accepted run verified real TCP/UDP attribution, retained helper totals, new-delta-only accumulation, lifetime reset, diagnostics, responsiveness, exit code 0, and shared-session cleanup.

### Milestone 3B - GPU

Status: approved for checkpoint within the tested PDH/WDDM scope; broader compatibility validation remains open.

- [x] Select a Windows performance-counter provider after official-source review and a real native benchmark.
- [x] Add GPU Engine and GPU Process Memory contracts, a persistent PDH source, PID plus UTC start-time validation, multi-adapter aggregation, diagnostics, deterministic tests, and UI fields.
- [x] Validate real VLC engine and memory values on the development machine with two reported adapters.
- [x] Bound native PDH buffers and instance counts, isolate utilization from independent memory availability, and make query disposal/concurrent capture deterministic.
- [x] Keep malformed, duplicate, stale, and reused-PID counter instances explicit instead of converting them to zero; preserve lower-bound semantics for partial memory sums.
- [x] Run the focused WinUI runtime pass with real VLC and a controlled Edge WebGL process tree; Edge remained honestly unavailable when its driver path exposed no GPU counter instances.
- [ ] Validate unsupported WDDM/driver, remote, virtual, and additional hardware scenarios.
- [ ] Exercise an actual operating-system PID-reuse occurrence during GPU capture; deterministic exact-identity rejection is covered by tests.

## Milestone 4 - Product UI

Status: in progress.

- [x] Add stable installed/portable application sorting for name, CPU, memory, Process I/O, Physical disk, Network, and process count.
- [x] Preserve selection, keyboard focus, and logical application tabs while live metrics reorder cards.
- [x] Integrate a restrained WinUI title bar while retaining Windows caption buttons, drag, and double-click behavior.
- [x] Refine application-card identity, primary metric, supporting metric, and unavailable-state hierarchy.
- [x] Add runtime System, Light, and Dark appearance modes with a persisted lightweight preference.
- [x] Repair the one-minute CPU chart so ordered timestamps, duplicate samples, unavailable gaps, invalid numbers, and real zero values render honestly.
- [x] Complete runtime accessibility stabilization for the Settings page (predefined cadence/retention, immediate theme, typed persistence, and read-only Broker health).
- [ ] Complete Dashboard and Portable Apps pages, remaining final UI pages, and final chart work.
- [ ] Complete dark-theme, High Contrast, 150-200% scaling, Windows 11 Snap Layout, and broader screen-reader validation.

## Milestone 5 - History

Status: SQLite backend and History page complete; stopped-tab presentation remains open.

- [x] Version-2 SQLite schema/migrations, WAL fallback, parameterized SQL, UTC timestamps, bounded async batches, crash-safe transactions, recovery, and diagnostics.
- [x] 24-hour retention, one-hour raw window, five-minute downsampling, bounded database-size pruning, metric queries, availability/completeness persistence, and PID-lifetime separation.
- [x] Deterministic storage and coordinator-isolation tests; runtime restart persistence measured in `VALIDATION.md`.
- [x] Cancellable History page with logical-application selector, 15-minute/1-hour/6-hour/24-hour ranges, bounded charts, local timestamps, and honest unavailable/partial gaps.
- [x] Repair History projection, scale domains, single-point rendering, duplicate/PID/gap handling, stale-result suppression, and extrema-preserving decimation.
- [ ] Stopped application tabs.

## Milestone 6 - Application actions

Status: selected-process action implementation and manual Release runtime validation complete.

- [x] Typed PID/start-time/executable identity and critical/protected/self/Broker fail-closed safety.
- [x] Confirmed End Task with bounded exit verification and honest Access Denied.
- [x] Strongly confirmed bounded End Process Tree with leaf-to-root ordering and partial results.
- [x] Verified Open File Location and safe Copy Process Details without command lines or secrets.
- [x] Existing details surface integration with process selection, keyboard commands, accessible feedback, and stale-selection suppression.
- [x] Complete focused Release mouse/keyboard/runtime validation on disposable helpers.
- [ ] Graceful close, restart, launch, official uninstall, and stopped-application actions.

## Milestone 7 - Optimization and release

- [x] Add the production-oriented x64 MSI with self-contained payloads, native
  Broker service lifecycle, major-upgrade/repair/uninstall behavior, and
  installer validation automation.
- [ ] Profiling, final security/accessibility review, benchmarks, release
  signing, release docs, and open-source readiness.

## First vertical slice exit criteria

Real process discovery, infrastructure filtering, multi-process grouping, portable separation, real CPU/memory, logical tab opening, real one-minute chart, Beginner/Advanced disclosure, successful Release build, and passing focused tests.

Implementation status: stable. Runtime/visual/keyboard/Automation smoke testing populated real application cards, kept section labels out of the tab order, opened a logical application tab by keyboard, exposed live metrics and classification evidence, stayed responsive, and met the steady CPU/working-set targets on the recorded validation machine. Final restore/build/test/launch results are recorded in `VALIDATION.md`.
## Phase 3B checkpoint

Phase 3B adds the optional privileged ETW broker for Network and Physical disk metrics. The app remains unelevated; broker installation is the only UAC boundary. Protocol authorization, bounds, reconnect/restart handling, honest unavailable/partial states, collector isolation, and PID reuse are covered by deterministic tests. LocalService reached `TraceEventSession.EnableKernelProvider` but returned Win32 5; the hardened LocalSystem service completed protocol v1, ETW startup, nonzero attribution, restart recovery, and cleanup.
