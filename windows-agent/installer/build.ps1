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
    "src/PcRemote.Input/PcRemote.Input.csproj",
    "src/PcRemote.Tray/PcRemote.Tray.csproj"
)

$selfContainedFlag = if ($SelfContained) { "-p:SelfContained=true" } else { "-p:SelfContained=false" }

# signtool.exe ships with the Windows SDK and is not on PATH on either the
# GitHub runner or a plain PowerShell session — only in a developer prompt.
function Resolve-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidates = @()
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        # Newest SDK version first (10.0.26100.0 sorts above 10.0.22621.0).
        $candidates += Get-ChildItem $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" }
    }
    $candidates += @(
        (Join-Path $kitsRoot "x64\signtool.exe"),
        (Join-Path $env:ProgramFiles "Windows Kits\10\bin\x64\signtool.exe")
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) {
        throw "signtool.exe not found; install the Windows SDK (https://developer.microsoft.com/windows/downloads/windows-sdk/)"
    }
    return $found
}

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
    $signtool = Resolve-SignTool
    Write-Host "Signing staged executables…"
    Get-ChildItem $staging -Filter *.exe | ForEach-Object {
        & $signtool sign /f $env:WINDOWS_CERT_PATH /p $env:WINDOWS_CERT_PASSWORD `
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

# --- Sign the installer itself (production) ---
# Validate-Release.ps1 checks the Authenticode signature of the final setup
# EXE, so sign it after Inno Setup produces it (the .iss SignTool directive
# stays disabled; signing happens here where the cert env vars are known).
if ($env:WINDOWS_CERT_PATH -and $env:WINDOWS_CERT_PASSWORD -and (Test-Path $setup)) {
    $signtool = Resolve-SignTool
    Write-Host "Signing installer…"
    & $signtool sign /f $env:WINDOWS_CERT_PATH /p $env:WINDOWS_CERT_PASSWORD `
        /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $setup
    if ($LASTEXITCODE -ne 0) { throw "signing installer failed" }
}

if (Test-Path $setup) {
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$setup.sha256" -Value "$hash  PC-Remote-Setup.exe" -NoNewline
    Write-Host "Built $setup"
    Write-Host "SHA-256: $hash"
}
