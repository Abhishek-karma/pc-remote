# Product Requirements

## 1. Problem Statement

People frequently want to control their PC from across the room — pausing a
video, nudging the cursor to click "skip intro," typing a search query into a
browser on a media PC, or putting the machine to sleep — without walking over
to a physical keyboard/mouse. Existing solutions (Unified Remote, Remote
Mouse) are broad but often bloated, ad-supported, or slow to pair. This
project builds a focused, fast, secure alternative for Windows PCs.

## 2. Target Users

- **Primary:** Home users with a PC connected to a TV (HTPC/media center use
  case) who want couch-distance control.
- **Secondary:** Desk users who want a quick way to control presentation
  slides, media playback, or lock/sleep their PC from their phone without
  reaching for the keyboard.

Both personas are on the **same home Wi-Fi network** as the PC during use —
this is the primary supported scenario for v1.

## 3. Goals (v1)

1. Pair an Android phone with a Windows PC in under 30 seconds.
2. Provide reliable, low-latency (<100ms perceived) mouse and keyboard
   control over the local network.
3. Provide one-tap media control (play/pause, volume, track skip).
4. Provide one-tap power control (sleep, lock, shutdown, restart) with
   confirmation for destructive actions.
5. Keep the connection secure: no PC should be controllable without explicit
   pairing approval.

## 4. Non-Goals (v1)

- Controlling the PC over the internet (outside the LAN) — deferred, see
  `05-TECHNICAL-ARCHITECTURE.md` §2.1 for the relay-server design needed later.
- macOS or Linux agents — Windows only for v1.
- Full remote-desktop screen mirroring/streaming — deferred to a later phase.
- File management/transfer — deferred to a later phase.
- Multi-user accounts, cloud sync of settings, or any server-side user
  database — this is a peer-to-peer LAN tool, not a hosted service.

## 5. Success Metrics

Since this starts as a personal/small-scale project rather than a commercial
product, success is measured qualitatively and via basic usage signals once
released:

| Metric | Target |
|---|---|
| Time to first successful pairing (new user) | < 30 seconds |
| Mouse-move perceived latency | < 100 ms on a typical home Wi-Fi network |
| Crash-free session rate (Android app) | > 99% |
| Agent uptime during active session | No unhandled crashes during a normal control session |
| Reconnect success rate (same network, previously paired) | > 95% without re-entering pairing code |

## 6. Constraints & Assumptions

- **Network:** Assumes both devices are on the same LAN/Wi-Fi subnet with
  multicast/broadcast not blocked by AP client isolation. If client
  isolation is on (common on guest networks), discovery and manual-IP
  connection may still work but should be documented as a known limitation.
- **Windows version:** Windows 10 (2004+) and Windows 11. Older Windows
  versions are out of scope.
- **Android version:** Android 8.0 (API 26)+ to keep `NsdManager` and modern
  Compose/coroutines support available without excessive backporting.
- **No app store backend required** for v1 — the Android app is a pure
  client with no server-side component of its own (the "server" is the
  user's own PC).

## 7. Key Risks

| Risk | Mitigation |
|---|---|
| Windows Firewall/UAC blocks the agent by default | Document setup steps clearly (`17-TROUBLESHOOTING.md`); consider an installer that requests the firewall exception automatically |
| Users pair once then can't find the PC again after IP changes (DHCP) | Prioritize mDNS discovery in the roadmap; until then, store hostname alongside IP and support manual re-entry easily |
| Perceived insecurity ("an app is moving my mouse") | Visible on-PC indicator when a phone is connected/controlling (see `09-SECURITY-PRIVACY.md`) |
| Battery/connection drop while backgrounded | Foreground service + clear "disconnected" state in UI, documented in `10-ERROR-HANDLING.md` |

## 8. Out-of-Scope Feature Requests (tracked for later consideration)

- Voice control ("Hey PC, pause")
- Custom macro/shortcut builder
- Multiple simultaneous PCs with quick-switch UI
- Widget/quick-settings-tile shortcuts on Android
