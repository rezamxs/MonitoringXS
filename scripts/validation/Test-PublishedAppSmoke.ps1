[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class PublishedAppWindow
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@

function Get-AppWindow {
    param([int]$ProcessId)

    [IntPtr[]]$found = @([IntPtr]::Zero)
    [PublishedAppWindow]::EnumWindows({
        param($window, $state)

        [uint32]$owner = 0
        [PublishedAppWindow]::GetWindowThreadProcessId($window, [ref]$owner) | Out-Null
        if ($owner -ne $ProcessId) {
            return $true
        }

        $className = [Text.StringBuilder]::new(128)
        [PublishedAppWindow]::GetClassName($window, $className, $className.Capacity) | Out-Null
        if ($className.ToString() -eq 'WinUIDesktopWin32WindowClass') {
            $found[0] = $window
            return $false
        }

        return $true
    }, [IntPtr]::Zero) | Out-Null
    return $found[0]
}

$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executable = Join-Path $publishPath 'MonitoringXS.App.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Published executable is missing: $executable"
}

$started = Get-Date
$process = Start-Process -FilePath $executable -WorkingDirectory $publishPath -PassThru
$window = [IntPtr]::Zero
$windowDeadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    if ($process.HasExited) {
        throw "Published app exited during startup with code $($process.ExitCode)."
    }
    $window = Get-AppWindow $process.Id
    if ($window -ne [IntPtr]::Zero) {
        break
    }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $windowDeadline)

if ($window -eq [IntPtr]::Zero) {
    throw 'Published app did not create its WinUI window within 10 seconds.'
}

[IntPtr]$messageResult = [IntPtr]::Zero
$responsive = [PublishedAppWindow]::SendMessageTimeout(
    $window,
    0,
    [IntPtr]::Zero,
    [IntPtr]::Zero,
    2,
    2000,
    [ref]$messageResult) -ne [IntPtr]::Zero
if (-not $responsive) {
    throw 'Published app window did not respond within two seconds.'
}

$remaining = $started.AddSeconds(10) - (Get-Date)
if ($remaining -gt [TimeSpan]::Zero) {
    Start-Sleep -Milliseconds ([int][Math]::Ceiling($remaining.TotalMilliseconds))
}
if ($process.HasExited) {
    throw "Published app did not remain alive for 10 seconds; exit code $($process.ExitCode)."
}

if (-not [PublishedAppWindow]::PostMessage(
    $window,
    0x0010,
    [IntPtr]::Zero,
    [IntPtr]::Zero)) {
    throw 'Could not request a controlled window close.'
}
if (-not $process.WaitForExit(10000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw 'Published app did not exit after the controlled window close.'
}
if ($process.ExitCode -ne 0) {
    throw "Published app returned exit code $($process.ExitCode) after the controlled close."
}

Start-Sleep -Seconds 2
$ended = Get-Date
$fatalEvents = @(Get-WinEvent -FilterHashtable @{
    LogName = 'Application'
    StartTime = $started
    EndTime = $ended
    ProviderName = @('Application Error', 'Windows Error Reporting', '.NET Runtime')
} -ErrorAction SilentlyContinue | Where-Object {
    $_.Message -match 'MonitoringXS\.App'
})
if ($fatalEvents.Count -ne 0) {
    throw "Application Error, WER, or CoreCLR fatal event detected: $($fatalEvents[0].Id)."
}

[pscustomobject]@{
    PublishDirectory = $publishPath
    AliveForSeconds = [Math]::Round(($ended - $started).TotalSeconds, 1)
    WindowResponsive = $responsive
    ExitCode = $process.ExitCode
    FatalEventCount = $fatalEvents.Count
    Result = 'Passed'
} | ConvertTo-Json
