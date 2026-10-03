# Deploys freshly published binaries from windows-agent\installer\staging (the
# folder ./installer/build.ps1 publishes to) to the installed location
# (C:\Program Files\PC Remote\) and restarts the service.
# Run elevated (one UAC prompt) - this script re-launches itself elevated if
# needed. The phone disconnects for ~5 seconds during the service restart and
# reconnects automatically.

$ErrorActionPreference = "Stop"

# Self-elevate: stopping the service and writing to Program Files both need it.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Requesting elevation…"
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`""
    )
    exit
}

Start-Transcript -Path (Join-Path $PSScriptRoot "deploy.log") -Force

# installer/staging is where build.ps1 publishes; deploy/staging is kept as a
# copy so this script keeps working from either location.
$staging = Join-Path $PSScriptRoot "staging"
if (-not (Test-Path (Join-Path $staging "PCRemoteService.exe"))) {
    $staging = Join-Path (Split-Path -Parent $PSScriptRoot) "installer\staging"
}
if (-not (Test-Path (Join-Path $staging "PCRemoteService.exe"))) {
    throw "No published binaries found. Run .\installer\build.ps1 (or publish to installer\staging) first."
}
$target  = "C:\Program Files\PC Remote"

Write-Host "Staging: $staging"

Write-Host "Stopping PCRemoteService…"
Stop-Service PCRemoteService -Force -ErrorAction Stop
# wait until fully stopped
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 500
    if ((Get-Service PCRemoteService).Status -eq "Stopped") { break }
}
Write-Host "Service status after stop: $((Get-Service PCRemoteService).Status)"

Get-Process PCRemoteTray, PCRemoteSession -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "Copying binaries to $target…"
Copy-Item "$staging\*" $target -Force -Recurse
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "copy failed" }

Write-Host "Starting PCRemoteService…"
Start-Service PCRemoteService
Start-Sleep -Seconds 3
$svc = Get-Service PCRemoteService
Write-Host "Service state: $($svc.Status)"

Write-Host "Restarting tray…"
Start-Process "$target\PCRemoteTray.exe" -ArgumentList "--minimized"

Write-Host "Done. The tray window should show a pairing code within a few seconds."
Stop-Transcript
