<#
.SYNOPSIS
  Opens one scripted demo take (docs/DEMO.md) in a clean Chrome window, ready to record.

.DESCRIPTION
  The page restarts the demo match from 38' and plays the take by itself. Chrome starts with a fresh profile (no
  extensions, no saved settings), full screen (kiosk), at 1920 x 1080 CSS pixels whatever the Windows display
  scaling, and with sound allowed to play without a click (the commentator). Alt + F4 closes it.

.EXAMPLE
  ./scripts/take.ps1 -Take 1
  ./scripts/take.ps1 -Take 2 -Windowed
  ./scripts/take.ps1 -Take 3 -Url http://localhost:5080 -Key my-operator-key
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet(1, 2, 3)][int]$Take,
    [string]$Url = "http://localhost:5080",
    # The operator key, when the server has MATCHLENS_ADMIN_KEY set (restarting the match needs it).
    [string]$Key = "",
    # A 1920 x 1080 window at the top left instead of full screen (for a monitor larger than 1080p).
    [switch]$Windowed
)

$ErrorActionPreference = "Stop"

$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $browser) { throw "Chrome or Edge was not found. Open $Url/judge?take=$Take yourself and press F11." }

try { Invoke-WebRequest "$Url/api/health" -UseBasicParsing -TimeoutSec 5 | Out-Null }
catch { throw "The server does not answer at $Url. Start it first (dotnet run --project src/MatchLens.Server)." }

$address = "$Url/judge?take=$Take"
if ($Key) { $address += "&key=$([uri]::EscapeDataString($Key))" }

# A fresh profile every time: no extensions, no remembered settings, and the flags below always apply.
$profileDir = Join-Path $env:TEMP ("sbiq-take-{0}-{1}" -f $Take, (Get-Date -Format "yyyyMMdd-HHmmss"))

$arguments = @(
    "--user-data-dir=$profileDir",
    "--no-first-run", "--no-default-browser-check", "--disable-features=Translate",
    "--autoplay-policy=no-user-gesture-required",
    "--force-device-scale-factor=1"
)
if ($Windowed) { $arguments += @("--window-position=0,0", "--window-size=1920,1080", "--app=$address") }
else { $arguments += @("--kiosk", $address) }

Write-Host "Take $Take`: $address"
Write-Host "The match restarts from 38' and the take plays itself. Alt + F4 closes the window."
Start-Process -FilePath $browser -ArgumentList $arguments
