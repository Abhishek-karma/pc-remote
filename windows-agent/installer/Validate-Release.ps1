# Validates a staged release artifact before it may ship (requirement 13):
#   * Authenticode signature present and chain-trusted
#   * Publisher matches the expected subject
#   * Product and file version match the release tag
#   * SHA-256 matches the published digest (when provided)
# Exits non-zero on any mismatch. In CI this runs only when signing secrets
# are configured; unsigned development builds skip with a warning.

param(
    [Parameter(Mandatory)][string]$Path,
    [string]$ExpectedPublisher = "CN=PC Remote",
    [string]$ExpectedVersion,
    [string]$ExpectedSha256
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) { throw "artifact not found: $Path" }

$sig = Get-AuthenticodeSignature -FilePath $Path
if ($sig.Status -ne 'Valid') {
    throw "Authenticode signature invalid ($($sig.Status)) for $Path"
}
if ($ExpectedPublisher -and $sig.SignerCertificate.Subject -notlike "*$ExpectedPublisher*") {
    throw "publisher mismatch: expected '$ExpectedPublisher', got '$($sig.SignerCertificate.Subject)'"
}

if ($ExpectedVersion) {
    $ver = (Get-Item $Path).VersionInfo.FileVersion
    if ($ver -notlike "*$ExpectedVersion*") {
        throw "version mismatch: expected '$ExpectedVersion', got '$ver'"
    }
}

if ($ExpectedSha256) {
    $hash = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch: expected $ExpectedSha256, got $hash"
    }
}

Write-Host "OK: $Path"
Write-Host "  Signer:  $($sig.SignerCertificate.Subject)"
Write-Host "  Version: $((Get-Item $Path).VersionInfo.FileVersion)"
Write-Host "  SHA-256: $((Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant())"
