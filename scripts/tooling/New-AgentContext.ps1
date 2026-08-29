<#
.SYNOPSIS
    Generate focused context packs for AI agent consumption.
.DESCRIPTION
    Creates compact, scoped context packages for Codex/Qwen review
    instead of whole-repository dumps. Uses Repomix if available,
    otherwise falls back to manual file concatenation.
.PARAMETER ContextName
    Name of the context pack (e.g., Installer, Localization, Broker, History, ProcessActions).
.PARAMETER OutputDirectory
    Directory for generated context packs (default: context-packs/).
.EXAMPLE
    .\scripts\tooling\New-AgentContext.ps1 -ContextName Broker
    .\scripts\tooling\New-AgentContext.ps1 -ContextName Installer -OutputDirectory .\my-packs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Installer', 'Localization', 'Broker', 'History', 'ProcessActions', 'Full')]
    [string]$ContextName,

    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent | Split-Path -Parent

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'context-packs'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$outputFile = Join-Path $OutputDirectory "$ContextName-context.md"

Write-Host "`n=== Generating Context Pack: $ContextName ===" -ForegroundColor Cyan
Write-Host "Output: $outputFile`n"

# Define file sets per context
$contextFiles = switch ($ContextName) {
    'Installer' {
        @(
            'installer/**/*.ps1'
            'installer/**/*.cs'
            'installer/**/*.wxs'
            'installer/**/*.props'
            'scripts/installer/**/*.ps1'
            'docs/INSTALLER.md'
            'tests/MonitoringXS.IntegrationTests/InstallerLifecycleScriptTests.cs'
        )
    }
    'Localization' {
        @(
            'src/MonitoringXS.App/Strings/**/*.resw'
            'tests/MonitoringXS.App.Tests/LocalizationTests.cs'
            'docs/adr/*localization*'
        )
    }
    'Broker' {
        @(
            'src/MonitoringXS.PrivilegedBroker/**/*.cs'
            'src/MonitoringXS.PrivilegedBroker/**/*.csproj'
            'scripts/privileged-broker/**/*.ps1'
            'tests/MonitoringXS.IntegrationTests/PrivilegedBrokerManagementScriptTests.cs'
            'tests/MonitoringXS.IntegrationTests/PrivilegedEtwBrokerTests.cs'
            'docs/SECURITY.md'
        )
    }
    'History' {
        @(
            'src/MonitoringXS.Storage/**/*.cs'
            'src/MonitoringXS.Storage/**/*.csproj'
            'tests/MonitoringXS.Storage.Tests/**/*.cs'
            'docs/METRICS.md'
        )
    }
    'ProcessActions' {
        @(
            'src/MonitoringXS.Platform.Windows/**/ProcessAction*.cs'
            'src/MonitoringXS.Application/**/ProcessAction*.cs'
            'src/MonitoringXS.App/**/ProcessAction*.cs'
            'tests/MonitoringXS.IntegrationTests/WindowsProcessActionServiceTests.cs'
            'tests/MonitoringXS.App.Tests/ProcessActionsViewModelTests.cs'
            'tests/MonitoringXS.ProcessActionTestHelper/**/*'
        )
    }
    'Full' {
        @(
            'src/**/*.cs'
            'tests/**/*.cs'
            'docs/**/*.md'
            'AGENTS.md'
        )
    }
}

# Try Repomix first
$repomixAvailable = $null -ne (Get-Command 'repomix' -ErrorAction SilentlyContinue)

if ($repomixAvailable -and $ContextName -ne 'Full') {
    Write-Host "Using Repomix for context generation..." -ForegroundColor Yellow
    # Build include pattern for repomix
    $includePatterns = ($contextFiles | Where-Object { $_ -notmatch '\*\*' } | Select-Object -First 5) -join ','
    $repomixArgs = @(
        '--output', $outputFile
        '--style', 'markdown'
        '--no-gitignore'
    )
    # Repomix doesn't support complex globs well, use directory-based approach
    $targetDir = switch ($ContextName) {
        'Installer'      { Join-Path $repoRoot 'installer' }
        'Localization'   { Join-Path $repoRoot 'src\MonitoringXS.App\Strings' }
        'Broker'         { Join-Path $repoRoot 'src\MonitoringXS.PrivilegedBroker' }
        'History'        { Join-Path $repoRoot 'src\MonitoringXS.Storage' }
        'ProcessActions' { Join-Path $repoRoot 'src' }
        default          { $repoRoot }
    }
    & repomix @repomixArgs $targetDir 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0 -and (Test-Path $outputFile)) {
        $size = (Get-Item $outputFile).Length
        Write-Host "Context pack generated via Repomix: $([math]::Round($size / 1KB, 1)) KB" -ForegroundColor Green
    } else {
        Write-Host "Repomix failed, falling back to manual concatenation." -ForegroundColor Yellow
        $repomixAvailable = $false
    }
}

if (-not $repomixAvailable) {
    Write-Host "Generating context pack via file concatenation..." -ForegroundColor Yellow
    $header = @"
# Monitoring XS — $ContextName Context Pack
Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Repository: https://github.com/rezamxs/MonitoringXS

---

"@
    $header | Set-Content -Path $outputFile -Encoding utf8

    foreach ($pattern in $contextFiles) {
        $fullPattern = Join-Path $repoRoot $pattern
        $matchedFiles = Get-ChildItem -Path $fullPattern -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' }

        foreach ($file in $matchedFiles) {
            $relativePath = $file.FullName.Substring($repoRoot.Length + 1)
            $extension = $file.Extension.TrimStart('.')
            $lang = switch ($extension) {
                'cs'    { 'csharp' }
                'ps1'   { 'powershell' }
                'xml'   { 'xml' }
                'resw'  { 'xml' }
                'wxs'   { 'xml' }
                'md'    { 'markdown' }
                'json'  { 'json' }
                'yml'   { 'yaml' }
                'yaml'  { 'yaml' }
                default { '' }
            }
            $content = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue
            if ($content) {
                $block = @"

## ``$relativePath``

``````$lang
$content
``````

"@
                Add-Content -Path $outputFile -Value $block -Encoding utf8
            }
        }
    }

    $size = (Get-Item $outputFile).Length
    Write-Host "Context pack generated: $([math]::Round($size / 1KB, 1)) KB" -ForegroundColor Green
}

Write-Host "`n=== Context Pack Complete ===" -ForegroundColor Cyan
Write-Host "File: $outputFile"
Write-Host "Use this file as focused context for AI code review.`n"