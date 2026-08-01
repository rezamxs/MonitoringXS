[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,
    [string]$AppPublishDirectory = '',
    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Installer {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Read-InstallerRows {
    param([string]$Query, [int]$FieldCount)
    $view = $script:Database.OpenView($Query)
    $null = $view.Execute()
    while ($record = $view.Fetch()) {
        $values = @()
        for ($field = 1; $field -le $FieldCount; $field++) {
            $values += [string]$record.StringData($field)
        }
        [pscustomobject]@{ Fields = $values }
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repositoryRoot '.artifacts\validation\installer-packaging'
}
if ([string]::IsNullOrWhiteSpace($AppPublishDirectory)) {
    $versionProperties = [xml](Get-Content -LiteralPath (
        Join-Path $repositoryRoot 'installer\InstallerVersion.props') -Raw)
    $version = [string]$versionProperties.Project.PropertyGroup.InstallerProductVersion
    $AppPublishDirectory = Join-Path $EvidenceRoot "publish\$version\app"
}

$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$script:Database = $windowsInstaller.OpenDatabase($resolvedMsi, 0)

$properties = @{}
foreach ($row in Read-InstallerRows 'SELECT `Property`,`Value` FROM `Property`' 2) {
    $properties[$row.Fields[0]] = $row.Fields[1]
}
Assert-Installer ($properties.ProductName -eq 'Monitoring XS') 'Unexpected MSI product name.'
Assert-Installer ($properties.ALLUSERS -eq '1') 'Installer is not per-machine.'
Assert-Installer ($properties.UpgradeCode -eq '{75C0FAA3-D030-4D64-9C7B-37FC72154ADD}') 'UpgradeCode changed.'
Assert-Installer ($properties.SecureCustomProperties -match 'BROKER_USER_SID' -and
    $properties.SecureCustomProperties -match 'BROKER_SESSION_ID' -and
    $properties.SecureCustomProperties -match 'BROKER_LOGON_ARGUMENT') 'Broker identity properties are not secure.'

$service = @(Read-InstallerRows 'SELECT `Name`,`ServiceType`,`StartType`,`StartName`,`Arguments`,`Component_` FROM `ServiceInstall`' 6)
Assert-Installer ($service.Count -eq 1) 'Expected exactly one installed service.'
Assert-Installer ($service[0].Fields[0] -eq 'MonitoringXS.PrivilegedEtwBroker') 'Broker service name changed.'
Assert-Installer ($service[0].Fields[1] -eq '16' -and $service[0].Fields[2] -eq '2') 'Broker must be own-process and automatic.'
Assert-Installer ($service[0].Fields[3] -eq 'LocalSystem') 'Broker must run as LocalSystem.'
$expectedArguments = '--user-sid [BROKER_USER_SID] --session [BROKER_SESSION_ID] [BROKER_LOGON_ARGUMENT]'
Assert-Installer ($service[0].Fields[4] -eq $expectedArguments) 'Broker arguments changed or became unsafe.'

$serviceControl = @(Read-InstallerRows 'SELECT `Name`,`Event`,`Wait` FROM `ServiceControl`' 3)
Assert-Installer ($serviceControl.Count -eq 1 -and $serviceControl[0].Fields[0] -eq $service[0].Fields[0]) 'Broker ServiceControl is missing.'
Assert-Installer ($serviceControl[0].Fields[1] -eq '163' -and $serviceControl[0].Fields[2] -eq '1') 'Broker start/stop/remove behavior changed.'

$features = @(Read-InstallerRows 'SELECT `Feature`,`Feature_Parent`,`Level`,`Attributes` FROM `Feature`' 4)
$desktopFeature = @($features | Where-Object { $_.Fields[0] -eq 'DesktopShortcutFeature' })
Assert-Installer ($desktopFeature.Count -eq 1 -and $desktopFeature[0].Fields[1] -eq 'ProductFeature' -and $desktopFeature[0].Fields[2] -eq '2') 'Desktop shortcut is not an explicit optional feature.'

$shortcuts = @(Read-InstallerRows 'SELECT `Shortcut`,`Directory_`,`Component_`,`Target`,`WkDir` FROM `Shortcut`' 5)
Assert-Installer (@($shortcuts | Where-Object { $_.Fields[0] -eq 'StartMenuShortcut' -and $_.Fields[1] -eq 'ProgramMenuDirectory' }).Count -eq 1) 'Start Menu shortcut is missing.'
Assert-Installer (@($shortcuts | Where-Object { $_.Fields[0] -eq 'DesktopShortcut' -and $_.Fields[1] -eq 'DesktopFolder' }).Count -eq 1) 'Desktop shortcut feature is missing.'
foreach ($shortcut in $shortcuts) {
    Assert-Installer ($shortcut.Fields[3] -eq '[APPFOLDER]MonitoringXS.App.exe' -and $shortcut.Fields[4] -eq 'APPFOLDER') 'Shortcut target or working directory is unsafe.'
}

$customActions = @(Read-InstallerRows 'SELECT `Action`,`Source`,`Target` FROM `CustomAction`' 3)
$productActions = @($customActions | Where-Object { $_.Fields[1] -eq 'InstallerCustomActions' })
Assert-Installer ($productActions.Count -eq 2) 'Unexpected product custom-action count.'
Assert-Installer (@($productActions | Where-Object { $_.Fields[0] -eq 'CaptureBrokerIdentity' -and $_.Fields[2] -eq 'Capture' }).Count -eq 1) 'Identity capture action is missing.'
Assert-Installer (@($productActions | Where-Object { $_.Fields[0] -eq 'ConfigureBrokerServiceSid' -and $_.Fields[2] -eq 'SetUnrestrictedSid' }).Count -eq 1) 'Service SID action is missing.'

$files = @(Read-InstallerRows 'SELECT `File`,`FileName`,`Language` FROM `File`' 3)
$forbiddenExtensions = @('.pdb', '.ps1', '.log', '.dmp', '.db', '.pfx', '.p12', '.key')
foreach ($file in $files) {
    $longName = ($file.Fields[1] -split '\|')[-1]
    Assert-Installer (-not ($forbiddenExtensions -contains [IO.Path]::GetExtension($longName).ToLowerInvariant())) "Forbidden payload: $longName"
    if ($file.Fields[2].Length -gt 20) {
        Assert-Installer ($file.Fields[0] -in @('WinUiXamlLibrary', 'WinUiXamlPhoneLibrary')) "Unexpected MSI File.Language overflow: $longName"
    }
}

$appExecutable = Join-Path $AppPublishDirectory 'MonitoringXS.App.exe'
Assert-Installer (Test-Path -LiteralPath $appExecutable) 'Published app executable is missing.'
$manifestText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($appExecutable))
Assert-Installer ($manifestText.Contains('requestedExecutionLevel level="asInvoker"')) 'Published app is not asInvoker.'
Assert-Installer (-not $manifestText.Contains('requireAdministrator')) 'Published app requests elevation.'

$report = [ordered]@{
    MsiPath = $resolvedMsi
    SizeBytes = (Get-Item -LiteralPath $resolvedMsi).Length
    ProductVersion = $properties.ProductVersion
    ProductCode = $properties.ProductCode
    UpgradeCode = $properties.UpgradeCode
    FileCount = $files.Count
    Service = 'LocalSystem/Automatic'
    ProductCustomActions = @($productActions | ForEach-Object { $_.Fields[0] })
    AppManifest = 'asInvoker'
    Result = 'Passed'
}
$reportDirectory = Join-Path $EvidenceRoot 'static'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (
    Join-Path $reportDirectory "package-validation-$($properties.ProductVersion).json") -Encoding UTF8
$report | ConvertTo-Json -Depth 4
