# Changelog

All notable changes to this project are documented here. Format loosely
follows [Keep a Changelog](https://keepachangelog.com/); versioning will
follow semantic-ish versioning once the first real release is tagged (see
`15-DEPLOYMENT.md` §4 for how agent/app versions are coordinated).

## [Unreleased]

### Added
- Full documentation set under `docs/` (this file and 00–17).
- Windows agent (`windows-agent/`): WebSocket server (`HttpListener`),
  6-digit pairing-code authentication with in-memory trust tokens, and
  Win32-based simulation of mouse move/click/scroll, keyboard key presses
  and text typing, media keys, and power actions (sleep/shutdown/restart/lock).
- Android app (`android-app/`): Pairing screen (manual IP + pairing-code
  entry), Touchpad screen (drag-to-move, tap-to-click, long-press-to-right-
  click, explicit Left/Right buttons), `RemoteConnection` WebSocket client
  matching the agent's JSON protocol, and local token persistence via
  `SharedPreferences`.
- Initial project architecture decisions: WebSocket + JSON protocol,
  client-server (agent-required) model, local-network-only scope for v1.
- mDNS discovery (`02-FEATURE-SPECIFICATION.md` F1.5): the Windows agent
  advertises `_pc-remote._tcp.local.` (Makaretu.Dns.Multicast, UDP 5353) and
  the Android app browses via `NsdManager`, listing discovered PCs on the
  Pairing screen; manual IP entry retained as a fallback
  (`07-API-SPECIFICATION.md` §7).
- Android control screens (F3–F5, F9): Keyboard (modifier-lock + special
  keys + debounced text), Media (transport + hold-to-repeat volume), Power
  (Sleep/Lock immediate, Restart/Shutdown confirmed via AlertDialog), and
  Settings (paired-PC list + Forget, touchpad sensitivity slider applied
  live, about). Post-pairing shell now has a bottom nav
  (Touchpad/Keyboard/Media/Power) and a dark-first custom theme
  (`04-UI-UX-SPECIFICATION.md` §2–3).
- Build/test infrastructure: full Gradle project for `android-app/`
  (settings/root/app modules, manifest, wrapper — previously source-only),
  xUnit test project for `windows-agent/` (`PcRemoteAgent.Tests`), and
  instrumented Compose tests for the Pairing and control screens.
- Security hardening pass (closes the TLS, code-expiry, token-persistence,
  and storage-encryption gaps in `09-SECURITY-PRIVACY.md`):
  WSS end-to-end via a hand-rolled TLS WebSocket server in the agent
  (self-signed cert, DPAPI-stored, **no `netsh urlacl`/admin needed**) with
  per-host TOFU certificate pinning in the app; 5-minute pairing-code
  expiry with auto-rotation; agent trust tokens persisted DPAPI-encrypted;
  Android tokens/cert-pins in `EncryptedSharedPreferences`.
- Reliability (`10-ERROR-HANDLING.md` §3, §5): automatic reconnect with
  exponential backoff (token-only re-auth, no prompt), a connection-status
  banner in the control shell, and a `disconnecting` protocol message so an
  expected shutdown/restart shows "PC is shutting down…" instead of
  reconnecting.
- App features: D-pad mode on the touchpad (TalkBack-accessible cursor
  alternative, `13-ACCESSIBILITY.md` §2), PC rename in Settings (`F9.1`),
  and a foreground session service with runtime notification permission.
- CI: GitHub Actions workflows for both codebases (`.github/workflows/`).
- Agent file logging: console output is mirrored to
  `%AppData%\PcRemoteAgent\logs\agent-<date>.log` (7-day retention) so an
  agent that auto-starts at login (Startup shortcut, no console visible)
  stays diagnosable (`14-OBSERVABILITY-LOGGING.md` §2).

### Known Gaps (tracked, not yet fixed — see linked docs)
- Agent "someone is connected" indicator is console-only; a persistent
  visible indicator needs the tray-app rework (`09-SECURITY-PRIVACY.md` §9.1).
- A changed server certificate or revoked token shows as a generic
  connection failure — no dedicated "re-pair required" message yet (`09` §9.2).
- No accessibility audit performed yet on shipped screens
  (`13-ACCESSIBILITY.md` §6).
- No CI/CD pipeline set up (`15-DEPLOYMENT.md` §5) — workflows added but
  never run against a real repository.

## [0.1.0] — Initial architecture & starter code

- First working end-to-end path: pair a phone with a PC and move the mouse
  cursor from the touchpad screen. Establishes the project's core
  architecture and protocol for all future feature work.
