# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.1] - 2026-10-03

### Removed (simplification pass)
The app is now the smallest thing that does the job. Removed machinery that was
never used, never requested, or could never run:

- **In-app updater (Windows):** `UpdateCoordinator`, the semver `UpdateVersion`
  parser, `AuthenticodeVerifier` (`WinVerifyTrust`), the `update_check` /
  `update_apply` IPC pair and the tray's update dialogs. Update by running the
  new `PC-Remote-Setup.exe` over the old install.
- **In-app updater (Android):** `UpdateChecker.kt` and its Settings UI.
- **UIAccess helper (`PcRemote.Session.UIA`):** Windows refuses to *launch* an
  unsigned `uiAccess` binary, so this second copy of the injector could never
  run — it was only a fourth binary to build, sign and ship. Removed from the
  solution, installer, build script and release workflow. **Consequence:**
  elevated windows are not reachable, which was already true in practice.
- **Tray GUI window (`AgentForm.cs`, 316 lines):** it duplicated every field the
  tray menu already shows, and its pairing-code label was always empty because
  the service only discloses the code to elevated callers. The context menu is
  now the whole UI; "Revoke all paired devices" moved into it so nothing was lost.
- **Reserved screen-streaming protocol:** `StreamOffer`, the `stream` message
  field and the `stream_request` command that only ever answered `not_ready`.
- **Dead code:** unused `IpcMessage.Command`/`Version` fields, the no-op
  `InputRouter.ReleaseAllButtons()`, a duplicate of the secure-desktop attach.

### Fixed
- **Lock screen / UAC / logon input did nothing at all.** The secure helper
  opened the input desktop with `DESKTOP_READOBJECTS` only; `SetThreadDesktop`
  rejects a handle without `DESKTOP_SWITCHDESKTOP` (ERROR_ACCESS_DENIED), so
  every relayed command was refused and the phone saw `input_unavailable`.
  Desktop access now lives in one place (`PcRemote.Core/Session/SecureDesktop`)
  with the correct access mask, a process-lifetime desktop handle (the old code
  leaked one handle per relayed `mouse_move`) and real Win32 error logging.
- **Secure helper was never restarted.** `_secureHelper ??=` only replaced a
  null reference, so once the helper died — or was never started because no user
  was logged on yet — lock-screen input stayed dead until the service restarted.
  It is now re-launched on liveness, every state, on the 500 ms tick.
- **Session id mismatch on the logon screen.** The helper derived its IPC pipe
  name from `WTSGetActiveConsoleSessionId`, which returns `0xFFFFFFFF` when
  nobody is attached, so service and helper disagreed on the pipe name and every
  relay timed out. The helper now uses its own session id (`ProcessIdToSessionId`)
  and the service falls back to the active/physical console session.
- **`IsAlive` could kill session detection.** `Process.HasExited` throws on a
  disposed process; that exception escaped `RefreshState` and terminated the WTS
  STA thread, ending lock/unlock detection for the rest of the boot.
- **Release-on-disconnect was a silent no-op.** `InputRouter.ReleaseAllButtons()`
  did nothing, so a client dropping mid-drag could leave a mouse button held
  down. It now relays a real `release_all` command to whichever helper is alive.

### Changed
- `deploy\deploy-update.ps1` no longer hardcodes a `D:\Remote\...` transcript
  path and self-elevates instead of failing unelevated.

---

## [0.2.0] - 2026-10-02

### Architecture (breaking, Windows agent)
- **Windows Service architecture**: The tray/user process no longer owns the WSS server, pairing state or input engine. A new `PCRemoteService` (LocalSystem, auto-start, SCM recovery restart) owns network communication, authentication/pairing, session detection and privileged operations — reachable before logon, after logoff and across tray crashes.
- **Session & privilege separation**: New `PCRemoteSession.exe` helper (user token) handles normal desktop input; `PCRemoteSession.exe --secure-input` (SYSTEM token in the console session) handles the Winlogon desktop; an optional signed `PCRemoteSession.UIA.exe` (`uiAccess=true`, Program Files) reaches elevated windows. Clear privilege boundaries — the service never injects input into session 0, the helpers never touch the network.
- **Ctrl+Alt+Del**: implemented via the documented `SendSAS` API instead of simulated keys.
- **Authenticated local IPC**: tray and session helpers talk to the service over `\.\pipe\PCRemoteCtl` with per-caller privilege checks (elevated-only for revoke/update/secure input). No unrestricted local TCP.
- **Project split**: `PcRemote.Core` / `PcRemote.Service` / `PcRemote.Session` / `PcRemote.Session.UIA` / `PcRemote.Tray` / `PcRemote.Tests`.

