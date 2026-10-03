# Builds PC-Remote-Setup.exe.
#  1. Publishes all components into installer/staging (framework-dependent,
#     win-x64). Switch to --self-contained true for runtime-free installs.
#  2. Signs staged executables when WINDOWS_CERT_PATH / WINDOWS_CERT_PASSWORD
#     are set (requirement 13).
#  3. Produces the SHA-256 sidecar listed in the release checksums.
#  4. Runs ISCC (Inno Setup) to produce dist/PC-Remote-Setup.exe.

param(
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $PSScriptRoot "staging"

$projects = @(
    "src/PcRemote.Service/PcRemote.Service.csproj",
    "src/PcRemote.Session/PcRemote.Session.csproj",
    "src/PcRemote.Tray/PcRemote.Tray.csproj"
)

$selfContainedFlag = if ($SelfContained) { "-p:SelfContained=true" } else { "-p:SelfContained=false" }

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item $staging -ItemType Directory | Out-Null

foreach ($proj in $projects) {
    $versionArgs = @()
    if ($Version) { $versionArgs += "-p:Version=$Version" }
    dotnet publish (Join-Path $repoRoot $proj) `
        -c $Configuration -r win-x64 $selfContainedFlag `
        -p:PublishDir=$staging -v q --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

# --- Signing (production) ---
if ($env:WINDOWS_CERT_PATH -and $env:WINDOWS_CERT_PASSWORD) {
    Write-Host "Signing staged executables…"
    Get-ChildItem $staging -Filter *.exe | ForEach-Object {
        & signtool.exe sign /f $env:WINDOWS_CERT_PATH /p $env:WINDOWS_CERT_PASSWORD `
            /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "signing failed: $($_.Name)" }
    }
} else {
    Write-Warning "No signing certificate configured (WINDOWS_CERT_PATH not set); output is unsigned; development only."
}

# --- SHA-256 sidecar for the updater (requirement 12) ---
Write-Host "Staging complete: $staging"

# --- Inno Setup ---
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isdl.php"
}
& $iscc (Join-Path $PSScriptRoot "PC-Remote-Setup.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $repoRoot "dist\PC-Remote-Setup.exe"
if (Test-Path $setup) {
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$setup.sha256" -Value "$hash  PC-Remote-Setup.exe" -NoNewline
    Write-Host "Built $setup"
    Write-Host "SHA-256: $hash"
}
