<#
.SYNOPSIS
    Run all static analysis tools against the Monitoring XS repository.
.DESCRIPTION
    Runs PSScriptAnalyzer, actionlint, Gitleaks, ast-grep, and lychee.
    Reports results without failing on optional tools that are not installed.
.EXAMPLE
    .\scripts\tooling\Invoke-StaticAnalysis.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$totalIssues = 0

function Test-CommandExists {
    param([string]$Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

Write-Host "`n=== Static Analysis ===" -ForegroundColor Cyan
Write-Host "Repository: $repoRoot`n"

# -- PSScriptAnalyzer ------------------------------------------
Write-Host "--- PSScriptAnalyzer ---" -ForegroundColor Yellow
if (Get-Module -ListAvailable PSScriptAnalyzer -ErrorAction SilentlyContinue) {
    Import-Module PSScriptAnalyzer -ErrorAction Stop
    $settingsPath = Join-Path $repoRoot 'PSScriptAnalyzerSettings.psd1'
    $scriptPaths = Get-ChildItem -Path (Join-Path $repoRoot 'scripts') -Filter '*.ps1' -Recurse -ErrorAction SilentlyContinue
    if ($scriptPaths) {
        $results = foreach ($script in $scriptPaths) {
            Invoke-ScriptAnalyzer -Path $script.FullName -Settings $settingsPath -ErrorAction SilentlyContinue
        }
        $errors = @($results | Where-Object { $_.Severity -eq 'Error' })
        $warnings = @($results | Where-Object { $_.Severity -eq 'Warning' })
        Write-Host "  Scripts analyzed: $($scriptPaths.Count)"
        Write-Host "  Errors: $($errors.Count), Warnings: $($warnings.Count)"
        if ($errors.Count -gt 0) {
            $errors | ForEach-Object { Write-Host "  ERROR: $($_.ScriptName):$($_.Line) - $($_.Message)" -ForegroundColor Red }
            $totalIssues += $errors.Count
        }
        if ($warnings.Count -gt 0) {
            $warnings | ForEach-Object { Write-Host "  WARN:  $($_.ScriptName):$($_.Line) - $($_.Message)" -ForegroundColor Yellow }
        }
    } else {
        Write-Host "  No PowerShell scripts found." -ForegroundColor Cyan
    }
} else {
    Write-Host "  SKIPPED - PSScriptAnalyzer not installed" -ForegroundColor Cyan
}

# -- actionlint ------------------------------------------------
Write-Host "`n--- actionlint ---" -ForegroundColor Yellow
if (Test-CommandExists 'actionlint') {
    $workflowDir = Join-Path $repoRoot '.github\workflows'
    if (Test-Path $workflowDir) {
        $alOutput = & actionlint (Join-Path $workflowDir '*.yml') 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  PASS - no issues found" -ForegroundColor Green
        } else {
            Write-Host "  Issues found:" -ForegroundColor Red
            $alOutput | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            $totalIssues++
        }
    }
} else {
    Write-Host "  SKIPPED - actionlint not installed" -ForegroundColor Cyan
}

# -- Gitleaks --------------------------------------------------
Write-Host "`n--- Gitleaks ---" -ForegroundColor Yellow
if (Test-CommandExists 'gitleaks') {
    $configPath = Join-Path $repoRoot '.gitleaks.toml'
    $glArgs = @('detect', '--source', $repoRoot, '--no-git', '-v')
    if (Test-Path $configPath) { $glArgs += @('--config', $configPath) }
    $glOutput = & gitleaks @glArgs 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  PASS - no secrets detected" -ForegroundColor Green
    } else {
        Write-Host "  Secrets or findings detected - review output above" -ForegroundColor Red
        $totalIssues++
    }
} else {
    Write-Host "  SKIPPED - gitleaks not installed" -ForegroundColor Cyan
}

# -- ast-grep --------------------------------------------------
Write-Host "`n--- ast-grep ---" -ForegroundColor Yellow
$astGrepCmd = if (Test-CommandExists 'ast-grep') { 'ast-grep' } elseif (Test-CommandExists 'sg') { 'sg' } else { $null }
if ($astGrepCmd) {
    $rulesPath = Join-Path $repoRoot '.ast-grep\rules.yml'
    if (Test-Path $rulesPath) {
        $asgOutput = & $astGrepCmd scan --config $rulesPath (Join-Path $repoRoot 'src') 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  PASS - no rule violations" -ForegroundColor Green
        } else {
            Write-Host "  Rule violations found - review output above" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  No rules file found at .ast-grep/rules.yml" -ForegroundColor Cyan
    }
} else {
    Write-Host "  SKIPPED - ast-grep not installed" -ForegroundColor Cyan
}

# -- lychee ----------------------------------------------------
Write-Host "`n--- lychee ---" -ForegroundColor Yellow
if (Test-CommandExists 'lychee') {
    $configPath = Join-Path $repoRoot '.lychee.toml'
    $lyArgs = @()
    if (Test-Path $configPath) { $lyArgs += @('--config', $configPath) }
    $lyArgs += (Join-Path $repoRoot 'README.md')
    $lyArgs += (Join-Path $repoRoot 'docs\**\*.md')
    $lyOutput = & lychee @lyArgs 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  PASS - all links valid" -ForegroundColor Green
    } else {
        Write-Host "  Broken links found - review output above" -ForegroundColor Yellow
    }
} else {
    Write-Host "  SKIPPED - lychee not installed" -ForegroundColor Cyan
}

# -- Summary ---------------------------------------------------
Write-Host "`n=== Static Analysis Complete ===" -ForegroundColor Cyan
if ($totalIssues -eq 0) {
    Write-Host "No blocking issues found.`n" -ForegroundColor Green
} else {
    Write-Host "$totalIssues issue(s) require attention.`n" -ForegroundColor Red
}

exit $totalIssues