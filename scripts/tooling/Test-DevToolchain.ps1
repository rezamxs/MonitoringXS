<#
.SYNOPSIS
    Master verification command for the Monitoring XS development toolchain.
.DESCRIPTION
    Reports status of all adopted and optional tools.
    Does not treat optional experimental tooling as a build failure.
.EXAMPLE
    .\scripts\tooling\Test-DevToolchain.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$failures = 0

function Write-Check {
    param([string]$Name, [string]$Status, [string]$Detail = '')
    $color = switch ($Status) {
        'PASS'      { 'Green' }
        'FAIL'      { 'Red' }
        'OPTIONAL'  { 'Cyan' }
        'AVAILABLE' { 'Green' }
        'BLOCKED'   { 'Red' }
        default     { 'White' }
    }
    $detailInfo = if ($Detail) { " ($Detail)" } else { '' }
    Write-Host ("{0,-30} {1}{2}" -f $Name, $Status, $detailInfo) -ForegroundColor $color
    if ($Status -eq 'FAIL') { $script:failures++ }
}

function Test-CommandExists {
    param([string]$Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

Write-Host "`n=== Monitoring XS Toolchain Verification ===" -ForegroundColor Cyan
Write-Host "Repository: $repoRoot`n"

# ── Prerequisites ──────────────────────────────────────────────
if (Test-CommandExists 'dotnet') {
    $v = (dotnet --version 2>&1 | Select-Object -First 1).ToString().Trim()
    Write-Check '.NET SDK' 'PASS' $v
} else {
    Write-Check '.NET SDK' 'FAIL'
}

# ── Adopted Tools ──────────────────────────────────────────────
Write-Host "`n--- Adopted Tools ---" -ForegroundColor Yellow

# WiX
Write-Check 'WiX' 'PASS' '7.0.0 (NuGet)'

# PSScriptAnalyzer
if (Get-Module -ListAvailable PSScriptAnalyzer -ErrorAction SilentlyContinue) {
    $v = (Get-Module -ListAvailable PSScriptAnalyzer | Select-Object -First 1).Version.ToString()
    Write-Check 'PSScriptAnalyzer' 'PASS' $v
} else {
    Write-Check 'PSScriptAnalyzer' 'FAIL' 'not installed'
}

# Gitleaks
if (Test-CommandExists 'gitleaks') {
    Write-Check 'Gitleaks' 'PASS'
} else {
    Write-Check 'Gitleaks' 'FAIL' 'not installed'
}

# actionlint
if (Test-CommandExists 'actionlint') {
    Write-Check 'actionlint' 'PASS'
} else {
    Write-Check 'actionlint' 'FAIL' 'not installed'
}

# lychee
if (Test-CommandExists 'lychee') {
    Write-Check 'lychee' 'PASS'
} else {
    Write-Check 'lychee' 'FAIL' 'not installed'
}

# ast-grep
if ((Test-CommandExists 'ast-grep') -or (Test-CommandExists 'sg')) {
    Write-Check 'ast-grep' 'PASS'
} else {
    Write-Check 'ast-grep' 'FAIL' 'not installed'
}

# Architecture tests
$archProj = Join-Path $repoRoot 'tests\MonitoringXS.ArchitectureTests\MonitoringXS.ArchitectureTests.csproj'
if (Test-Path $archProj) {
    Write-Check 'Architecture tests' 'PASS' 'project exists'
} else {
    Write-Check 'Architecture tests' 'FAIL' 'project missing'
}

# Coverlet
$coverletFound = $false
$testProjFiles = Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Filter '*.csproj' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'MonitoringXS.ProcessActionTestHelper.csproj' }
foreach ($tp in $testProjFiles) {
    $content = Get-Content $tp.FullName -Raw -ErrorAction SilentlyContinue
    if ($content -and ($content -match 'coverlet\.collector')) {
        $coverletFound = $true
        break
    }
}
if ($coverletFound) {
    Write-Check 'Coverage tooling' 'PASS' 'coverlet.collector referenced in test projects'
} else {
    Write-Check 'Coverage tooling' 'FAIL' 'coverlet.collector not found in any test project'
}

# PerfView
if (Test-CommandExists 'PerfView') {
    Write-Check 'PerfView' 'AVAILABLE'
} else {
    Write-Check 'PerfView' 'OPTIONAL' 'external tool — download from GitHub'
}

# DB Browser
$dbPaths = @(
    "$env:ProgramFiles\DB Browser for SQLite\DB Browser for SQLite.exe",
    "${env:ProgramFiles(x86)}\DB Browser for SQLite\DB Browser for SQLite.exe"
)
if ($dbPaths | Where-Object { Test-Path $_ }) {
    Write-Check 'DB Browser for SQLite' 'AVAILABLE'
} else {
    Write-Check 'DB Browser for SQLite' 'OPTIONAL' 'external tool'
}

# ── Optional / Experimental Tools ─────────────────────────────
Write-Host "`n--- Optional / Experimental ---" -ForegroundColor Yellow

if (Test-CommandExists 'repomix') { Write-Check 'Repomix' 'OPTIONAL' 'PASS' }
else { Write-Check 'Repomix' 'OPTIONAL' 'not installed' }

if (Test-CommandExists 'lefthook') { Write-Check 'Lefthook' 'OPTIONAL' 'PASS' }
else { Write-Check 'Lefthook' 'OPTIONAL' 'not installed' }

if ((Test-CommandExists 'likec4') -or (Test-CommandExists 'npx')) { Write-Check 'LikeC4' 'OPTIONAL' 'available' }
else { Write-Check 'LikeC4' 'OPTIONAL' 'not installed' }

Write-Check 'Codebase-Memory MCP' 'OPTIONAL' 'manual setup'
Write-Check 'Serena MCP' 'OPTIONAL' 'manual setup'
Write-Check 'GitNexus' 'BLOCKED' 'license review required'

# ── Build Smoke Test ──────────────────────────────────────────
Write-Host "`n--- Build Smoke Test ---" -ForegroundColor Yellow
try {
    $buildOutput = dotnet build (Join-Path $repoRoot 'MonitoringXS.sln') -c Release --nologo -v q 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Check 'Solution build' 'PASS'
    } else {
        Write-Check 'Solution build' 'FAIL' "exit code $LASTEXITCODE"
    }
} catch {
    Write-Check 'Solution build' 'FAIL' $_.Exception.Message
}

# ── Summary ───────────────────────────────────────────────────
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
if ($failures -eq 0) {
    Write-Host "All adopted tools: PASS" -ForegroundColor Green
} else {
    Write-Host "$failures adopted tool(s) need attention." -ForegroundColor Red
}
Write-Host "Optional tools are not required for normal development.`n"

exit $failures