<#
.SYNOPSIS
    Run tests with code coverage collection for Monitoring XS.
.DESCRIPTION
    Runs dotnet test with Coverlet/XPlat Code Coverage collector.
    Generates line and branch coverage in cobertura format.
    Does not instrument Release product binaries distributed to users.
.PARAMETER Configuration
    Build configuration (default: Debug).
.PARAMETER OutputDirectory
    Directory for coverage output (default: TestResults/Coverage).
.EXAMPLE
    .\scripts\tooling\Invoke-Coverage.ps1
    .\scripts\tooling\Invoke-Coverage.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$solutionPath = Join-Path $repoRoot 'MonitoringXS.sln'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'TestResults\Coverage'
}

Write-Host "`n=== Coverage Collection ===" -ForegroundColor Cyan
Write-Host "Solution: $solutionPath"
Write-Host "Configuration: $Configuration"
Write-Host "Output: $OutputDirectory`n"

# Ensure output directory exists
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Restore first
Write-Host "Restoring packages..." -ForegroundColor Yellow
dotnet restore $solutionPath --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Restore failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Build
Write-Host "Building solution..." -ForegroundColor Yellow
dotnet build $solutionPath -c $Configuration --no-restore --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Test with coverage
Write-Host "Running tests with coverage collection..." -ForegroundColor Yellow
$testArgs = @(
    'test', $solutionPath
    '-c', $Configuration
    '--no-build'
    '--collect:XPlat Code Coverage'
    '--results-directory', $OutputDirectory
    '--', 'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura'
)

dotnet @testArgs
$testExitCode = $LASTEXITCODE

# Find and report coverage files
$coverageFiles = Get-ChildItem -Path $OutputDirectory -Filter 'coverage.cobertura.xml' -Recurse -ErrorAction SilentlyContinue
if ($coverageFiles) {
    Write-Host "`nCoverage files generated:" -ForegroundColor Green
    foreach ($file in $coverageFiles) {
        Write-Host "  $($file.FullName)" -ForegroundColor Green
    }
    Write-Host "`nTo view coverage:" -ForegroundColor Cyan
    Write-Host "  - Visual Studio: Open .cobertura.xml via Test Explorer" -ForegroundColor Cyan
    Write-Host "  - ReportGenerator: dotnet tool install -g dotnet-reportgenerator-globaltool" -ForegroundColor Cyan
    Write-Host "    reportgenerator -reports:$OutputDirectory\**\coverage.cobertura.xml -targetdir:$OutputDirectory\Report -reporttypes:Html" -ForegroundColor Cyan
} else {
    Write-Host "`nNo coverage files found. Tests may have failed." -ForegroundColor Yellow
}

Write-Host "`n=== Coverage Collection Complete ===" -ForegroundColor Cyan
exit $testExitCode