# Deploys freshly published binaries from windows-agent\deploy\staging to the
# installed location (C:\Program Files\PC Remote\) and restarts the service.
# Run elevated (one UAC prompt). The phone disconnects for ~5 seconds during
# the service restart and reconnects automatically.

$ErrorActionPreference = "Stop"
Start-Transcript -Path "D:\Remote\windows-agent\deploy\deploy.log" -Force

$staging = Join-Path $PSScriptRoot "staging"
$target  = "C:\Program Files\PC Remote"

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
