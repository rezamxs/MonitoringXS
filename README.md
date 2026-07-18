# Monitoring XS

Monitoring XS is an application-centric resource monitor for Windows 10 version 1809+ and Windows 11. It groups related processes into understandable logical applications, hides operating-system infrastructure, separates portable tools, and reports only real Windows metrics.

> Project status: early vertical slice at the Milestone 1/2 boundary. Process discovery, initial attribution, real CPU/working-set metrics, and real process-wide I/O accounting are implemented. Physical-disk attribution, network, GPU, SQLite history, and management actions remain planned and are never represented with fabricated values.

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

Packaging profiles will be added before the release milestone. Monitoring APIs may return limited data for protected or higher-integrity processes; the UI must show that limitation rather than request permanent elevation.

## Architecture and documentation

- [Product specification](docs/PRODUCT_SPEC.md)
- [Architecture](docs/ARCHITECTURE.md)
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
