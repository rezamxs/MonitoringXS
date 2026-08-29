[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentMsi,
    [Parameter(Mandatory = $true)]
    [string]$PreviousMsi,
    [string]$EvidenceRoot = '',
    [switch]$RecoverPreexistingBrokerSnapshot,
    [switch]$SkipRollbackValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceName = 'MonitoringXS.PrivilegedEtwBroker'
$productName = 'Monitoring XS'
$currentProductCode = '{5F19D1C2-3DDD-4165-A5FE-9F296A509EB5}'
$previousProductCode = '{6E351D69-BE14-40D2-B59D-95073AEBB106}'
$installRoot = Join-Path $env:ProgramFiles 'Monitoring XS'
$appPath = Join-Path $installRoot 'App\MonitoringXS.App.exe'
$brokerPath = Join-Path $installRoot 'Broker\MonitoringXS.PrivilegedBroker.exe'
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Monitoring XS\Monitoring XS.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Monitoring XS.lnk'
$userDataRoot = Join-Path $env:LOCALAPPDATA 'MonitoringXS'
$sessionName = 'MonitoringXS.KernelMetrics.v1'
$taskName = "MonitoringXS-Installer-Validation-$([Guid]::NewGuid().ToString('N'))"
$results = [Collections.Generic.List[object]]::new()
$initialBroker = $null
$initialBrokerRestored = $false
$scheduledTask = $null
$msiPhaseTimeoutMilliseconds = 10 * 60 * 1000

function Assert-State {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Add-Result {
    param([string]$Phase, [string]$Result, [string]$Details = '')
    $script:Results.Add([ordered]@{ Phase = $Phase; Result = $Result; Details = $Details })
    Write-Host "$Phase`: $Result $Details"
}

function Start-MsiProcess {
    param([string[]]$MsiArguments, [string]$Phase)
    $argumentText = ($MsiArguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + $_.Replace('"', '\"') + '"'
        } else {
            $_
        }
    }) -join ' '
    $phaseMessage = "$(Get-Date -Format o) Phase: $Phase"
    Write-Host $phaseMessage
    Add-Content -LiteralPath (Join-Path $script:EvidenceRoot 'lifecycle-phases.log') `
        -Value $phaseMessage -Encoding UTF8
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') `
        -ArgumentList $argumentText -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($script:MsiPhaseTimeoutMilliseconds)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "MSI phase '$Phase' exceeded the 10-minute timeout."
    }
    return $process.ExitCode
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Msi {
    param(
        [ValidateSet('/i', '/x', '/fa')]
        [string]$Action,
        [string]$Target,
        [string]$LogName,
        [string[]]$Properties = @(),
        [bool]$ExpectSuccess = $true
    )
    $logPath = Join-Path $script:EvidenceRoot "logs\$LogName.log"
    $effectiveProperties = @($Properties)
    if ($Action -in @('/i', '/fa')) {
        $effectiveProperties += $script:BrokerIdentityProperties
    }
    $arguments = @($Action, $Target, '/qn', '/norestart') + $effectiveProperties + @('/L*v', $logPath)
    [int]$exitCode = Start-MsiProcess $arguments $LogName
    if ($ExpectSuccess) {
        Assert-State ($exitCode -in @(0, 3010)) "MSI $Action failed with exit code $exitCode."
    } else {
        Assert-State ($exitCode -notin @(0, 3010)) "MSI $Action unexpectedly succeeded."
    }
    $null = Add-Result $LogName ($(if ($exitCode -eq 3010) { 'RebootRequired' } elseif ($exitCode -eq 0) { 'Passed' } else { 'FailedAsExpected' })) "ExitCode=$exitCode"
    return [int]$exitCode
}

function Wait-ServiceAbsent {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Broker service remained after the bounded removal wait.'
}

function Get-ProductEntries {
    $roots = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    return @(Get-ItemProperty $roots -ErrorAction SilentlyContinue |
        Where-Object {
            $null -ne $_.PSObject.Properties['DisplayName'] -and
            $_.DisplayName -eq $productName
        })
}

