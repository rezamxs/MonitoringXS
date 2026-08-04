param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateRange(10, 600)]
    [int]$DurationSeconds = 60,
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class MonitoringXsAutomationNative
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
'@

$started = [DateTimeOffset]::UtcNow
$runner = $null
$app = $null
$elementsRead = 0
$toggleCount = 0
$resizeCount = 0
$automationErrors = 0
$responsive = $true
$tabOpened = $false
$expanded = $false
$closeRequested = $false
$cleanExit = $false

function Get-Root {
    param([Diagnostics.Process]$Process)
    return [System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
}

function Get-Descendants {
    param([System.Windows.Automation.AutomationElement]$Root)
    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
}

function Find-ApplicationCard {
    param([System.Windows.Automation.AutomationElement]$Root)
    foreach ($element in (Get-Descendants -Root $Root))
    {
        try
        {
            if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                $element.Current.Name -match 'Running.*Physical disk')
            {
                return $element
            }
        }
        catch
        {
        }
    }

    return $null
}

function Find-Expander {
    param([System.Windows.Automation.AutomationElement]$Root)
    foreach ($element in (Get-Descendants -Root $Root))
    {
        try
        {
            if ($element.Current.Name -eq 'Advanced application information')
            {
                return $element
            }
        }
        catch
        {
        }
    }

    return $null
}

try
{
    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $runner = Start-Process -FilePath 'dotnet' -WorkingDirectory $RepositoryRoot -ArgumentList @(
        'run',
        '--project',
        '.\src\MonitoringXS.App\MonitoringXS.App.csproj',
        '-c',
        $Configuration,
        '--no-build') -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while ([DateTimeOffset]::UtcNow -lt $deadline -and $null -eq $app)
    {
        # WMI process enumeration is denied in some non-elevated validation sessions.
        # The harness launches one app instance, so the process name is sufficient.
        $candidate = Get-Process -Name 'MonitoringXS.App' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate)
        {
            $app = $candidate
        }
        else
        {
            Start-Sleep -Milliseconds 200
        }
    }

    if ($null -eq $app)
    {
        throw 'MonitoringXS.App.exe did not start within 60 seconds.'
    }

    while ([DateTimeOffset]::UtcNow -lt $deadline)
    {
        $app.Refresh()
        if ($app.MainWindowHandle -ne 0)
        {
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if ($app.MainWindowHandle -eq 0)
    {
        throw 'The Monitoring XS main window was not observed.'
    }

    [void][MonitoringXsAutomationNative]::SetForegroundWindow($app.MainWindowHandle)
    $root = Get-Root -Process $app
    $card = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline -and $null -eq $card)
    {
        $card = Find-ApplicationCard -Root $root
        if ($null -eq $card)
        {
            Start-Sleep -Milliseconds 250
        }
    }

    if ($null -eq $card)
    {
        throw 'No keyboard-focusable application card was exposed to UI Automation.'
    }

    $scrollItem = $card.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
    $scrollItem.ScrollIntoView()
    $card.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Seconds 1

    $root = Get-Root -Process $app
    foreach ($element in (Get-Descendants -Root $root))
    {
        try
        {
            if ($element.Current.Name -match ' application tab$')
            {
                $tabOpened = $true
                break
            }
        }
        catch
        {
        }
    }

    if (-not $tabOpened)
    {
        throw 'Keyboard activation did not open an application tab.'
    }

    $expander = Find-Expander -Root $root
    if ($null -eq $expander)
    {
        throw 'The Advanced application information expander was not exposed.'
    }

    $expandPattern = $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expandPattern.Expand()
    $expanded = $true
    $toggleCount++

    $originalRect = [MonitoringXsAutomationNative+Rect]::new()
    [void][MonitoringXsAutomationNative]::GetWindowRect($app.MainWindowHandle, [ref]$originalRect)
    $stopAt = [DateTimeOffset]::UtcNow.AddSeconds($DurationSeconds)
    $iteration = 0
    while ([DateTimeOffset]::UtcNow -lt $stopAt -and -not $app.HasExited)
    {
        try
        {
            $root = Get-Root -Process $app
            foreach ($element in (Get-Descendants -Root $root))
            {
                $null = $element.Current.Name
                $null = $element.Current.ControlType
                $elementsRead++
            }

            if ($iteration % 10 -eq 0)
            {
                $expander = Find-Expander -Root $root
                $expandPattern = $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                if ($expandPattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded)
                {
                    $expandPattern.Collapse()
                }
                else
                {
                    $expandPattern.Expand()
                }
                $toggleCount++
            }

            if ($iteration % 5 -eq 0)
            {
                $width = if (($resizeCount % 2) -eq 0) { 1100 } else { 1180 }
                $height = if (($resizeCount % 2) -eq 0) { 720 } else { 760 }
                [void][MonitoringXsAutomationNative]::SetWindowPos(
                    $app.MainWindowHandle,
                    [IntPtr]::Zero,
                    0,
                    0,
                    $width,
                    $height,
                    0x0002 -bor 0x0004)
                $resizeCount++
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException]
        {
            $automationErrors++
        }

        $app.Refresh()
        $responsive = $responsive -and $app.Responding
        $iteration++
        Start-Sleep -Milliseconds 100
    }

    $exitedBeforeClose = $app.HasExited
    if (-not $exitedBeforeClose)
    {
        [void][MonitoringXsAutomationNative]::SetWindowPos(
            $app.MainWindowHandle,
            [IntPtr]::Zero,
            0,
            0,
            $originalRect.Right - $originalRect.Left,
            $originalRect.Bottom - $originalRect.Top,
            0x0002 -bor 0x0004)
        $closeRequested = $app.CloseMainWindow()
        $cleanExit = $app.WaitForExit(15000)
    }

    if (-not $runner.HasExited)
    {
        [void]$runner.WaitForExit(10000)
    }

    $crashes = @(Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            StartTime = $started.LocalDateTime
        } -ErrorAction SilentlyContinue | Where-Object {
            $_.ProviderName -eq 'Application Error' -and $_.Message -match 'MonitoringXS.App'
        })

    $result = [pscustomobject]@{
        StartedUtc = $started.ToString('O')
        Configuration = $Configuration
        DurationSeconds = $DurationSeconds
        MainWindowObserved = $true
        ApplicationTabOpenedByKeyboard = $tabOpened
        ExpanderOpened = $expanded
        ElementsRead = $elementsRead
        ExpanderToggles = $toggleCount
        WindowResizes = $resizeCount
        AutomationErrors = $automationErrors
        ResponsiveAtEverySample = $responsive
        ExitedBeforeClose = $exitedBeforeClose
        CloseRequested = $closeRequested
        CleanExit = $cleanExit
        DotnetRunExitCode = if ($runner.HasExited) { $runner.ExitCode } else { $null }
        NewApplicationCrashEvents = $crashes.Count
    }
    $result | ConvertTo-Json

    if ($exitedBeforeClose -or
        -not $responsive -or
        -not $cleanExit -or
        $runner.ExitCode -ne 0 -or
        $crashes.Count -ne 0)
    {
        exit 1
    }
}
finally
{
    if ($null -ne $app -and -not $app.HasExited)
    {
        try
        {
            [void]$app.CloseMainWindow()
            [void]$app.WaitForExit(10000)
        }
        catch
        {
        }
    }

    if ($null -ne $runner -and -not $runner.HasExited)
    {
        try
        {
            [void]$runner.WaitForExit(10000)
        }
        catch
        {
        }
    }
}
