# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