### Removed
- **Self-install architecture**: the EXE no longer copies itself to `%LocalAppData%`; replaced by a real Inno Setup installer (`PC-Remote-Setup.exe`) that installs to `C:\Program Files\PC Remote\`, registers the service, configures recovery, creates private-profile/localsubnet firewall rules, and removes legacy installs. The old `profile=any` firewall rule is replaced.

### Security
- **Updater**: fixed the wrong GitHub API endpoint (`api.github.org` -> `api.github.com`, Windows + Android). The service updater now verifies SHA-256 (release sidecar) and the Authenticode signature via `WinVerifyTrust` before executing any update artifact, stages it, and hands off to the signed installer — it never blindly runs a downloaded EXE.
- **Stable identity**: the TLS certificate is generated once and never regenerated on IP changes; a stable `pcId` GUID is reported in `auth_ok`/mDNS so DHCP changes no longer force re-pairing.
- **Token hardening**: pairing tokens are now 256-bit CSPRNG values (was GUID), stored DPAPI-protected under `ProgramData\PCRemote` owned by the service.
- **Command allowlist** enforced in the service and re-validated inside session helpers; the service rejects anything outside the fixed command set.

### Known limitations (honest status)
- Screen streaming / real remote-desktop view (Phases 5-6) is not implemented yet; `stream_request` is protocol-reserved. The connected experience is still touchpad/keyboard/media/power.
- Lock-screen / UAC / elevated-window paths are implemented but **not yet validated on a VM** — see `windows-agent/TEST-MATRIX.md` before claiming production support.

---


## [0.1.8] - 2026-09-20

### Added & Enhanced
- **Windows Agent GUI Control Panel**: Added a modern dark-themed desktop window displaying live server status, large 6-digit pairing code with quick copy/regenerate buttons, connected device count, local IP address list, and startup toggles.
- **Self-Installation & Boot Auto-Start**: Windows Agent automatically installs to `%LocalAppData%\PCRemote\PcRemoteAgent.exe` on first launch and configures HKCU auto-start (`--minimized`) to run silently on boot, surviving deletion of original installer executables.
- **Tray & Window Management**: Added minimize-to-tray on close `(X)`, double-click tray icon to restore GUI, and "Unpair All Devices" security action.

---

## [0.1.7] - 2026-09-19

### Security & Stability
- **EncryptedSharedPreferences Auto-Recovery**: Added automatic KeyStore corruption recovery and safe fallback on Android to prevent app crashes on open.
- **Android 14+ Foreground Service Safety**: Added exception safety around `startForegroundService` and `startForeground` to prevent `ForegroundServiceStartNotAllowedException` crashes.
- **WinForms UI Thread Safety**: Enforced handle creation and `BeginInvoke` dispatching on Windows Agent tray context menu to prevent cross-thread UI exceptions.

### Fixed & Enhanced
- **UIPI Failure Feedback**: Propagated Win32 `SendInput` return status to report `uipi_blocked` error codes to Android when targeted foreground windows are elevated.
- **Manual Connection Port Input**: Added explicit port number text field to Android manual IP connection UI.
- **Legacy Code Cleanup**: Removed obsolete legacy service cleanup logic from `FirewallHelper.cs`.

---

## [0.1.6] - 2026-09-19

### Security
- **TOFU Certificate Pin Hardening**: Removed automatic pin clearing on TLS handshake errors in Android client to prevent MITM certificate substitution attacks.
- **Notification Pairing Code Security**: Removed balloon notification pairing code popup on Windows agent to prevent pairing code exposure in Windows Notification Center history.

### Fixed
- **Android 14+ Foreground Service**: Added `FOREGROUND_SERVICE_DATA_SYNC` type to `ConnectionForegroundService` to fix crashes on Android 14 (API 34+).
- **Background Sleep Disconnects**: Added `WakeLock` and `WifiLock` acquisition during active Android connections to prevent Doze mode socket drops.
- **Virtual Network Interface Filtering**: Excluded virtual network adapters (`WSL`, `vEthernet`, `VMware`, `VirtualBox`) from Windows agent IP enumeration and mDNS broadcasts.
- **WebSocket Frame Concurrency**: Added `SemaphoreSlim` lock around Windows agent `SendFrameAsync` to prevent `SslStream` framing corruption during concurrent writes.
- **Single-File Startup Path**: Updated Windows agent startup registry key and tray icon loader to use `Environment.ProcessPath`.

---

## [0.1.5] - 2026-09-15

### Added
- **Automatic Firewall Provisioning**: Windows Agent automatically provisions inbound Windows Firewall rules for WSS (`58642/TCP`) and mDNS (`5353/UDP`) on startup, eliminating manual command-line setup.
- **Exclusive Address Socket Binding**: Added `ExclusiveAddressUse = true` to Windows Agent's `TcpListener` to prevent socket hijacking by legacy or conflicting background processes.
- **Automated Stale Pin & Cert Recovery**: Android app automatically resets invalid TLS certificate pins and triggers TOFU (Trust-On-First-Use) re-pinning on certificate rotation without requiring manual user intervention in Settings.
- **Legacy Background Service Removal**: Added automatic detection and termination of legacy `PCRemoteService` instances on Windows Agent launch to prevent port collision on `58642`.

### Fixed
- Fixed duplicate assembly attribute compilation error (CS0579) in `windows-agent` caused by stale `obj` build artifacts.
- Fixed IPv4 connection failures on port `58642` caused by legacy background service binding priority.

---

## [0.1.4] - 2026-09-14
- Reverted tray theme, restored startup checkbox, and synced app icon with Android.
- Final release verification security fixes.

---

## [0.1.3] - 2026-09-14
- Dynamic version name rendering in Android settings screen.
- Matched Windows tray context menu to Obsidian/Cyan dark UI palette.

---

## [0.1.2] - 2026-09-14
- Enhanced mDNS auto-detection serviceType matching and serial resolution.
- Updated documentation with v0.1.2 protocol specs.
