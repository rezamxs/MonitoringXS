# Monitoring XS Development Tooling

This document describes the development toolchain for Monitoring XS.
All tools listed here are **development-only** unless explicitly marked as runtime.

## Quick Start

```powershell
# Install/check adopted tools
.\scripts\tooling\Bootstrap-DevToolchain.ps1

# Include experimental tools
.\scripts\tooling\Bootstrap-DevToolchain.ps1 -IncludeExperimental

# Verify all tools
.\scripts\tooling\Test-DevToolchain.ps1

# Run static analysis
.\scripts\tooling\Invoke-StaticAnalysis.ps1

# Run tests with coverage
.\scripts\tooling\Invoke-Coverage.ps1
```

---

## Adopted Tools (Group A)

### ArchUnitNET — Architecture Tests

| Field | Value |
|-------|-------|
| Version | 0.13.3 |
| Install | NuGet package in `tests/MonitoringXS.ArchitectureTests/` |
| License | MIT |
| Scope | Development (test project only) |
| Runtime Impact | **ZERO** |

Enforces architecture boundaries defined in [`ARCHITECTURE.md`](ARCHITECTURE.md). Rules verify that Core, Application, Collectors, Storage, Platform.Windows, DesignSystem, and PrivilegedBroker maintain correct dependency directions.

```powershell
dotnet test tests/MonitoringXS.ArchitectureTests/ -c Release
```

### CodeQL — Security Analysis

| Field | Value |
|-------|-------|
| Version | GitHub-native (codeql-action v3) |
| Install | `.github/workflows/codeql.yml` |
| License | GitHub Terms of Service |
| Scope | CI only |
| Runtime Impact | **ZERO** |

Runs on push to main, PRs to main, and weekly schedule. Uses `security-extended` query suite for C#.

### PSScriptAnalyzer — PowerShell Analysis

| Field | Value |
|-------|-------|
| Version | Latest (PowerShell Gallery) |
| Install | `Install-Module PSScriptAnalyzer -Scope CurrentUser` |
| License | MIT |
| Config | `PSScriptAnalyzerSettings.psd1` |
| Scope | Development + CI |
| Runtime Impact | **ZERO** |

Analyzes `scripts/**/*.ps1`, installer scripts, broker management scripts, validation scripts. Style rules excluded to avoid churn.

```powershell
Invoke-ScriptAnalyzer -Path scripts/ -Settings PSScriptAnalyzerSettings.psd1 -Recurse
```

### Coverlet — Code Coverage

| Field | Value |
|-------|-------|
| Version | Built into .NET SDK (XPlat Code Coverage collector) |
| Install | No separate install needed |
| License | MIT |
| Scope | Development + CI |
| Runtime Impact | **ZERO** |

Generates cobertura XML format. Does not instrument Release product binaries.

```powershell
.\scripts\tooling\Invoke-Coverage.ps1
```

### PerfView — Performance Profiling

| Field | Value |
|-------|-------|
| Version | Latest stable |
| Install | Download from https://github.com/microsoft/perfview/releases |
| License | MIT |
| Scope | External development tool |
| Runtime Impact | **ZERO** |

Not added to any project. Used externally for:
- CPU sampling: `PerfView.exe collect -Providers=*MonitoringXS* -CircularMB=512`
- GC/Allocation: `PerfView.exe collect -GCOnly -CircularMB=256`
- Memory growth: `PerfView.exe memsnap MonitoringXS.App.exe`

### BenchmarkDotNet — Microbenchmarks

| Field | Value |
|-------|-------|
| Status | Existing custom benchmark harness in `benchmarks/` |
| Scope | Development |
| Runtime Impact | **ZERO** |

The existing benchmarks project uses a custom harness (not BenchmarkDotNet framework). It tests PhysicalDiskMetricCollector aggregation performance and ETW event source behavior. No changes made.

```powershell
dotnet run --project benchmarks/MonitoringXS.Benchmarks/ -c Release
```

### Gitleaks — Secret Scanning

