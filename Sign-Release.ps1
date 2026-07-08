<#
.SYNOPSIS
  Sign a PUBLISHED AudioSwitcher release so the in-app updater will trust it.

  Your PRIVATE signing key stays on this machine - it is never uploaded. This downloads
  the CI-built release zip, signs it locally, and attaches manifest.json + manifest.sig to
  the release. Run it once after each release you want delivered via auto-update.

.EXAMPLE
  .\Sign-Release.ps1 v1.7.2
  .\Sign-Release.ps1 v1.7.2 -Key "D:\keys\AudioSwitcher-signing.key"
#>
param(
    [Parameter(Mandatory)] [string]$Tag,
    [string]$Key   = "$HOME\Documents\AudioSwitcher-signing.key",
    [string]$Repo  = "thetrueartist/Audio-Switcher",
    [string]$Asset = "AudioSwitcher-win-x64.zip"
)
$ErrorActionPreference = "Stop"

$exe = "$env:ProgramFiles\AudioSwitcher\AudioSwitcher.exe"
if (-not (Test-Path $exe)) { throw "AudioSwitcher.exe not found at $exe - install a build first." }
if (-not (Test-Path $Key)) { throw "Signing key not found: $Key  (generate it with --gen-signing-key)" }

$work = Join-Path $env:TEMP "AudioSwitcher-sign-$Tag"
New-Item -ItemType Directory -Force $work | Out-Null
Set-Location $work

Write-Host "Downloading $Asset for $Tag..." -ForegroundColor Cyan
$zip = Join-Path $work $Asset
Invoke-WebRequest "https://github.com/$Repo/releases/download/$Tag/$Asset" -OutFile $zip

Write-Host "Signing locally (private key stays on this machine)..." -ForegroundColor Cyan
# -Wait: the exe is GUI-subsystem, so PowerShell won't block on '&' - Start-Process -Wait does.
Start-Process -FilePath $exe -ArgumentList @("--sign-release", "`"$zip`"", "`"$Key`"", $Tag) -Wait -NoNewWindow
if (-not (Test-Path (Join-Path $work "manifest.json"))) { throw "Signing did not produce manifest.json" }

if (Get-Command gh -ErrorAction SilentlyContinue) {
    Write-Host "Uploading manifest.json + manifest.sig via gh..." -ForegroundColor Cyan
    gh release upload $Tag "$work\manifest.json" "$work\manifest.sig" --repo $Repo --clobber
    Write-Host "Done - $Tag is now signed and trusted by the updater." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "gh CLI not found. Attach these two files to the $Tag release (drag onto the release, Edit):" -ForegroundColor Yellow
    Write-Host "  $work\manifest.json"
    Write-Host "  $work\manifest.sig"
    Start-Process "https://github.com/$Repo/releases/edit/$Tag"
}
