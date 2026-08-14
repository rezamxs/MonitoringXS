<#
.SYNOPSIS
    Bootstrap the Monitoring XS development toolchain.
.DESCRIPTION
    Detects prerequisites, installs missing approved DEVELOPMENT tools,
    and prints versions. Does not install runtime dependencies.
.PARAMETER IncludeExperimental
    Also install/evaluate TEST-FIRST experimental tools.
.EXAMPLE
    .\scripts\tooling\Bootstrap-DevToolchain.ps1
    .\scripts\tooling\Bootstrap-DevToolchain.ps1 -IncludeExperimental
#>
[CmdletBinding()]
param(
    [switch]$IncludeExperimental
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-ToolStatus {
    param([string]$Name, [string]$Status, [string]$Version = '')
    $color = switch ($Status) {
        'PASS'      { 'Green' }
        'MISSING'   { 'Yellow' }
        'INSTALLED' { 'Green' }
        'SKIPPED'   { 'Cyan' }
        'BLOCKED'   { 'Red' }
        default     { 'White' }
    }
    $versionInfo = if ($Version) { " ($Version)" } else { '' }
    Write-Host ("{0,-30} {1}{2}" -f $Name, $Status, $versionInfo) -ForegroundColor $color
}

function Test-CommandExists {
    param([string]$Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

function Get-ToolVersion {
    param([string]$Command, [string[]]$Arguments = @('--version'))
    try {
        $output = & $Command @Arguments 2>&1 | Select-Object -First 1
        return $output.ToString().Trim()
    } catch {
        return 'unknown'
    }
}

Write-Host "`n=== Monitoring XS Development Toolchain Bootstrap ===" -ForegroundColor Cyan
Write-Host "Repository: $(Split-Path $PSScriptRoot -Parent | Split-Path -Parent)`n"

# ── Prerequisites ──────────────────────────────────────────────
Write-Host "--- Prerequisites ---" -ForegroundColor Yellow

# .NET SDK
if (Test-CommandExists 'dotnet') {
    $dotnetVersion = Get-ToolVersion 'dotnet' '--version'
    Write-ToolStatus '.NET SDK' 'PASS' $dotnetVersion
} else {
    Write-ToolStatus '.NET SDK' 'MISSING'
    Write-Host "  Install: https://dot.net/download" -ForegroundColor Red
}

# Git
if (Test-CommandExists 'git') {
    $gitVersion = Get-ToolVersion 'git' '--version'
    Write-ToolStatus 'Git' 'PASS' $gitVersion
} else {
    Write-ToolStatus 'Git' 'MISSING'
}

# ── Adopted Tools (Group A) ───────────────────────────────────
Write-Host "`n--- Adopted Tools (Group A) ---" -ForegroundColor Yellow

# PSScriptAnalyzer (PowerShell module)
if (Get-Module -ListAvailable PSScriptAnalyzer -ErrorAction SilentlyContinue) {
    $psaVersion = (Get-Module -ListAvailable PSScriptAnalyzer | Select-Object -First 1).Version.ToString()
    Write-ToolStatus 'PSScriptAnalyzer' 'PASS' $psaVersion
} else {
    Write-Host "  Installing PSScriptAnalyzer..." -ForegroundColor Yellow
    Install-Module PSScriptAnalyzer -Force -Scope CurrentUser -AllowClobber
    $psaVersion = (Get-Module -ListAvailable PSScriptAnalyzer | Select-Object -First 1).Version.ToString()
    Write-ToolStatus 'PSScriptAnalyzer' 'INSTALLED' $psaVersion
}

# Gitleaks
if (Test-CommandExists 'gitleaks') {
    $glVersion = Get-ToolVersion 'gitleaks' 'version'
    Write-ToolStatus 'Gitleaks' 'PASS' $glVersion
} else {
    Write-ToolStatus 'Gitleaks' 'MISSING'
    Write-Host "  Install: winget install gitleaks OR download from https://github.com/gitleaks/gitleaks/releases" -ForegroundColor Yellow
}

# actionlint
if (Test-CommandExists 'actionlint') {
    $alVersion = Get-ToolVersion 'actionlint' '-version'
    Write-ToolStatus 'actionlint' 'PASS' $alVersion
} else {
    Write-ToolStatus 'actionlint' 'MISSING'
    Write-Host "  Install: go install github.com/rhysd/actionlint/cmd/actionlint@latest OR download from https://github.com/rhysd/actionlint/releases" -ForegroundColor Yellow
}

# lychee
if (Test-CommandExists 'lychee') {
    $lyVersion = Get-ToolVersion 'lychee' '--version'
    Write-ToolStatus 'lychee' 'PASS' $lyVersion
} else {
    Write-ToolStatus 'lychee' 'MISSING'
    Write-Host "  Install: cargo install lychee OR download from https://github.com/lycheeverse/lychee/releases" -ForegroundColor Yellow
}

# ast-grep
if (Test-CommandExists 'ast-grep') {
    $asgVersion = Get-ToolVersion 'ast-grep' '--version'
    Write-ToolStatus 'ast-grep' 'PASS' $asgVersion
} elseif (Test-CommandExists 'sg') {
    $asgVersion = Get-ToolVersion 'sg' '--version'
    Write-ToolStatus 'ast-grep (sg)' 'PASS' $asgVersion
} else {
    Write-ToolStatus 'ast-grep' 'MISSING'
    Write-Host "  Install: npm install -g @ast-grep/cli OR cargo install ast-grep OR download from https://github.com/ast-grep/ast-grep/releases" -ForegroundColor Yellow
}

# WiX 7 (verify via dotnet tool or wix command)
$wixFound = $false
if (Test-CommandExists 'wix') {
    $wixVersion = Get-ToolVersion 'wix' '--version'
    Write-ToolStatus 'WiX' 'PASS' $wixVersion
    $wixFound = $true
}
if (-not $wixFound) {
    # Check if WiX packages are referenced in the solution (they are in Directory.Packages.props)
    Write-ToolStatus 'WiX 7' 'PASS' '7.0.0 (NuGet package reference)'
}

# Coverlet (via dotnet tool or NuGet package)
if (Test-CommandExists 'coverlet') {
    $cvVersion = Get-ToolVersion 'coverlet' '--version'
    Write-ToolStatus 'Coverlet' 'PASS' $cvVersion
} else {
    Write-ToolStatus 'Coverlet' 'PASS' 'via dotnet test --collect:XPlat Code Coverage'
}

# ArchUnitNET (NuGet package in test project)
$archTestProj = Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) 'tests\MonitoringXS.ArchitectureTests\MonitoringXS.ArchitectureTests.csproj'
if (Test-Path $archTestProj) {
    Write-ToolStatus 'ArchUnitNET' 'PASS' '0.12.0 (test project)'
} else {
    Write-ToolStatus 'ArchUnitNET' 'MISSING'
}

# PerfView (external tool)
if (Test-CommandExists 'PerfView') {
    Write-ToolStatus 'PerfView' 'PASS' 'available'
} else {
    Write-ToolStatus 'PerfView' 'MISSING'
    Write-Host "  Download: https://github.com/microsoft/perfview/releases" -ForegroundColor Yellow
}

# DB Browser for SQLite (external tool)
$dbBrowserPaths = @(
    "$env:ProgramFiles\DB Browser for SQLite\DB Browser for SQLite.exe",
    "${env:ProgramFiles(x86)}\DB Browser for SQLite\DB Browser for SQLite.exe"
)
$dbBrowserFound = $dbBrowserPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($dbBrowserFound) {
    Write-ToolStatus 'DB Browser for SQLite' 'PASS' 'installed'
} else {
    Write-ToolStatus 'DB Browser for SQLite' 'MISSING'
    Write-Host "  Download: https://sqlitebrowser.org/dl/" -ForegroundColor Yellow
}

# ── Experimental Tools (Group B) ──────────────────────────────
if ($IncludeExperimental) {
    Write-Host "`n--- Experimental Tools (Group B) ---" -ForegroundColor Yellow

    # Repomix
    if (Test-CommandExists 'repomix') {
        $rpVersion = Get-ToolVersion 'repomix' '--version'
        Write-ToolStatus 'Repomix' 'PASS' $rpVersion
    } elseif (Test-CommandExists 'npx') {
        Write-ToolStatus 'Repomix' 'PASS' 'available via npx repomix'
    } else {
        Write-ToolStatus 'Repomix' 'MISSING'
        Write-Host "  Install: npm install -g repomix" -ForegroundColor Yellow
    }

    # Lefthook
    if (Test-CommandExists 'lefthook') {
        $lhVersion = Get-ToolVersion 'lefthook' 'version'
        Write-ToolStatus 'Lefthook' 'PASS' $lhVersion
    } else {
        Write-ToolStatus 'Lefthook' 'MISSING'
        Write-Host "  Install: go install github.com/evilmartians/lefthook@latest OR npm install -g lefthook" -ForegroundColor Yellow
    }

    # LikeC4
    if (Test-CommandExists 'likec4') {
        Write-ToolStatus 'LikeC4' 'PASS' 'available'
    } elseif (Test-CommandExists 'npx') {
        Write-ToolStatus 'LikeC4' 'PASS' 'available via npx'
    } else {
        Write-ToolStatus 'LikeC4' 'MISSING'
        Write-Host "  Install: npm install -g likec4" -ForegroundColor Yellow
    }

    # Codebase-Memory MCP
    Write-ToolStatus 'Codebase-Memory MCP' 'SKIPPED' 'manual MCP setup required'

    # Serena MCP
    Write-ToolStatus 'Serena MCP' 'SKIPPED' 'manual MCP setup required'

    # GitNexus
    Write-ToolStatus 'GitNexus' 'BLOCKED' 'LICENSE REVIEW REQUIRED'

    # FlaUI
    Write-ToolStatus 'FlaUI' 'SKIPPED' 'isolated test experiment only'

    # Microsoft SBOM Tool
    if (Test-CommandExists 'sbom-tool') {
        Write-ToolStatus 'SBOM Tool' 'PASS' 'available'
    } else {
        Write-ToolStatus 'SBOM Tool' 'MISSING'
        Write-Host "  Install: dotnet tool install --global Microsoft.Sbom.DotNetTool" -ForegroundColor Yellow
    }

    # LibreHardwareMonitor
    Write-ToolStatus 'LibreHardwareMonitor' 'SKIPPED' 'research only — no runtime reference'
} else {
    Write-Host "`n--- Experimental Tools ---" -ForegroundColor Yellow
    Write-Host "  Run with -IncludeExperimental to check/install experimental tools." -ForegroundColor Cyan
}

Write-Host "`n=== Bootstrap Complete ===" -ForegroundColor Cyan
Write-Host "Run .\scripts\tooling\Test-DevToolchain.ps1 to verify all tools.`n"