| Field | Value |
|-------|-------|
| Version | Latest stable |
| Install | `winget install gitleaks` or download from GitHub releases |
| License | MIT |
| Config | `.gitleaks.toml` |
| Scope | Development + CI |
| Runtime Impact | **ZERO** |

Scans tracked repository content. Build directories excluded via allowlist. Never whitelist real secrets.

```powershell
# Local scan
gitleaks detect --source . --no-git -v --config .gitleaks.toml

# Git history scan
gitleaks detect -v --config .gitleaks.toml
```

### actionlint — GitHub Actions Linting

| Field | Value |
|-------|-------|
| Version | Latest stable |
| Install | `go install github.com/rhysd/actionlint/cmd/actionlint@latest` |
| License | MIT |
| Scope | Development |
| Runtime Impact | **ZERO** |

Validates `.github/workflows/*.yml` files.

```powershell
actionlint .github/workflows/*.yml
```

### lychee — Documentation Link Validation

| Field | Value |
|-------|-------|
| Version | Latest stable |
| Install | `cargo install lychee` or download from GitHub releases |
| License | MIT/Apache-2.0 |
| Config | `.lychee.toml` |
| Scope | Development |
| Runtime Impact | **ZERO** |

Validates links in README, docs/**/*.md, CONTRIBUTING. Localhost/example URLs explicitly excluded.

```powershell
lychee --config .lychee.toml README.md docs/**/*.md
```

### DB Browser for SQLite — Database Inspection

| Field | Value |
|-------|-------|
| Version | Latest stable |
| Install | https://sqlitebrowser.org/dl/ |
| License | GPL-3.0 |
| Scope | External development tool |
| Runtime Impact | **ZERO** |

Used to inspect copied `history.db` files offline.

**Safe inspection workflow:**
1. Stop Monitoring XS
2. Copy `%LOCALAPPDATA%\MonitoringXS\history.db` to a temporary location
3. Open the copy in DB Browser for SQLite
4. Never modify the live database while Monitoring XS is running

### WiX 7 — Installer

| Field | Value |
|-------|-------|
| Version | 7.0.0 |
| Install | NuGet packages in `Directory.Packages.props` |
| License | MS-RL |
| Scope | Build/installer |
| Runtime Impact | **ZERO** (build-time only) |

Existing WiX 7 configuration audited and preserved. No second installer framework introduced.

```powershell
.\scripts\installer\Build-Installer.ps1
.\scripts\installer\Invoke-InstallerLifecycleValidation.ps1
```

### ast-grep — Structural Static Analysis

| Field | Value |
|-------|-------|
| Version | Latest stable |
| Install | `npm install -g @ast-grep/cli` or `cargo install ast-grep` |
| License | Apache-2.0 |
| Config | `.ast-grep/rules.yml` |
| Scope | Development |
| Runtime Impact | **ZERO** |

Five targeted rules:
1. Fragile availability mapping using `.Contains()`
2. Broad `catch(Exception)` in collector paths
3. Forbidden shell invocation patterns
4. Hardcoded user-facing strings in ViewModels
5. Core layer referencing Platform.Windows

```powershell
ast-grep scan --config .ast-grep/rules.yml src/
```

---

## Experimental Tools (Group B)

These tools are **NOT** mandatory. They are evaluation candidates.

### Repomix — Context Packaging

| Field | Value |
|-------|-------|
| Install | `npm install -g repomix` |
| License | MIT |
| Config | `repomix.config.json` |
| Status | KEEP EXPERIMENTAL |

Generates focused context packs from repository sources. Excludes binaries, secrets, build artifacts.

### LikeC4 — Architecture Visualization

| Field | Value |
|-------|-------|
| Install | `npm install -g likec4` |
| License | MIT |
| Config | `.tooling/architecture.likec4` |
| Status | KEEP EXPERIMENTAL |

Small prototype model covering major components and key flows. Drawback: requires Node.js runtime.

### Lefthook — Git Hook Orchestration

| Field | Value |
|-------|-------|
| Install | `npm install -g lefthook` or `go install github.com/evilmartians/lefthook@latest` |
| License | MIT |
| Config | `lefthook.yml` |
| Status | KEEP EXPERIMENTAL |

