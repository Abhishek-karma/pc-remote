# Releasing PC Remote

Releases are produced entirely by CI when you push a version tag. This document
covers the release flow and the signing secrets the release workflow requires.

## Cutting a release

1. Make sure `main` is green and the version is bumped (`CHANGELOG.md`, the
   project/`versionName` fields, and the installer). The tag **is** the version:
   `v0.2.3` → `0.2.3`.
2. Tag and push:

   ```bash
   git tag v0.2.3
   git push origin v0.2.3
   ```

3. The **release** workflow (`.github/workflows/release.yml`) runs on the tag.
   It builds and validates **both** platforms, then the `github-release` job
   publishes the artifacts (installer + APK + `SHA256SUMS.txt`) to the GitHub
   Release. `github-release` has `needs: [windows, android]`, so **both** platform
   jobs must pass or nothing is published.

> A tag build is a **production release**. Both platform jobs refuse to ship
> unsigned binaries: Windows throws if the signing certificate is missing, and
> Android exits non-zero if the keystore secret is missing. Development builds
> (`windows-agent-ci`, `android-app-ci`) build unsigned and are unaffected.

## Signing secrets

Both platforms sign with self-generated material. This is intentional and
sufficient for this project: it guarantees integrity and a stable publisher /
signing identity across updates, not third-party trust.

Create these as **repository secrets** under
*Settings → Secrets and variables → Actions → New repository secret*.

### Windows (Authenticode)

| Secret | Value |
| --- | --- |
| `WINDOWS_CERT_PFX_BASE64` | Base64 of the code-signing `.pfx` |
| `WINDOWS_CERT_PASSWORD` | Password for that `.pfx` |

The certificate's subject **must** contain `CN=PC Remote` — `Validate-Release.ps1`
rejects a publisher mismatch. Generate and export it with:

```powershell
# Create a self-signed code-signing certificate (CurrentUser\My store)
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=PC Remote" `
  -KeyUsage DigitalSignature -FriendlyName "PC Remote Code Signing" `
  -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) -HashAlgorithm SHA256

# Export to a password-protected PFX
$pwd = ConvertTo-SecureString "CHOOSE_A_STRONG_PASSWORD" -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath .\pc-remote-signing.pfx -Password $pwd

# Print the value for the WINDOWS_CERT_PFX_BASE64 secret
[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path .\pc-remote-signing.pfx)))
```

Keep the `.pfx` and its password backed up. **Rotating to a new certificate
changes the publisher**, which breaks the continuity `Validate-Release.ps1`
enforces.

How the workflow uses it: the *Provision signing certificate* step decodes the
base64 to a `.pfx` on the runner and **imports it into `Cert:\LocalMachine\Root`**
so `Get-AuthenticodeSignature` reports `Valid` for the self-signed root. It then
signs the staged executables and the final `PC-Remote-Setup.exe`. `Validate-Release.ps1`
re-checks signature, publisher and version before the release job publishes.

> The `.pfx` password must be identical to `WINDOWS_CERT_PASSWORD`, and the
> certificate must be unexpired, or the signing / validation steps fail.

### Android (APK signing)

| Secret | Value |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Base64 of the signing `.keystore` / `.jks` |
| `ANDROID_KEYSTORE_PASSWORD` | Store password |
| `ANDROID_KEY_ALIAS` | Key alias |
| `ANDROID_KEY_PASSWORD` | Key password |

Generate a keystore with `keytool` (ships with the JDK / Android Studio):

```bash
# macOS/Linux
keytool -genkeypair -v -keystore release.keystore \
  -alias pc-remote -keyalg RSA -keysize 2048 -validity 10000 -storetype PKCS12
base64 -i release.keystore            # value for ANDROID_KEYSTORE_BASE64
```

```powershell
# Windows
keytool -genkeypair -v -keystore release.keystore `
  -alias pc-remote -keyalg RSA -keysize 2048 -validity 10000 -storetype PKCS12
[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path .\release.keystore)))
```

Notes:
- **Use `-storetype PKCS12`.** With PKCS12 the key password must equal the store
  password, so set `ANDROID_KEYSTORE_PASSWORD` and `ANDROID_KEY_PASSWORD` to the
  same value.
- The store password must be at least 6 characters.
- **Back up the keystore and its passwords.** Losing them means you can never
  publish an update that upgrades an installed app (the signing identity
  changes). The same keystore must be reused for the life of the app.

`app/build.gradle.kts` applies this `release` signing config automatically when
`ANDROID_KEYSTORE_FILE` is set, producing the signed
`PC-Remote-Android-release.apk` that the workflow stages.

## Verifying a release

After publishing, users can verify the installer digest against
`SHA256SUMS.txt`:

```powershell
# PowerShell
(Get-FileHash .\PC-Remote-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
```

```bash
# Linux/macOS
sha256sum -c SHA256SUMS.txt --ignore-missing
```

## Re-releasing the same version

If a run fails after the tag was pushed, you cannot simply re-run it with the
same tag if the release already exists. Either delete the tag and re-push:

```bash
git tag -d v0.2.3 && git push origin :refs/tags/v0.2.3
git tag v0.2.3 && git push origin v0.2.3
```

or cut the next patch tag (`v0.2.4`).
