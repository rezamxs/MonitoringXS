# Monitoring XS

Monitoring XS is a Windows desktop app that groups related processes into logical applications and shows their combined resource usage. Instead of scanning dozens of Chrome or Visual Studio Code processes individually, you see one entry per application with its total CPU, memory, disk, network, and GPU usage.

The project is under active development. Core monitoring, installer packaging, and most UI features work; some validation and polish remain open.

## Features

Working today:

- Application discovery for Win32 and MSIX apps
- Process grouping by logical application
- Separate tracking for portable and unregistered apps
- CPU, memory, and process I/O metrics
- Physical disk and network monitoring via ETW (requires Privileged Broker)
- GPU engine and process-attributed GPU memory via performance counters
- Beginner and advanced view modes
- Keyboard navigation
- SQLite metric history with 24-hour retention
- History page with gap-aware charts
- Process actions: End Task, End Process Tree, Open File Location, Copy Process Details
- English and Persian localization
- MSI installer with install, repair, upgrade, and uninstall ([details](docs/INSTALLER.md))

Not yet complete:

- Broader GPU driver/hardware validation
- Stopped-application presentation and actions
- Remaining UI pages and high-DPI / High Contrast validation

## How it works

Windows creates multiple processes per application (renderer, GPU, utility, etc.). Monitoring XS classifies each process using executable metadata, package identity, and parent relationships, then aggregates metrics at the application level. Metrics that cannot be collected are shown as unavailable rather than fabricated.

Physical disk and network monitoring use a restricted LocalSystem service (`PrivilegedEtwBroker`) because kernel ETW requires elevated access. The main app runs unelevated. If the broker is not installed or stops, those metrics show as unavailable while CPU, memory, process I/O, and GPU continue working. See [security documentation](docs/SECURITY.md) and [ADR 0004](docs/adr/0004-privileged-etw-broker-service.md) for details.

## Requirements

- Windows 10 build 17763+ or Windows 11
- x64 (ARM64 is an architectural target, not yet tested)
- .NET 10 SDK
- Visual Studio 2026 with **WinUI application development** workload and current Windows SDK
- Developer Mode enabled for local debug deployment

## Build and run

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

Run from Visual Studio (x64 target) or:

```powershell
dotnet run --project src/MonitoringXS.App/MonitoringXS.App.csproj -c Debug -p:Platform=x64
```

To install the Privileged Broker for disk and network metrics, run from an elevated PowerShell:

```powershell
.\scripts\privileged-broker\Manage-PrivilegedBroker.ps1 -Mode Install
```

See [development setup](docs/DEVELOPMENT.md) and [installer documentation](docs/INSTALLER.md) for additional options.

## Project structure

- `MonitoringXS.Core` — immutable models and interfaces
- `MonitoringXS.Application` — orchestration and use cases
- `MonitoringXS.Platform.Windows` — Windows discovery, metadata, P/Invoke
- `MonitoringXS.Collectors` — metric sampling and aggregation
- `MonitoringXS.Storage` — SQLite persistence and retention
- `MonitoringXS.DesignSystem` — Precision Glass visual tokens
- `MonitoringXS.App` — WinUI views, ViewModels, navigation
- `MonitoringXS.ElevatedHelper` — on-demand privileged operations
- `MonitoringXS.PrivilegedBroker` — optional ETW broker service

Full architecture details are in [ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Current status

The repository builds cleanly in Release configuration. Current validation results are documented in [docs/VALIDATION.md](docs/VALIDATION.md). ETW disk and network attribution, GPU counters, and SQLite history have been validated on the development machine. These results reflect one environment and do not constitute a release qualification.

Some monitoring data may be unavailable for protected or higher-integrity processes. The app reports this honestly instead of requesting permanent elevation.

## Documentation

- [Product specification](docs/PRODUCT_SPEC.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Metric semantics](docs/METRICS.md)
- [Application attribution](docs/APPLICATION_ATTRIBUTION.md)
- [Security](docs/SECURITY.md) and [privacy](docs/PRIVACY.md)
- [Performance](docs/PERFORMANCE.md)
- [Design system](docs/DESIGN_SYSTEM.md)
- [Milestones](docs/MILESTONES.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Engineering style](docs/ENGINEERING_STYLE.md)

## Contributing

Issues and focused pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md) before submitting.

Created and maintained by [rezamxs](https://github.com/rezamxs). Licensed under the [MIT License](LICENSE).