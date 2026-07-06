<#
.SYNOPSIS
  Installer + control wrapper for AudioSwitcher.exe.

  Builds the C# daemon, installs a scheduled task that runs it elevated at
  logon, and exposes status/lock/reset commands. Day-to-day, the daemon
  runs on its own; you never need this script except to install/uninstall
  or query state.

.USAGE
  .\AudioSwitcher.ps1 -Install            build .exe, install scheduled task, start daemon
  .\AudioSwitcher.ps1 -Uninstall          stop daemon, remove scheduled task, delete .exe
  .\AudioSwitcher.ps1 -Build              just compile, don't install
  .\AudioSwitcher.ps1 -Start              start the daemon (e.g. after Uninstall+Install)
  .\AudioSwitcher.ps1 -Stop               stop the daemon
  .\AudioSwitcher.ps1 -Status             query the running daemon
  .\AudioSwitcher.ps1 -ListDevices        list audio endpoints
  .\AudioSwitcher.ps1 -ShowState          print learned state
  .\AudioSwitcher.ps1 -Lock <exe> <tier>  pin a game to a tier (0..4)
  .\AudioSwitcher.ps1 -Reset              wipe learned overrides
  .\AudioSwitcher.ps1 -TestETW            run ETW for 30s standalone
  .\AudioSwitcher.ps1 -Foreground         run daemon in this terminal (debug)

.NOTES
  Must be run as Administrator for -Install/-Uninstall/-Foreground.
#>