function Assert-ServiceHealthy {
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    Assert-State ($service.Status -eq 'Running') 'Broker service is not running.'
    $configuration = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    Assert-State ([int]$configuration.Start -eq 2) 'Broker service is not Automatic.'
    Assert-State ([string]$configuration.ObjectName -eq 'LocalSystem') 'Broker service is not LocalSystem.'
    $expectedPrefix = '"' + $brokerPath + '" --user-sid S-1-5-21-'
    Assert-State ([string]$configuration.ImagePath -like "$expectedPrefix*") 'Broker executable path or arguments are not safely quoted.'
    $sidType = (& sc.exe qsidtype $serviceName 2>&1 | Out-String)
    Assert-State ($LASTEXITCODE -eq 0 -and $sidType -match 'UNRESTRICTED') 'Broker service SID type is not unrestricted.'
    Assert-State (Test-Path -LiteralPath $brokerPath) 'Broker executable is missing.'

    $unsafeSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
    $writeRights = [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in (Get-Acl -LiteralPath (Split-Path -Parent $brokerPath)).Access) {
        try {
            $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        } catch {
            continue
        }
        $unsafe = $sid -in $unsafeSids -and
            $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (($rule.FileSystemRights -band $writeRights) -ne 0)
        Assert-State (-not $unsafe) "Broker directory grants write access to $sid."
    }
}

function Assert-Installed {
    param([string]$Version, [bool]$DesktopExpected)
    Assert-State (Test-Path -LiteralPath $appPath) 'Application executable is missing.'
    Assert-ServiceHealthy
    $products = @(Get-ProductEntries)
    Assert-State ($products.Count -eq 1) 'Expected exactly one uninstall registration.'
    Assert-State ([string]$products[0].DisplayVersion -eq $Version) 'Installed product version is inconsistent.'
    Assert-State (Test-Path -LiteralPath $startMenuShortcut) 'Start Menu shortcut is missing.'
    Assert-State ((Test-Path -LiteralPath $desktopShortcut) -eq $DesktopExpected) 'Desktop shortcut selection was not honored.'
    $shell = New-Object -ComObject WScript.Shell
    $startTarget = $shell.CreateShortcut($startMenuShortcut).TargetPath
    Assert-State ($startTarget -eq $appPath) 'Start Menu shortcut target is incorrect.'
    if ($DesktopExpected) {
        Assert-State ($shell.CreateShortcut($desktopShortcut).TargetPath -eq $appPath) 'Desktop shortcut target is incorrect.'
    }
}

function Assert-Uninstalled {
    Wait-ServiceAbsent
    Assert-State (-not (Test-Path -LiteralPath $installRoot)) 'Installer-owned files remain after uninstall.'
    Assert-State (-not (Test-Path -LiteralPath $startMenuShortcut)) 'Start Menu shortcut remains after uninstall.'
    Assert-State (-not (Test-Path -LiteralPath $desktopShortcut)) 'Desktop shortcut remains after uninstall.'
    Assert-State (@(Get-ProductEntries).Count -eq 0) 'Uninstall registration remains.'
    Assert-State (@(Get-Process -Name 'MonitoringXS.App','MonitoringXS.PrivilegedBroker' -ErrorAction SilentlyContinue).Count -eq 0) 'Monitoring XS process remains after uninstall.'
    & logman.exe query $sessionName -ets 2>$null | Out-Null
    Assert-State ($LASTEXITCODE -ne 0) 'Monitoring XS ETW session remains after uninstall.'
}

