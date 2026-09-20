# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
