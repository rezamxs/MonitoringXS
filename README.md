# Monitoring XS

Monitoring XS is a native Windows desktop monitor organized around **logical applications**, not raw process lists. Related processes (for example multiple Chrome or Visual Studio Code helpers) are grouped so you see combined CPU, memory, disk, network, and GPU use for the application the user actually launched.

The project is under active development. Core monitoring, History, Process Intelligence, Diagnostics, installer packaging, and English/Persian UI are implemented. Remaining work includes placeholder pages, broader GPU hardware validation, and release signing.

## Features

Working in the current codebase:

- Win32 and MSIX application discovery with portable/unregistered apps kept separate
- Logical-application grouping with evidence, confidence, and Beginner/Advanced views
- CPU, working set, and process I/O from Windows process counters
- Physical disk and network attribution via kernel ETW (requires the Privileged Broker)
- GPU engine utilization and process-attributed dedicated/shared GPU memory via Windows performance counters
- Single capture runtime (`MonitoringRuntime` / snapshot hub): UI consumers do not start their own collectors
- SQLite History with 24-hour retention, application/process sessions, and PID + start-time identity
- History page: 5 minute–24 hour ranges, gap-aware charts, hover tooltips, loading/empty/error states
- Process Intelligence: details, search, publisher, file version, architecture, parent, threads, handles, copy details
- Process actions with PID + start-time revalidation: End Task, End Process Tree, Open File Location
- Diagnostics center over the existing snapshot/broker/history surfaces (no second monitoring loop)
- Metric availability is explicit: **unavailable is never shown as zero**
- English and Persian (RTL) localization
- MSI installer with install, repair, upgrade, and uninstall ([details](docs/INSTALLER.md))

Not finished:

- Dashboard and Portable Apps navigation items are visible placeholders
- Stopped-application presentation and additional actions (graceful close, restart, uninstall)
- Broader GPU driver/hardware, remote, and virtual-adapter validation
- High Contrast, 150–200% scaling, Snap Layout, and broader screen-reader validation
- Release signing and public release qualification

## How it works

Windows often creates several processes per application. Monitoring XS classifies each process using executable metadata, package identity, and parent relationships, then aggregates metrics at the application level.

Live capture runs through one runtime pipeline and publishes immutable snapshots. History is written asynchronously to a local SQLite database. The History UI queries `IMetricHistoryStore`; it does not open SQLite itself.

Physical disk and network monitoring use a restricted LocalSystem service (`PrivilegedEtwBroker`) because kernel ETW requires elevated access. The main app runs unelevated (`asInvoker`). If the broker is missing or stopped, those metrics show as unavailable while CPU, memory, process I/O, and GPU continue. See [security](docs/SECURITY.md) and [ADR 0004](docs/adr/0004-privileged-etw-broker-service.md).

GPU values come from Windows `GPU Engine` and `GPU Process Memory` counters on WDDM-capable drivers. Missing counter objects, protected processes, and software-rendered workloads stay unavailable or partial. That is not a claim of universal GPU coverage.

## Requirements

- Windows 10 build 17763+ or Windows 11
- x64 (ARM64 is an architectural target, not yet tested)
- .NET 10 SDK
- Visual Studio 2026 with the **WinUI application development** workload and a current Windows SDK
- Developer Mode enabled for local debug deployment

## Build and test

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

Run from Visual Studio (x64) or:

```powershell
dotnet run --project src/MonitoringXS.App/MonitoringXS.App.csproj -c Debug -p:Platform=x64
```

Current restore/build/test counts are recorded in [docs/VALIDATION.md](docs/VALIDATION.md) after each checkpoint. Do not treat an older numbered result in that log as the live suite size.

To install the Privileged Broker for disk and network metrics, run from elevated PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\privileged-broker\Manage-PrivilegedBroker.ps1" -Mode Install
```

Status (unelevated):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\privileged-broker\Manage-PrivilegedBroker.ps1" -Mode Status
```

See [development setup](docs/DEVELOPMENT.md) and [installer documentation](docs/INSTALLER.md).

## Project structure

- `MonitoringXS.Core` — immutable models and interfaces
- `MonitoringXS.Application` — runtime, snapshot hub, and use cases
- `MonitoringXS.Platform.Windows` — discovery, metadata, ETW/PDH, process actions
- `MonitoringXS.Collectors` — metric sampling and aggregation
- `MonitoringXS.Storage` — SQLite history and JSON settings
- `MonitoringXS.DesignSystem` — Precision Glass visual tokens
- `MonitoringXS.App` — WinUI views and view models
- `MonitoringXS.ElevatedHelper` — on-demand privileged operations
- `MonitoringXS.PrivilegedBroker` — optional ETW broker service

Architecture details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Current status

The integration branch through Phase E1 (History UX and chart foundation) is the current product line. Public `main` may lag until that branch is reviewed and merged.

Some data is unavailable for protected or higher-integrity processes. The app reports that honestly instead of requesting permanent elevation.

## Documentation

- [Product specification](docs/PRODUCT_SPEC.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Metric semantics](docs/METRICS.md)
- [Application attribution](docs/APPLICATION_ATTRIBUTION.md)
- [Security](docs/SECURITY.md) and [privacy](docs/PRIVACY.md)
- [Performance](docs/PERFORMANCE.md)
- [Design system](docs/DESIGN_SYSTEM.md)
- [Milestones](docs/MILESTONES.md)
- [Validation log](docs/VALIDATION.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Engineering style](docs/ENGINEERING_STYLE.md)
- [Windows installer](docs/INSTALLER.md)

## Feedback and bug reports

- **Bugs** — [Bug Report](https://github.com/rezamxs/MonitoringXS/issues/new?template=bug_report.yml)
- **Feature ideas** — [Feature Request](https://github.com/rezamxs/MonitoringXS/issues/new?template=feature_request.yml)
- **Security vulnerabilities** — [SECURITY.md](SECURITY.md). Do not disclose security issues in public issues.

## Contributing

Issues and focused pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md) before submitting.

Created and maintained by [rezamxs](https://github.com/rezamxs). Licensed under the [MIT License](LICENSE).