[CmdletBinding(DefaultParameterSetName = 'Default')]
param(
    [Parameter(ParameterSetName='Install')]      [switch]$Install,
    [Parameter(ParameterSetName='Uninstall')]    [switch]$Uninstall,
    [Parameter(ParameterSetName='Build')]        [switch]$Build,
    [Parameter(ParameterSetName='Start')]        [switch]$Start,
    [Parameter(ParameterSetName='Stop')]         [switch]$Stop,
    [Parameter(ParameterSetName='Status')]       [switch]$Status,
    [Parameter(ParameterSetName='ListDevices')]  [switch]$ListDevices,
    [Parameter(ParameterSetName='ShowState')]    [switch]$ShowState,
    [Parameter(ParameterSetName='Reset')]        [switch]$Reset,
    [Parameter(ParameterSetName='TestETW')]      [switch]$TestETW,
    [Parameter(ParameterSetName='Lock')]         [string]$Lock,
    [Parameter(ParameterSetName='Lock')]         [int]$LockTier = -1,
    [Parameter(ParameterSetName='Foreground')]   [switch]$Foreground,
    [Parameter(ParameterSetName='Foreground')]   [switch]$Verbose2
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir = "$env:LOCALAPPDATA\AudioSwitcher\bin"
$Exe = "$InstallDir\AudioSwitcher.exe"
$SourceCs = "$ScriptDir\AudioSwitcher.cs"
$TaskName = "AudioSwitcherDaemon"

function Test-Task { [bool](Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) }

function Test-Admin {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($current)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Admin {
    if (-not (Test-Admin)) {
        Write-Error "Must be run as Administrator."
        exit 1
    }
}

# If not elevated, relaunch this script elevated with $Flag (e.g. '-Install') in a new
# window and return $true (caller should stop). Returns $false when already admin.
function Invoke-Elevated([string]$Flag) {
    if (Test-Admin) { return $false }
    Write-Host "  '$Flag' needs administrator - opening an elevated window..." -ForegroundColor Yellow
    try {
        Start-Process powershell -Verb RunAs -ArgumentList `
            "-NoExit -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $Flag"
    } catch {
        Write-Host "  Elevation cancelled." -ForegroundColor DarkGray
    }
    return $true
}

function Find-Csc {
    # Try modern dotnet first
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) { return @{ Tool = "dotnet"; Path = $dotnet.Source } }

    # Fall back to bundled .NET Framework csc.exe
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return @{ Tool = "csc"; Path = $c } }
    }
    return $null
}

function Build-Exe {
    if (-not (Test-Path $SourceCs)) {
        Write-Error "Source not found: $SourceCs"
        exit 1
    }
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    $compiler = Find-Csc
    if (-not $compiler) {
        Write-Error "No C# compiler found. Install .NET SDK or .NET Framework 4.x."
        exit 1
    }

    Write-Host "Building $Exe using $($compiler.Tool)..." -ForegroundColor Cyan

    if ($compiler.Tool -eq "dotnet") {
        # Build the committed project file. Contributors can also run
        #   dotnet publish AudioSwitcher.csproj -c Release -r win-x64 --self-contained false
        # directly, or open it in an IDE.
        $proj = Join-Path $ScriptDir "AudioSwitcher.csproj"
        if (-not (Test-Path $proj)) {
            Write-Error "AudioSwitcher.csproj not found next to the script - copy it alongside AudioSwitcher.cs."
            exit 1
        }
        & dotnet publish $proj -c Release -r win-x64 --self-contained false -o $InstallDir 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    } else {
        # .NET Framework csc - won't support System.Text.Json or modern features.
        # Need to fall back to Newtonsoft.Json or hand-roll JSON. For simplicity,
        # warn the user that they need .NET 6+ SDK.
        Write-Error @"
Only .NET Framework csc.exe was found. This project uses System.Text.Json
which requires .NET 6+ SDK.

Install: https://dotnet.microsoft.com/download (any current version)

Or: winget install Microsoft.DotNet.SDK.8
"@
        exit 1
    }

    if (Test-Path $Exe) {
        Write-Host "Built: $Exe" -ForegroundColor Green
    } else {
        Write-Error "Build appeared to succeed but $Exe not found"
        exit 1
    }
}

# Make sure $Exe exists at the install location. Order: already installed ->
# prebuilt exe bundled next to this script (the release zip) -> compile from source.
function Ensure-Exe {
    if (Test-Path $Exe) { return $true }
    $bundled = Join-Path $ScriptDir "AudioSwitcher.exe"
    if (Test-Path $bundled) {
        if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
        Copy-Item $bundled $Exe -Force
        Write-Host "  Using bundled AudioSwitcher.exe -> $InstallDir" -ForegroundColor DarkGray
        return $true
    }
    if (Test-Path $SourceCs) { Build-Exe; return (Test-Path $Exe) }
    Write-Error "No AudioSwitcher.exe and no AudioSwitcher.cs found next to this script."
    return $false
}

function Install-Task {
    Assert-Admin
    # Stop any running/old instance FIRST - otherwise the exe is locked (can't overwrite) and
    # the single-instance guard would make the freshly-installed one exit immediately.
    Stop-Daemon
    if (-not (Ensure-Exe)) { return }

    # Remove old task if it exists
    if (Test-Task) {
        Write-Host "Removing existing scheduled task..." -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }

    Write-Host "Creating scheduled task '$TaskName'..." -ForegroundColor Cyan

    # Create task: run at logon, elevated, hidden
    $action = New-ScheduledTaskAction -Execute $Exe
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1))

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null

    Write-Host "Starting daemon now..." -ForegroundColor Cyan
    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 2
    Write-Host ""
    Write-Host "  Installed. Auto-start is ON - it now launches at every logon." -ForegroundColor Green
    Write-Host "  You can close this window; the daemon keeps running." -ForegroundColor DarkGray
    Show-Status
}

function Uninstall-Task {
    Assert-Admin
    Write-Host "Stopping daemon..." -ForegroundColor Yellow
    Stop-Daemon
    if (Test-Task) {
        Write-Host "Removing scheduled task..." -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    if (Test-Path $Exe) {
        Write-Host "Deleting $Exe..." -ForegroundColor Yellow
        Remove-Item $Exe -Force
    }
    Write-Host "Done. State files in $env:LOCALAPPDATA\AudioSwitcher\ are preserved." -ForegroundColor Green
    Write-Host "Delete that folder manually if you want a clean slate." -ForegroundColor DarkGray
}

function Start-Daemon {
    if (-not (Ensure-Exe)) { return }
    if (Test-Task) {
        Start-ScheduledTask -TaskName $TaskName
    } else {
        Write-Host "Scheduled task not found - running directly. Install with -Install for auto-start." -ForegroundColor Yellow
        Start-Process -FilePath $Exe -WorkingDirectory $InstallDir
    }
    Start-Sleep -Seconds 2
    Show-Status
}

function Stop-Daemon {
    $procs = Get-Process -Name "AudioSwitcher" -ErrorAction SilentlyContinue
    if ($procs) {
        foreach ($p in $procs) {
            try { $p | Stop-Process -Force -ErrorAction Stop; $p.WaitForExit(4000) | Out-Null }
            catch { Write-Host "  Could not stop pid $($p.Id) (try running as Administrator)." -ForegroundColor Yellow }
        }
        Start-Sleep -Milliseconds 300   # let the exe file unlock before any overwrite
        Write-Host "Daemon stopped." -ForegroundColor Yellow
    } else {
        Write-Host "Daemon was not running." -ForegroundColor DarkGray
    }
}

function Show-Banner {
    $art = @'
   ##  #  # ###  ###  ##   ### #   # ### ###  ### #  # #### ###
  #  # #  # #  #  #  #  # #    #   #  #   #  #    #  # #    #  #
  #### #  # #  #  #  #  #  ##  # # #  #   #  #    #### ###  ###
  #  # #  # #  #  #  #  #    # ## ##  #   #  #    #  # #    # #
  #  #  ##  ###  ###  ##  ###  #   # ###  #   ### #  # #### #  #
'@
    Write-Host ""
    Write-Host $art -ForegroundColor Cyan
    Write-Host "  per-game audio format switcher        by @thetrueartist" -ForegroundColor DarkGray
    Write-Host ""
}

function Show-Status {
    Show-Banner
    if (-not (Test-Path $Exe)) {
        Write-Host "  Status     : " -NoNewline; Write-Host "not built" -ForegroundColor Yellow
        Write-Host "  Next step  : run  .\AudioSwitcher.ps1 -Install   (build + auto-start)" -ForegroundColor DarkGray
        Write-Host ""
        return
    }

    # Auto-start (scheduled task) state
    $autostart = Test-Task
    Write-Host "  Auto-start : " -NoNewline
    if ($autostart) { Write-Host "ON (runs at logon)" -ForegroundColor Green }
    else            { Write-Host "OFF" -ForegroundColor DarkGray -NoNewline; Write-Host "  (run -Install to enable)" -ForegroundColor DarkGray }

    # Running state
    $running = Get-Process -Name "AudioSwitcher" -ErrorAction SilentlyContinue
    Write-Host "  Daemon     : " -NoNewline
    if ($running) { Write-Host "running (pid $($running[0].Id))" -ForegroundColor Green }
    else          { Write-Host "stopped" -ForegroundColor Yellow }

    # Live state from the daemon (endpoint / current format / games)
    if ($running) {
        Write-Host ""
        & $Exe --status
    }
    Write-Host ""
}

# -- Main --------------------------------------------------------------------
switch ($PSCmdlet.ParameterSetName) {
    'Install'     { Install-Task }
    'Uninstall'   { Uninstall-Task }
    'Build'       { Build-Exe }
    'Start'       { Start-Daemon }
    'Stop'        { Stop-Daemon }
    'Status'      { Show-Status }
    'ListDevices' { if (Test-Path $Exe) { & $Exe --list-devices } else { Write-Error "Run -Install or -Build first" } }
    'ShowState'   { if (Test-Path $Exe) { & $Exe --show-state }   else { Write-Error "Run -Install or -Build first" } }
    'Reset'       { if (Test-Path $Exe) { & $Exe --reset }        else { Write-Error "Run -Install or -Build first" } }
    'TestETW'     { Assert-Admin; if (Test-Path $Exe) { & $Exe --test-etw } else { Write-Error "Run -Install or -Build first" } }
    'Lock'        {
        if (-not (Test-Path $Exe)) { Write-Error "Run -Install or -Build first"; exit 1 }
        if ($LockTier -lt 0) { Write-Error "Provide -LockTier <0..4>"; exit 1 }
        & $Exe --lock $Lock $LockTier
    }
    'Foreground'  {
        Assert-Admin
        if (-not (Ensure-Exe)) { return }
        $cmdArgs = @("--console")   # --console = live log in this window, not the tray
        if ($Verbose2) { $cmdArgs += "--verbose" }
        & $Exe @cmdArgs
    }
    default {
        # No switch given -> friendly interactive menu.
        Show-Status
        Write-Host "  What would you like to do?" -ForegroundColor White
        Write-Host ""
        Write-Host "   [1] Install + enable auto-start   (recommended)" -ForegroundColor Gray
        Write-Host "   [2] Show status" -ForegroundColor Gray
        Write-Host "   [3] Show learned state (overrides / crashes / glitches)" -ForegroundColor Gray
        Write-Host "   [4] List audio devices" -ForegroundColor Gray
        Write-Host "   [5] Run in this window (live log, Ctrl-C to stop)" -ForegroundColor Gray
        Write-Host "   [6] Stop the daemon" -ForegroundColor Gray
        Write-Host "   [7] Uninstall" -ForegroundColor Gray
        Write-Host "   [Q] Quit" -ForegroundColor Gray
        Write-Host ""
        $choice = Read-Host "  Choice"
        switch ($choice.Trim().ToUpper()) {
            '1' { if (-not (Invoke-Elevated '-Install'))   { Install-Task } }
            '2' { Show-Status }
            '3' { if (Test-Path $Exe) { & $Exe --show-state } else { Write-Host "  Build first (option 1)." -ForegroundColor Yellow } }
            '4' { if (Test-Path $Exe) { & $Exe --list-devices } else { Write-Host "  Build first (option 1)." -ForegroundColor Yellow } }
            '5' { if (-not (Invoke-Elevated '-Foreground')) { if (Ensure-Exe) { & $Exe --console } } }
            '6' { if (-not (Invoke-Elevated '-Stop'))      { Stop-Daemon } }
            '7' { if (-not (Invoke-Elevated '-Uninstall')) { Uninstall-Task } }
            'Q' { }
            ''  { }
            default { Write-Host "  Unknown choice '$choice'." -ForegroundColor Yellow }
        }
    }
}