The pre-commit hook is limited to Git's fast, cross-platform staged whitespace check. Optional analyzers remain explicit developer commands.

### Codebase-Memory MCP

| Field | Value |
|-------|-------|
| Status | MANUAL SETUP REQUIRED |
| Scope | Read-only repository access recommended |
| Security | Keep index/cache out of Git |

Evaluation candidate for repository context optimization. See `docs/tooling/CODE_CONTEXT_EVALUATION.md`.

### Serena MCP

| Field | Value |
|-------|-------|
| Status | MANUAL SETUP REQUIRED |
| Scope | C# symbol/navigation |
| Security | Repository-scoped, read-only preferred |

Evaluation candidate for semantic code retrieval. See `docs/tooling/CODE_CONTEXT_EVALUATION.md`.

### GitNexus

| Field | Value |
|-------|-------|
| Status | **BLOCKED — LICENSE REVIEW REQUIRED** |
| Action | Do not install until license/commercial compatibility verified |

### FlaUI — UI Automation

| Field | Value |
|-------|-------|
| Status | STUDY ONLY |
| Scope | Isolated test experiment (1-2 focused tests max) |

Not adopted wholesale. Existing UI automation system preserved.

### Microsoft SBOM Tool

| Field | Value |
|-------|-------|
| Install | `dotnet tool install --global Microsoft.Sbom.DotNetTool` |
| License | MIT |
| Status | RELEASE EXPERIMENT |

Generate SBOM from Release/published artifacts only. Not for inner-loop builds.

### LibreHardwareMonitor

| Field | Value |
|-------|-------|
| Status | RESEARCH ONLY |
| Action | No NuGet/runtime reference added |

Future sensor research candidate. Hardware sensors feature is intentionally out of scope.

---

## Repository Knowledge Layer

A machine-readable repository map exists at `.tooling/repository-map.yml`.
It provides:
- Architecture module listing with dependencies
- Critical entry points
- Security boundary documentation
- History storage details
- Localization resource paths
- Installer entry points
- Key validation scripts
- Important documentation paths

This reduces discovery cost without duplicating existing documentation.

---

## Generated Artifacts

All generated artifacts are ignored by Git:

| Artifact | Location | Ignored By |
|----------|----------|------------|
| Coverage output | `TestResults/Coverage/` | `.gitignore` (TestResults/) |
| Context packs | `context-packs/` | `.gitignore` (added) |
| Tooling indexes | `.tooling/indexes/` | `.gitignore` (added) |
| Profiler outputs | `*.etl`, `*.dmp` | `.gitignore` |
| SBOM artifacts | `.artifacts/sbom/` | `.gitignore` (.artifacts/) |

---

## Security Notes

All MCP and local tooling follows these principles:
- **Read-only** where possible
- **Repository-scoped** — no access to user home, unrelated drives
- **No arbitrary network** — tools do not exfiltrate repository contents
- **No arbitrary shell** — no unprompted command execution
- **Index/cache location** — kept in `.tooling/` or user-local, added to `.gitignore`
- **Secrets** — never exposed to AI tooling; `.env`, certificates, credentials excluded

---

## Uninstall / Update

| Tool | Uninstall | Update |
|------|-----------|--------|
| PSScriptAnalyzer | `Uninstall-Module PSScriptAnalyzer` | `Update-Module PSScriptAnalyzer` |
| Gitleaks | Remove binary / `winget uninstall gitleaks` | Re-download latest release |
| actionlint | Remove binary | Re-download latest release |
| lychee | `cargo uninstall lychee` | `cargo install lychee --force` |
| ast-grep | `npm uninstall -g @ast-grep/cli` | `npm update -g @ast-grep/cli` |
| Repomix | `npm uninstall -g repomix` | `npm update -g repomix` |
| Lefthook | `npm uninstall -g lefthook` | `npm update -g lefthook` |
| SBOM Tool | `dotnet tool uninstall -g Microsoft.Sbom.DotNetTool` | Re-install latest |
| ArchUnitNET | Remove NuGet package reference | Update version in csproj |
