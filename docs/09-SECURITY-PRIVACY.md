# Security & Privacy

## 1. Threat Model

**In scope threats:**
- An unauthorized device on the same LAN attempting to control the PC
  without pairing.
- A previously-trusted phone being lost/stolen and used to control the PC.
- Casual network sniffing on the same LAN (e.g., a compromised IoT device)
  capturing traffic between phone and PC.

**Explicitly out of scope for v1** (given the local-only architecture):
- A remote (internet-based) attacker — there is no exposed internet-facing
  service in v1; the agent only listens on the LAN interface.
- A fully compromised OS on either endpoint (if the phone or PC itself is
  already compromised by malware, this app's protocol-level protections
  don't meaningfully change that risk).

## 2. Transport Security

**Current state:** `wss://` (TLS) end-to-end. The agent generates a
self-signed certificate on first run (stored DPAPI-encrypted under
`%AppData%\PcRemoteAgent\`) and the app pins it per host on the first
successful handshake (trust-on-first-use, similar to SSH host keys). Later
connections to the same host must present the identical certificate — a
changed certificate fails the handshake and requires explicit re-pairing
from the app side. Plaintext `ws://` is not served.

**Known trade-off:** TOFU protects against passive sniffing and against
later MITM, but a MITM *during the very first pairing* would be trusted once
(and then pinned as the "real" host). This is the documented SSH-style
trade-off; the pairing code itself still comes from the agent's console, so
an attacker would need both to sit in the TLS path and trick the user into
reading *their* code.

## 3. Authentication & Pairing

- Pairing requires physical/visual access to the PC (reading the 6-digit
  code off its screen) — this is the primary defense against unauthorized
  pairing, since an attacker would need to be in the same room, not just on
  the same Wi-Fi.
- mDNS provides no authenticity of its own — any device on the LAN can
  advertise `_pc-remote._tcp.local.` (spoofing a fake agent, e.g. to harvest
  pairing attempts). Discovery only supplies candidate hosts; trust still
  comes from the 6-digit code read off the real PC's screen or from a
  previously-saved token. The app must never auto-pair with a discovered
  host: an unpaired discovered PC always requires an explicitly entered
  pairing code.
- Pairing codes expire after 5 minutes and the agent rotates them
  automatically, printing each new code to its console (`PairingStore`
  regenerates and revalidates against a timestamp; an expired code is
  rejected even if it looks right).
- Once paired, a long-lived trust token replaces the pairing code for future
  connections (`07-API-SPECIFICATION.md` §4.1–4.2). Trust tokens are
  persisted on the agent (DPAPI-encrypted file) so devices stay paired
  across agent restarts.
- Trust tokens function as a bearer credential: anyone who obtains a valid
  token can control the PC without re-pairing. Treat them with the same
  care as a password (see `06-DATA-MODEL.md` for storage locations).

## 4. Secure Storage

| Location | Current |
|---|---|
| Android: trust tokens + cert pins | `EncryptedSharedPreferences` (AES256-GCM values, AES256-SIV keys, androidx.security-crypto) |
| Windows: trust tokens | DPAPI-protected JSON file, current-user scope (`%AppData%\PcRemoteAgent\trusted-devices.json`) |
| Windows: server certificate | DPAPI-protected PFX, current-user scope (`%AppData%\PcRemoteAgent\server-cert.dat`) |

## 5. Data Privacy

- **No telemetry, no analytics, no cloud service** in v1 — all data (mouse
  movements, keystrokes typed, media commands) travels directly between the
  phone and the PC on the local network and nowhere else.
- **Clipboard sync (F6, planned)** is the most privacy-sensitive planned
  feature since clipboards commonly contain passwords, one-time codes, or
  personal data. It must be **opt-in, off by default, per-PC**, and clearly
  labeled in Settings — never silently enabled.
- **Keystrokes typed via the Keyboard screen** are transmitted as plaintext
  JSON over the (currently unencrypted) socket — this includes anything the
  user types, including passwords typed into a PC application. This is a
  strong argument for prioritizing TLS (§2) before this feature sees any
  real-world use beyond the developer's own trusted network.

## 6. Visible Control Indicator (Recommended)

To avoid the PC being controlled without the person at the keyboard
noticing, the agent should show a persistent visual indicator (tray icon
state change, or a small on-screen overlay) whenever a phone is actively
connected — not just during pairing, but for the duration of every session.
This is currently **not implemented** (the agent is a console app with no
persistent tray presence yet — see `05-TECHNICAL-ARCHITECTURE.md` §3).

## 7. Permission Justification (Android)

| Permission | Why needed |
|---|---|
| `INTERNET` | Required for the local-network WebSocket connection (Android treats LAN sockets as requiring this permission) |
| `ACCESS_NETWORK_STATE` | To detect Wi-Fi connectivity changes and drive reconnect logic |
| `CHANGE_WIFI_MULTICAST_STATE` | Normal (auto-granted) permission to hold a `WifiManager.MulticastLock` while the app browses mDNS (`07-API-SPECIFICATION.md` §7), so multicast replies from the agent are actually received during discovery |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_DATA_SYNC` | Lets the connection keepalive service run in the foreground (Android 14+ requires the typed declaration) while controlling the PC |
| `POST_NOTIFICATIONS` | Runtime permission (API 33+) for the "Controlling your PC" session notification shown by that service; requested only when a session becomes active |

No permissions beyond networking are required — no location, contacts,
storage, camera, or microphone access, reinforcing the minimal-footprint
positioning from `01-PRODUCT-REQUIREMENTS.md`.

## 8. Revocation

- Users should be able to revoke a paired device from the PC side (agent
  UI, planned) without needing access to that phone — important if a phone
  is lost.
- "Forget this PC" on the app side (`02-FEATURE-SPECIFICATION.md` F9) only
  removes local app state; it does not by itself revoke the token on the
  agent, since the app may not be reachable/trusted to make that request
  reliably. True revocation must be agent-initiated (deleting the entry from
  `trusted-devices.json`, `06-DATA-MODEL.md` §2.2).

## 9. Open Security Gaps (tracked)

1. The "someone is connected" indicator is console-only (a live connected-
   device count); a persistent tray/overlay indicator needs the agent to
   become a tray app (§6).
2. A changed server certificate (agent reinstall/new machine keeping the
   same IP) or a revoked token surfaces as a generic connection failure —
   the app does not yet distinguish "wrong cert, re-pair me" from
   "unreachable" in its message copy.
3. No hardening of the TOFU first-pairing window beyond the pairing-code
   requirement (§2) — accepted and documented trade-off.

These are not release blockers in the same class as the closed gaps above —
see `16-RELEASE-CHECKLIST.md`.
