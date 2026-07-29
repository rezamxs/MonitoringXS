[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Status', 'Remove')]
    [string]$Mode,
    [string]$RepositoryRoot = '',
    [string]$PackageRoot = '',
    [string]$UserSid = '',
    [string]$LogonSid = '',
    [int]$SessionId = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$serviceName = 'MonitoringXS.PrivilegedEtwBroker'
$serviceAccount = 'LocalSystem'
$installRootName = 'PrivilegedEtwBroker'
$sessionName = 'MonitoringXS.KernelMetrics.v1'
$scriptPath = $PSCommandPath

function Get-RepositoryRoot {
    param([string]$RequestedRoot)
    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        $candidate = (Resolve-Path -LiteralPath $RequestedRoot).Path
        if (Test-Path -LiteralPath (Join-Path $candidate 'MonitoringXS.sln')) {
            return $candidate
        }
        throw "Repository root does not contain MonitoringXS.sln: $candidate"
    }

    $directory = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $directory) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName 'MonitoringXS.sln')) {
            return $directory.FullName
        }
        $directory = $directory.Parent
    }
    throw 'Could not locate repository root containing MonitoringXS.sln.'
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ClientIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        throw 'The interactive user SID is unavailable.'
    }
    $logon = $identity.Groups | Where-Object {
        $_.IsWellKnown([Security.Principal.WellKnownSidType]::LogonIdsSid)
    } | Select-Object -First 1
    [ordered]@{
        UserSid = $identity.User.Value
        LogonSid = if ($null -eq $logon) { '' } else { $logon.Value }
        SessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    }
}

function Get-ProtocolVersion {
    param([string]$Root)
    $path = Join-Path $Root 'src\MonitoringXS.Platform.Windows\Broker\PrivilegedEtwBrokerProtocol.cs'
    $matches = @(Select-String -LiteralPath $path -Pattern 'public const ushort Version = (\d+);')
    if ($matches.Count -ne 1) {
        throw 'The authoritative broker protocol version could not be resolved.'
    }
    return [int]$matches[0].Matches[0].Groups[1].Value
}

function Get-InstallPaths {
    $root = Join-Path $env:ProgramData "MonitoringXS\$installRootName"
    [ordered]@{
        Root = $root
        Executable = Join-Path $root 'MonitoringXS.PrivilegedBroker.exe'
        Manifest = Join-Path $root 'broker-manifest.json'
    }
}

function Invoke-DotNetPublish {
    param(
        [string]$Root,
        [string]$OutputRoot
    )
    $project = Join-Path $Root 'src\MonitoringXS.PrivilegedBroker\MonitoringXS.PrivilegedBroker.csproj'
    $arguments = @('publish', $project, '-c', 'Release', '-o', $OutputRoot)
    $quoted = ($arguments | ForEach-Object {
        '"' + $_.Replace('"', '\"') + '"'
    }) -join ' '
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.Arguments = $quoted
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $null = $process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $out = $stdout.GetAwaiter().GetResult()
    $err = $stderr.GetAwaiter().GetResult()
    $global:LASTEXITCODE = $process.ExitCode
    if ($process.ExitCode -ne 0) {
        throw "Broker Release publish failed with exit code $($process.ExitCode).`n$err`n$out"
    }
}

function Invoke-Elevated {
    param([string]$ChildMode, [hashtable]$Arguments)
    $childArguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $script:ScriptPath,
        '-Mode', $ChildMode,
        '-RepositoryRoot', $script:RepositoryRoot
    )
    foreach ($key in $Arguments.Keys) {
        if (-not [string]::IsNullOrWhiteSpace([string]$Arguments[$key])) {
            $childArguments += @("-$key", [string]$Arguments[$key])
        }
    }
    $child = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $childArguments
    if ($child.ExitCode -ne 0) {
        throw "Elevated $ChildMode failed with exit code $($child.ExitCode)."
    }
}

function Invoke-Sc {
    param([string[]]$Arguments)
    & sc.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Service Control Manager operation failed with exit code $LASTEXITCODE."
    }
}

