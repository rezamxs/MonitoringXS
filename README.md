# Monitoring XS

Monitoring XS is a Windows desktop app I am building to make resource monitoring easier to understand.

Windows Task Manager shows many separate processes, even when several of them belong to the same application. This project tries to group related processes and show the combined CPU, memory and disk usage of the application.

The project is still under development. Some parts work, but several planned features are not finished yet.

## Author and direction

Monitoring XS is created and maintained by [rezaalizadeh](https://github.com/rezamxs), using the public alias `rezamxs`.

The project direction is simple: build a clean, accurate, low-overhead Windows application that helps people monitor logical applications instead of confusing raw process lists.

Copyright (c) 2026 `rezam_xs`. The source is available under the MIT License.

## Why I started this project

I wanted a simpler way to see which applications are using my computer resources.

Programs such as Chrome, Edge and Visual Studio Code can create many processes. Checking each process separately makes it difficult to understand the total usage of the program.

Monitoring XS tries to group these related processes and show them as one application.

## Current status

Currently working:

- running application discovery
- Win32 and MSIX application detection
- grouping related processes
- separating portable and unregistered applications
- CPU usage
- memory usage
- Process I/O
- physical disk monitoring through ETW
- network monitoring through ETW
- application tabs
- beginner and advanced views
- keyboard navigation

Not finished yet:

- GPU and VRAM monitoring
- 24-hour history
- application close, restart and force-stop actions
- uninstall support
- installer and release package
- final UI pages and testing

Physical disk and network monitoring may need Administrator access on some Windows systems. Without that permission, the application should show Access denied instead of displaying a fake value.

## Validation

The current repository has:

- a successful Release build
- 150 passing tests
- no failed or skipped tests
- validated ETW physical-disk monitoring on the development machine
- validated ETW network attribution with controlled Chrome traffic on the development machine

These results only describe the current development environment and do not mean the application is ready for a public release.

## Requirements

- Windows 10 build 17763 or later; Windows 11 recommended for development.
- x64 machine (ARM64 compatibility is an architectural target).
- .NET 10 SDK.
- Visual Studio 2026 with the **WinUI application development** workload and a current Windows SDK.
- Developer Mode for local unpackaged/debug deployment where required.

## Build and test

```powershell
dotnet restore MonitoringXS.sln
dotnet build MonitoringXS.sln -c Release --no-restore
dotnet test MonitoringXS.sln -c Release --no-build
```

Run the app from Visual Studio using the x64 target, or:

```powershell
dotnet run --project src/MonitoringXS.App/MonitoringXS.App.csproj -c Debug -p:Platform=x64
```

Packaging profiles will be added before the release milestone. Some monitoring data can be unavailable for protected or higher-integrity processes. The app should show that limitation instead of requesting permanent elevation.

## Architecture and documentation

- [Product specification](docs/PRODUCT_SPEC.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Engineering style](docs/ENGINEERING_STYLE.md)
- [Logical applications and honest metrics decision](docs/adr/0001-logical-applications-and-honest-metrics.md)
- [Shared kernel metrics session decision](docs/adr/0002-shared-kernel-metrics-session.md)
- [Application attribution](docs/APPLICATION_ATTRIBUTION.md)
- [Metric semantics](docs/METRICS.md)
- [Precision Glass design system](docs/DESIGN_SYSTEM.md)
- [Security](docs/SECURITY.md) and [privacy](docs/PRIVACY.md)
- [Performance](docs/PERFORMANCE.md)
- [Milestones](docs/MILESTONES.md)
- [Development setup](docs/DEVELOPMENT.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Latest validation results](docs/VALIDATION.md)

## Unpackaged developer publish

After the first successful build, an unpackaged self-contained x64 developer output can be produced with:

```powershell
dotnet publish src/MonitoringXS.App/MonitoringXS.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true
```

MSIX packaging and signing are intentionally not claimed yet; they are Milestone 7 work and require a package manifest, identity, certificate/signing policy, and clean-machine installation validation.

## Contributing

Issues and focused pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and [SECURITY.md](SECURITY.md). Monitoring XS is licensed under the [MIT License](LICENSE).
