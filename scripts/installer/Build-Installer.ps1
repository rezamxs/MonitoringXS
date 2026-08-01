[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^$|^\d+\.\d+\.\d+$')]
    [string]$Version = '',
    [ValidatePattern('^$|^[{(]?[0-9A-Fa-f-]{36}[)}]?$')]
    [string]$ProductCode = '',
    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$versionProperties = [xml](Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'installer\InstallerVersion.props') -Raw)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string]$versionProperties.Project.PropertyGroup.InstallerProductVersion
}
if ([string]::IsNullOrWhiteSpace($ProductCode)) {
    $ProductCode = [string]$versionProperties.Project.PropertyGroup.InstallerProductCode
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repositoryRoot '.artifacts\validation\installer-packaging'
}
$evidencePath = [IO.Path]::GetFullPath($EvidenceRoot)
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (-not $evidencePath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer evidence root must remain inside the repository.'
}

function Reset-OutputDirectory {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $evidencePrefix = $evidencePath.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($evidencePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset output outside installer evidence root: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

$appPublish = Join-Path $evidencePath "publish\$Version\app"
$brokerPublish = Join-Path $evidencePath "publish\$Version\broker"
$packageRoot = Join-Path $evidencePath 'package'
Reset-OutputDirectory $appPublish
Reset-OutputDirectory $brokerPublish
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

$commonPublish = @(
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    "-p:Version=$Version",
    '--nologo'
)
$appArguments = @(
    'publish',
    (Join-Path $repositoryRoot 'src\MonitoringXS.App\MonitoringXS.App.csproj')
) + $commonPublish + @(
    '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=true',
    '-o', $appPublish
)
Invoke-DotNet $appArguments
$brokerArguments = @(
    'publish',
    (Join-Path $repositoryRoot 'src\MonitoringXS.PrivilegedBroker\MonitoringXS.PrivilegedBroker.csproj')
) + $commonPublish + @(
    '-o', $brokerPublish
)
Invoke-DotNet $brokerArguments

$installerProject = Join-Path $repositoryRoot 'installer\MonitoringXS.Installer\MonitoringXS.Installer.wixproj'
Invoke-DotNet @(
    'build', $installerProject,
    '-c', $Configuration,
    "-p:InstallerProductVersion=$Version",
    "-p:InstallerProductCode=$ProductCode",
    "-p:InstallerEvidenceRoot=$evidencePath",
    "-p:AppPublishDir=$appPublish",
    "-p:BrokerPublishDir=$brokerPublish",
    '--nologo'
)

$msi = Join-Path $packageRoot "MonitoringXS-$Version-x64.msi"
if (-not (Test-Path -LiteralPath $msi)) {
    throw "Expected installer output is missing: $msi"
}
$item = Get-Item -LiteralPath $msi
Write-Output "Installer: $($item.FullName)"
Write-Output "SizeBytes: $($item.Length)"