function Get-ServiceRecord {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return $null
    }
    $configuration = (& sc.exe qc $serviceName 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw 'Service configuration is not readable.'
    }
    $paths = Get-InstallPaths
    $pathMatches = $configuration.IndexOf(
        $paths.Executable,
        [StringComparison]::OrdinalIgnoreCase) -ge 0
    $account = if ($configuration.IndexOf(
        $serviceAccount,
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $serviceAccount
    } else {
        'Unknown'
    }
    [pscustomobject]@{
        Name = $service.Name
        State = $service.Status.ToString()
        StartMode = switch ($service.StartType.ToString()) {
            'Automatic' { 'Auto' }
            'Manual' { 'Manual' }
            'Disabled' { 'Disabled' }
            default { 'Unknown' }
        }
        StartName = $account
        PathName = if ($pathMatches) { "`"$($paths.Executable)`"" } else { '<unmanaged>' }
    }
}

function Get-ServiceExecutableFromPath {
    param([string]$PathName)
    if ($PathName -match '^\s*"([^"]+)"') {
        return $matches[1]
    }
    return ($PathName -split '\s+', 2)[0]
}

function Install-Internal {
    param(
        [string]$Root,
        [string]$Package,
        [string]$UserSid,
        [string]$LogonSid,
        [int]$SessionId
    )
    if (-not (Test-Administrator)) {
        throw 'Install requires an elevated PowerShell/UAC.'
    }
    $paths = Get-InstallPaths
    $managedInstallation = $false
    $installed = $false
    try {
        $expectedPackage = Join-Path $Package 'MonitoringXS.PrivilegedBroker.exe'
        if (-not (Test-Path -LiteralPath $expectedPackage)) {
            throw "Published broker executable is missing: $expectedPackage"
        }
        $existing = Get-ServiceRecord
        if ($null -ne $existing) {
            if ((Get-ServiceExecutableFromPath $existing.PathName) -ne $paths.Executable) {
                throw 'Refusing installation: existing service executable path is outside the managed installation directory.'
            }
            $managedInstallation = $true
            if ($existing.State -ne 'Stopped') {
                Invoke-Sc @('stop', $serviceName)
                Start-Sleep -Milliseconds 750
            }
            Invoke-Sc @('delete', $serviceName)
            Start-Sleep -Milliseconds 750
        } else {
            $managedInstallation = $true
        }
        if (Test-Path -LiteralPath $paths.Root) {
            & icacls.exe $paths.Root /grant:r '*S-1-5-32-544:(OI)(CI)(F)' /t | Out-Null
            Remove-Item -LiteralPath $paths.Root -Recurse -Force
        }
        New-Item -ItemType Directory -Path $paths.Root -Force | Out-Null
        Copy-Item -Path (Join-Path $Package '*') -Destination $paths.Root -Recurse -Force
        $productRoot = Split-Path -Parent $paths.Root
        & icacls.exe $productRoot /grant:r "*$($UserSid):(RX)" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not grant the intended user traverse access to the MonitoringXS ProgramData directory.'
        }
        & icacls.exe $paths.Root /inheritance:r /grant:r `
            '*S-1-5-18:(OI)(CI)(F)' `
            '*S-1-5-32-544:(OI)(CI)(F)' `
            "*$($UserSid):(OI)(CI)(RX)" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not apply the broker installation-directory ACL.'
        }

        $logonArgument = if ([string]::IsNullOrWhiteSpace($LogonSid)) {
            ''
        } else {
            " --logon-sid $LogonSid"
        }
        $binaryPath = "`"$($paths.Executable)`" --user-sid $UserSid --session $SessionId$logonArgument"
        Invoke-Sc @(
            'create',
            $serviceName,
            'binPath=', $binaryPath,
            'start=', 'auto',
            'obj=', $serviceAccount,
            'DisplayName=', 'Monitoring XS Privileged ETW Broker'
        )
        Invoke-Sc @('sidtype', $serviceName, 'unrestricted')
        Invoke-Sc @('description', $serviceName, 'Allowlisted Monitoring XS Network and Physical Disk ETW broker.')
        Invoke-Sc @('start', $serviceName)

        $service = Get-ServiceRecord
        $binary = Get-Item -LiteralPath $paths.Executable -ErrorAction SilentlyContinue
        if ($null -eq $service -or $service.State -ne 'Running') {
            throw 'Broker service was not Running after installation.'
        }
        if ($service.StartName -ne $serviceAccount -or $service.StartMode -ne 'Auto') {
            throw 'Broker service identity or start type verification failed.'
        }
        if ($null -eq $binary) {
            throw 'Installed broker executable verification failed.'
        }
        if ((Get-ServiceExecutableFromPath $service.PathName) -ne $paths.Executable) {
            throw 'Installed broker executable path verification failed.'
        }
        $protocol = Get-ProtocolVersion $Root
        [ordered]@{
            ServiceName = $serviceName
            ServiceAccount = $serviceAccount
            StartType = $service.StartMode
            BinaryVersion = $binary.VersionInfo.FileVersion
            BinarySha256 = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash
            ProtocolVersion = $protocol
            InstalledUtc = [DateTimeOffset]::UtcNow
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $paths.Manifest -Encoding UTF8
        $installed = $true
        Write-Output "Installed: $serviceName"
        Write-Output "State: Running; Account: LocalSystem; StartType: Auto"
        Write-Output "Binary: present; Protocol: v$protocol"
    } finally {
        if (-not $installed -and $managedInstallation) {
            & sc.exe stop $serviceName 2>$null | Out-Null
            & sc.exe delete $serviceName 2>$null | Out-Null
            if (Test-Path -LiteralPath $paths.Root) {
                & icacls.exe $paths.Root /grant:r '*S-1-5-32-544:(OI)(CI)(F)' /t | Out-Null
                Remove-Item -LiteralPath $paths.Root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Show-Status {
    $paths = Get-InstallPaths
    $service = Get-ServiceRecord
    if ($null -eq $service) {
        Write-Output "Installed: No"
        $script:StatusExitCode = 1
        return
    }
    $binary = Get-Item -LiteralPath $paths.Executable -ErrorAction SilentlyContinue
    $manifest = if (Test-Path -LiteralPath $paths.Manifest) {
        Get-Content -LiteralPath $paths.Manifest -Raw | ConvertFrom-Json
    } else {
        $null
    }
    $protocol = Get-ProtocolVersion $script:RepositoryRoot
    $pathMatches = $null -ne $binary -and
        ((Get-ServiceExecutableFromPath $service.PathName) -eq $paths.Executable)
    $protocolMatches = $null -ne $manifest -and
        [int]$manifest.ProtocolVersion -eq $protocol
    $healthy = $service.State -eq 'Running' -and
        $service.StartMode -eq 'Auto' -and
        $service.StartName -eq $serviceAccount -and
        $null -ne $binary -and $pathMatches -and $protocolMatches
    Write-Output "Installed: Yes"
    Write-Output "State: $($service.State)"
    Write-Output "StartType: $($service.StartMode)"
    Write-Output "Account: $($service.StartName)"
    Write-Output "Binary: $(if ($null -eq $binary) { 'missing' } else { 'present' })"
    Write-Output "Version: $(if ($null -eq $binary) { 'unknown' } else { $binary.VersionInfo.FileVersion })"
    Write-Output "ProtocolCompatibility: $(if ($protocolMatches) { 'compatible' } else { 'mismatch' })"
    $script:StatusExitCode = if ($healthy) { 0 } else { 1 }
}

function Remove-Internal {
    if (-not (Test-Administrator)) {
        throw 'Remove requires an elevated PowerShell/UAC.'
    }
    $paths = Get-InstallPaths
    $service = Get-ServiceRecord
    $managedServiceExisted = $false
    if ($null -ne $service) {
        $expectedExecutable = $paths.Executable
        if ((Get-ServiceExecutableFromPath $service.PathName) -ne $expectedExecutable) {
            throw 'Refusing removal: service executable path is outside the managed installation directory.'
        }
        $managedServiceExisted = $true
        if ($service.State -ne 'Stopped') {
            & sc.exe stop $serviceName 2>$null | Out-Null
            Start-Sleep -Milliseconds 750
        }
        Invoke-Sc @('delete', $serviceName)
        Start-Sleep -Milliseconds 750
    }
    if (Test-Path -LiteralPath $paths.Root) {
        & icacls.exe $paths.Root /grant:r '*S-1-5-32-544:(OI)(CI)(F)' /t | Out-Null
        Remove-Item -LiteralPath $paths.Root -Recurse -Force
    }
    if (Get-ServiceRecord) {
        throw 'Broker service still exists after removal.'
    }
    if ($managedServiceExisted) {
        # This dedicated session is owned by the managed broker after its service stops.
        & logman.exe stop $sessionName -ets 2>$null | Out-Null
    }
    Write-Output "Removed: $serviceName"
}

$script:RepositoryRoot = Get-RepositoryRoot $RepositoryRoot
$temporaryPackage = $null
$exitCode = 0
try {
    if ($Mode -eq 'Status') {
        $script:StatusExitCode = 1
        Show-Status
        $exitCode = $script:StatusExitCode
    } elseif ($Mode -eq 'Install') {
        $identity = if (-not [string]::IsNullOrWhiteSpace($UserSid) -and $SessionId -gt 0) {
            [ordered]@{ UserSid = $UserSid; LogonSid = $LogonSid; SessionId = $SessionId }
        } else {
            Get-ClientIdentity
        }
        if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
            $temporaryPackage = Join-Path ([IO.Path]::GetTempPath()) (
                "MonitoringXS.PrivilegedBroker.$([Guid]::NewGuid().ToString('N'))")
            New-Item -ItemType Directory -Path $temporaryPackage -Force | Out-Null
            Invoke-DotNetPublish $script:RepositoryRoot $temporaryPackage
        } else {
            $temporaryPackage = (Resolve-Path -LiteralPath $PackageRoot).Path
        }
        if (Test-Administrator) {
            Install-Internal $script:RepositoryRoot $temporaryPackage `
                $identity.UserSid $identity.LogonSid $identity.SessionId
        } else {
            Invoke-Elevated 'Install' @{
                PackageRoot = $temporaryPackage
                UserSid = $identity.UserSid
                LogonSid = $identity.LogonSid
                SessionId = $identity.SessionId
            }
        }
    } elseif ($Mode -eq 'Remove') {
        if (Test-Administrator) {
            Remove-Internal
        } else {
            Invoke-Elevated 'Remove' @{}
        }
    }
} catch {
    $exitCode = 1
    Write-Error $_.Exception.Message
} finally {
    if ($null -ne $temporaryPackage -and (Test-Path -LiteralPath $temporaryPackage)) {
        Remove-Item -LiteralPath $temporaryPackage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
