# Development setup

1. Install Visual Studio 2026 with **WinUI application development**, .NET 10 SDK, and a current Windows SDK.
2. Enable Windows Developer Mode for local deployment if prompted.
3. Clone the repository to a normal user-writable path.
4. Run `dotnet restore MonitoringXS.sln`.
5. Run the Release build and tests documented in `AGENTS.md`.

The app targets x64 first. Use direct Visual Studio launch for WinUI debugging. Core and classification tests do not require elevation. Per-process access failures are expected and must not make tests depend on the developer's current process list.

Package versions are centrally managed in `Directory.Packages.props`. Update only to stable versions and record compatibility decisions. Never add local NuGet credentials to the repository.