function Start-AppUnelevated {
    $taskService = New-Object -ComObject 'Schedule.Service'
    $taskService.Connect()
    $root = $taskService.GetFolder('\')
    $definition = $taskService.NewTask(0)
    $definition.RegistrationInfo.Description = 'Temporary Monitoring XS installer validation task.'
    $definition.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $definition.Principal.LogonType = 3
    $definition.Principal.RunLevel = 0
    $definition.Settings.Enabled = $true
    $definition.Settings.AllowDemandStart = $true
    $action = $definition.Actions.Create(0)
    $action.Path = $appPath
    $action.WorkingDirectory = Split-Path -Parent $appPath
    $script:ScheduledTask = $root.RegisterTaskDefinition($taskName, $definition, 6, $null, $null, 3, $null)
    $null = $script:ScheduledTask.Run($null)

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $process = Get-Process -Name 'MonitoringXS.App' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $appPath } | Select-Object -First 1
        if ($null -ne $process) {
            Start-Sleep -Seconds 8
            Assert-State (-not $process.HasExited) 'Installed app exited during launch smoke.'
            return $process
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Installed app did not start through a least-privilege interactive task.'
}

function Stop-App {
    $processes = @(Get-Process -Name 'MonitoringXS.App' -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        $null = $process.CloseMainWindow()
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (@(Get-Process -Name 'MonitoringXS.App' -ErrorAction SilentlyContinue).Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    Get-Process -Name 'MonitoringXS.App' -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Remove-AnyMsiProduct {
    foreach ($code in @($currentProductCode, $previousProductCode)) {
        $entry = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$code" -ErrorAction SilentlyContinue
        if ($null -ne $entry) {
            $exitCode = Start-MsiProcess @('/x', $code, '/qn', '/norestart') 'cleanup-uninstall'
            Assert-State ($exitCode -in @(0, 3010)) "Cleanup uninstall failed with exit code $exitCode."
        }
    }
}

function Snapshot-And-Remove-InitialBroker {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }
    $configuration = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $match = [regex]::Match([string]$configuration.ImagePath, '^"?([^" ]+\.exe)"? --user-sid (S-[^ ]+) --session (\d+)(?: --logon-sid (S-[^ ]+))?$')
    Assert-State ($match.Success) 'Refusing to replace an unrecognized pre-existing Broker service.'
    $executable = $match.Groups[1].Value
    $expectedRoot = Join-Path $env:ProgramData 'MonitoringXS\PrivilegedEtwBroker'
    Assert-State ((Split-Path -Parent $executable) -eq $expectedRoot) 'Pre-existing Broker is outside its development path.'
    Assert-State ($service.Status -eq 'Running' -and $service.StartType -eq 'Automatic') 'Pre-existing Broker state cannot be restored exactly by this validation.'
    $snapshot = Join-Path $script:EvidenceRoot 'machine-state\preexisting-broker'
    New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
    Copy-Item -Path (Join-Path $expectedRoot '*') -Destination $snapshot -Recurse -Force
    $script:InitialBroker = [ordered]@{
        Package = $snapshot
        UserSid = $match.Groups[2].Value
        SessionId = [int]$match.Groups[3].Value
        LogonSid = $match.Groups[4].Value
    }
    $manager = Join-Path $script:RepositoryRoot 'scripts\privileged-broker\Manage-PrivilegedBroker.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $manager -Mode Remove -RepositoryRoot $script:RepositoryRoot
    Assert-State ($LASTEXITCODE -eq 0) 'Could not remove the pre-existing development Broker.'
    Wait-ServiceAbsent
    Add-Result 'preexisting-broker-snapshot' 'Passed' 'Running/Automatic development Broker saved for restoration.'
}

function Restore-InitialBroker {
    if ($null -eq $script:InitialBroker) {
        return
    }
    $manager = Join-Path $script:RepositoryRoot 'scripts\privileged-broker\Manage-PrivilegedBroker.ps1'
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $manager,
        '-Mode', 'Install', '-RepositoryRoot', $script:RepositoryRoot,
        '-PackageRoot', $script:InitialBroker.Package,
        '-UserSid', $script:InitialBroker.UserSid,
        '-SessionId', [string]$script:InitialBroker.SessionId
    )
    if (-not [string]::IsNullOrWhiteSpace($script:InitialBroker.LogonSid)) {
        $arguments += @('-LogonSid', $script:InitialBroker.LogonSid)
    }
    & powershell.exe @arguments
    Assert-State ($LASTEXITCODE -eq 0) 'Could not restore the pre-existing development Broker.'
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    $configuration = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $expectedExecutable = Join-Path $env:ProgramData 'MonitoringXS\PrivilegedEtwBroker\MonitoringXS.PrivilegedBroker.exe'
    Assert-State ($service.Status -eq 'Running' -and [int]$configuration.Start -eq 2) 'Restored development Broker state is incorrect.'
    Assert-State ([string]$configuration.ObjectName -eq 'LocalSystem') 'Restored development Broker identity is incorrect.'
    Assert-State ([string]$configuration.ImagePath -like "$expectedExecutable --user-sid *") 'Restored development Broker path is incorrect.'
    $script:InitialBrokerRestored = $true
    Add-Result 'preexisting-broker-restore' 'Passed' 'Original development Broker restored Running/Automatic.'
}

function New-FailingBrokerMsi {
    param([string]$Source, [string]$Destination)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    $dtfAssembly = Join-Path $env:USERPROFILE (
        '.nuget\packages\wixtoolset.dtf.windowsinstaller\7.0.0\lib\net20\WixToolset.Dtf.WindowsInstaller.dll')
    Add-Type -Path $dtfAssembly
    $database = [WixToolset.Dtf.WindowsInstaller.Database]::new(
        $Destination,
        [WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::Direct)
    $database.Execute(
        "UPDATE `CustomAction` SET `Target` = 'MissingEntryForRollbackTest' WHERE `Action` = 'ConfigureBrokerServiceSid'")
    $database.Commit()
    $database.Dispose()
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$script:RepositoryRoot = $repositoryRoot
Assert-State (Test-Administrator) 'Lifecycle validation must run elevated.'
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentLogonSid = $currentIdentity.Groups | Where-Object {
    $_.IsWellKnown([Security.Principal.WellKnownSidType]::LogonIdsSid)
} | Select-Object -First 1
Assert-State ($null -ne $currentIdentity.User -and $currentIdentity.User.IsAccountSid()) 'Validation user SID is unavailable.'
Assert-State ([Diagnostics.Process]::GetCurrentProcess().SessionId -gt 0) 'Validation interactive session is unavailable.'
$script:BrokerIdentityProperties = @(
    "BROKER_USER_SID=$($currentIdentity.User.Value)",
    "BROKER_SESSION_ID=$([Diagnostics.Process]::GetCurrentProcess().SessionId)"
)
if ($null -ne $currentLogonSid) {
    $script:BrokerIdentityProperties += "BROKER_LOGON_ARGUMENT=--logon-sid $($currentLogonSid.Value)"
}
$current = (Resolve-Path -LiteralPath $CurrentMsi).Path
$previous = (Resolve-Path -LiteralPath $PreviousMsi).Path
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repositoryRoot '.artifacts\validation\installer-packaging\lifecycle'
}
$script:EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$allowedPrefix = (Join-Path $repositoryRoot '.artifacts\validation\installer-packaging').TrimEnd('\') + '\'
Assert-State ($script:EvidenceRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) 'Evidence root is outside the installer validation tree.'
New-Item -ItemType Directory -Path (Join-Path $script:EvidenceRoot 'logs') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $script:EvidenceRoot 'path with spaces') -Force | Out-Null

$spacedCurrent = Join-Path $script:EvidenceRoot 'path with spaces\Monitoring XS 1.0.0.msi'
$failingMsi = Join-Path $script:EvidenceRoot (
    "path with spaces\Monitoring XS rollback-$([Guid]::NewGuid().ToString('N')).msi")
$initialUserFiles = @{}
foreach ($name in @('settings.json', 'history.db', 'attribution-overrides.json')) {
    $path = Join-Path $userDataRoot $name
    $initialUserFiles[$name] = Test-Path -LiteralPath $path
}

if ($RecoverPreexistingBrokerSnapshot) {
    $snapshot = Join-Path $script:EvidenceRoot 'machine-state\preexisting-broker'
    Assert-State (Test-Path -LiteralPath (Join-Path $snapshot 'MonitoringXS.PrivilegedBroker.exe')) 'Pre-existing Broker snapshot is unavailable for recovery.'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $logonSid = $identity.Groups | Where-Object {
        $_.IsWellKnown([Security.Principal.WellKnownSidType]::LogonIdsSid)
    } | Select-Object -First 1
    $script:InitialBroker = [ordered]@{
        Package = $snapshot
        UserSid = $identity.User.Value
        SessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        LogonSid = if ($null -eq $logonSid) { '' } else { $logonSid.Value }
    }
    Add-Result 'preexisting-broker-recovery' 'Passed' 'Snapshot from the interrupted preflight will be restored during cleanup.'
}

$failure = $null
try {
    Copy-Item -LiteralPath $current -Destination $spacedCurrent -Force
    if (-not $SkipRollbackValidation) {
        New-FailingBrokerMsi $spacedCurrent $failingMsi
    }
    Remove-AnyMsiProduct
    Snapshot-And-Remove-InitialBroker
    Assert-Uninstalled

    if (-not $SkipRollbackValidation) {
        $failureExitCode = Invoke-Msi '/i' $failingMsi 'rollback-failed-service-configuration' @() $false
        Assert-State ($failureExitCode -eq 1603) "Rollback test returned unexpected exit code $failureExitCode."
        $failureLog = Get-Content -LiteralPath (
            Join-Path $script:EvidenceRoot 'logs\rollback-failed-service-configuration.log') -Raw
        Assert-State ($failureLog -match 'MissingEntryForRollbackTest' -and
            $failureLog -match 'Return value 3') 'Rollback test did not fail in the deferred Broker custom action.'
        Assert-Uninstalled
        Add-Result 'rollback-safe-failure' 'Passed' 'Deferred Broker configuration failure left no product, service, or files.'
    }

    $null = Invoke-Msi '/i' $spacedCurrent 'clean-install'
    Assert-Installed '1.0.0' $false
    Add-Result 'clean-install-state' 'Passed' 'Default install omitted Desktop shortcut.'

    $null = Invoke-Msi '/i' $spacedCurrent 'already-installed' @('ADDLOCAL=ProductFeature,DesktopShortcutFeature')
    Assert-Installed '1.0.0' $true
    Add-Result 'already-installed-state' 'Passed' 'Explicit Desktop feature added without duplicate product.'

    Stop-App
    $repairFile = Join-Path $installRoot 'App\MonitoringXS.Core.dll'
    Remove-Item -LiteralPath $repairFile -Force
    & sc.exe config $serviceName start= demand | Out-Null
    Assert-State ($LASTEXITCODE -eq 0) 'Could not create repair test state.'
    $null = Invoke-Msi '/fa' $spacedCurrent 'repair'
    Assert-State (Test-Path -LiteralPath $repairFile) 'Repair did not restore the removed application file.'
    Assert-Installed '1.0.0' $true
    Add-Result 'repair-state' 'Passed' 'File and Automatic/Running service definition restored.'

    $app = Start-AppUnelevated
    Assert-State ($app.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId) 'App launched in the wrong session.'
    Add-Result 'normal-app-launch' 'Passed' 'Least-privilege interactive task; app remained responsive for 8 seconds.'
    $null = Invoke-Msi '/x' $currentProductCode 'uninstall-running-app'
    Assert-Uninstalled
    Add-Result 'uninstall-state' 'Passed' 'Running app/Broker, files, shortcuts, service, registration, and ETW session removed.'

    $null = Invoke-Msi '/i' $previous 'previous-install'
    Assert-Installed '0.9.0' $false
    $null = Invoke-Msi '/i' $spacedCurrent 'major-upgrade'
    Assert-Installed '1.0.0' $false
    Assert-State ($null -eq (Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$previousProductCode" -ErrorAction SilentlyContinue)) 'Previous product registration remains after upgrade.'
    Add-Result 'major-upgrade-state' 'Passed' 'Previous product removed; one current product and service remain.'
    $null = Invoke-Msi '/x' $currentProductCode 'upgrade-uninstall'
    Assert-Uninstalled

    $null = Invoke-Msi '/i' $spacedCurrent 'repeat-install'
    Assert-Installed '1.0.0' $false
    $null = Invoke-Msi '/x' $currentProductCode 'repeat-uninstall'
    Assert-Uninstalled
    Add-Result 'repeat-cycle' 'Passed' 'Second clean install/uninstall completed.'

    foreach ($name in $initialUserFiles.Keys) {
        if ($initialUserFiles[$name]) {
            Assert-State (Test-Path -LiteralPath (Join-Path $userDataRoot $name)) "Existing user data was removed: $name"
        }
    }
    Add-Result 'user-data-preservation' 'Passed' 'All pre-existing Settings/History/override files remain.'
} catch {
    $failure = $_
} finally {
    Stop-App
    try {
        $taskService = New-Object -ComObject 'Schedule.Service'
        $taskService.Connect()
        $taskService.GetFolder('\').DeleteTask($taskName, 0)
    } catch {
    }
    Remove-AnyMsiProduct
    try {
        Assert-Uninstalled
    } catch {
        if ($null -eq $failure) {
            $failure = $_
        } else {
            Add-Result 'cleanup' 'Failed' $_.Exception.Message
        }
    }
    try {
        Restore-InitialBroker
    } catch {
        if ($null -eq $failure) {
            $failure = $_
        } else {
            Add-Result 'broker-restore' 'Failed' $_.Exception.Message
        }
    }
    $report = [ordered]@{
        GeneratedUtc = [DateTimeOffset]::UtcNow
        CurrentMsi = $current
        PreviousMsi = $previous
        Results = $results
        UserDataRoot = $userDataRoot
        InitialBrokerRestored = $initialBrokerRestored
        FinalResult = if ($null -eq $failure) { 'Passed' } else { 'Failed' }
        Failure = if ($null -eq $failure) { $null } else { $failure.Exception.Message }
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (
        Join-Path $script:EvidenceRoot 'lifecycle-validation.json') -Encoding UTF8
}

if ($null -ne $failure) {
    throw $failure
}
Write-Output 'Installer lifecycle validation: Passed'
