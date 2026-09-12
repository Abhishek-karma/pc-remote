# Technical Architecture

## 1. System Overview

Two independent programs communicate over a local-network WebSocket
connection:

```
┌─────────────────────┐         ┌──────────────────────┐
│   Android App        │         │   Windows Agent       │
│  (Kotlin/Compose)     │ <-----> │  (C# / .NET 8)         │
│                       │  LAN    │                       │
│  - Compose UI          │  ws://  │  - HttpListener +      │
│  - RemoteConnection     │  :58642 │    WebSocket server   │
│    (OkHttp WS client)  │         │  - Win32 SendInput     │
│  - TokenStore (prefs)  │         │  - Pairing/token store │
└─────────────────────┘         └──────────────────────┘
```

There is no third-party backend, cloud service, or database. The "server"
role is played entirely by the user's own PC.

## 2. Network Model

### 2.1 v1: Local network only

Both devices must be on the same Wi-Fi/LAN subnet. The agent listens on TCP
port `58642` (no admin rights required) and serves WebSocket upgrades over
TLS at `wss://<pc-ip>:58642/`, presenting a self-signed certificate that the
app pins per host (trust-on-first-use, `09-SECURITY-PRIVACY.md` §2). The
agent advertises itself via mDNS (`_pc-remote._tcp.local.`), so the app can
discover it automatically; manual IP entry (reading the IP off the agent
console) remains the fallback when multicast is blocked.

### 2.2 Future: Remote access

To control the PC from outside the LAN, a relay is required since home PCs
sit behind NAT. Two options, not yet decided:

- **Cloud relay:** a small always-on server (could be self-hosted) that both
  the agent and app connect outbound to (avoiding inbound port-forwarding),
  forwarding messages between them. Simpler to implement, but adds a
  third-party trust dependency and hosting cost/maintenance.
- **WebRTC data channel:** agent and app negotiate a direct peer connection
  via a STUN/TURN server for NAT traversal. More complex to implement
  correctly but keeps data off a relay server's path in the common case.

This is deliberately deferred — see `01-PRODUCT-REQUIREMENTS.md` §4
(non-goals).

## 3. Windows Agent Architecture

**Stack:** C#, .NET 8, single console-app process (see `windows-agent/`).

Components (`Program.cs`):

| Component | Responsibility |
|---|---|
| `Program.Main` | Starts `TcpListener`, loads/generates the TLS certificate, prints pairing code/IPs, rotates the code every 5 min, accepts loop |
| `HandleConnectionAsync` | Per-connection loop: TLS + WS handshake, auth gate, dispatch, connected-device counter |
| `WebSocketConnection` | Minimal RFC 6455 server over a stream (handshake, masked-frame reads, sends) — replaces `HttpListener`, removing the `netsh urlacl`/admin requirement |
| `CertificateManager` | Generates the self-signed server cert on first run; persists it DPAPI-encrypted |
| `PairingStore` | Pairing-code expiry (5 min) and rotation, auth, trust tokens persisted DPAPI-encrypted |
| `RemoteMessage` | JSON (de)serialization model shared conceptually with the Android side |
| `MdnsAdvertiser` | Advertises `_pc-remote._tcp.` (mDNS via `Makaretu.Dns.Multicast`) so the app can discover this PC — see `07-API-SPECIFICATION.md` §7 |
| `Win32Input` | P/Invoke wrappers around `SendInput`/`keybd_event` for mouse/keyboard/media simulation |
| `SystemPower` | P/Invoke + `Process.Start("shutdown", ...)` for power actions |

**Threading model:** Each accepted connection runs on its own `Task` via
`Task.Run`; TLS negotiation and the auth handshake run on that task, and the
input-simulation calls themselves are synchronous, fast Win32 calls that do
not need their own thread pool tuning at this scale (single-digit concurrent
phone connections expected).

**Planned evolution:** move from a console app to a WinForms/WPF tray app
(`NotifyIcon`) so it can run invisibly in the background, show a
connected-device indicator, and expose a small settings UI (see
`04-UI-UX-SPECIFICATION.md` is app-side only; the agent will get its own
minimal tray UI documented separately when built).

## 4. Android App Architecture

**Stack:** Kotlin, Jetpack Compose, OkHttp (WebSocket client),
kotlinx.serialization.

```
android-app/app/src/main/java/com/example/pcremote/
├── MainActivity.kt              (pairing ↔ ControlHub shell, status banner,
│                                  foreground-service lifecycle)
├── network/
│   ├── RemoteConnection.kt      (wss client, TOFU cert pinning, reconnect,
│                                  command API; TokenStore, PinStore,
│                                  SettingsStore, EncryptedPrefs)
│   └── DiscoveryService.kt      (NsdManager wrapper: mDNS browse for PCs,
│                                  faked NsdGateway for unit tests)
├── service/
│   └── ConnectionForegroundService.kt  (keeps the session alive when
│                                  backgrounded)
└── ui/
    ├── PairingScreen.kt         (discovery list + manual IP/code fallback)
    ├── TouchpadScreen.kt        (gestures + D-pad accessible mode)
    ├── KeyboardScreen.kt / MediaScreen.kt / PowerScreen.kt / SettingsScreen.kt
    └── theme/Theme.kt           (dark-first palette)
```

**Planned additions** (per `02-FEATURE-SPECIFICATION.md`):
- Navigation: replace `ControlHub`'s remembered-tab state with a proper
  nav graph (Compose Navigation) once there are 4+ screens plus Settings

**State management:** `RemoteConnection.state` is a `StateFlow<ConnectionState>`
that UI screens collect directly; no additional ViewModel layer exists yet
at this scale, but one should be introduced (`ConnectionViewModel`) once
screen count grows, to avoid passing a raw `RemoteConnection` instance
through every composable.

## 5. Communication Protocol

Full message reference lives in `07-API-SPECIFICATION.md`. Summary:

- **Transport:** WebSocket (`ws://`) over TCP, port 58642.
- **Framing:** One JSON object per WebSocket text message.
- **Auth:** first message on any connection must be `type: "auth"`; no
  other message type is processed until `auth_ok` is returned.
- **Direction:** Mostly app → agent (commands). Agent → app messages are
  currently limited to `auth_ok`/`auth_failed`; this will expand once
  file-list responses and screen-mirroring frames are added.

## 6. Key Design Decisions & Rationale

### 6.1 WebSocket over raw TCP/UDP

Chosen for message framing (no manual length-prefixing needed) and library
support on both platforms (OkHttp on Android, built-in on .NET). UDP was
considered for mouse-move deltas specifically (lower overhead, drops are
tolerable for a live cursor) but deferred as a v2 optimization — see
`12-PERFORMANCE.md`.

### 6.2 JSON over binary protocol

JSON was chosen for v1 for debuggability and development speed. This is
expected to become a bottleneck only for screen-mirroring frames, which will
likely use raw binary WebSocket messages (base64-free) rather than JSON-
wrapped image data when that feature is built.

### 6.3 Client-server (agent required) over agentless control

Controlling a PC with zero installed software (e.g., via RDP/VNC protocols
already built into Windows) was considered and rejected for v1: it requires
enabling Remote Desktop (Pro/Enterprise Windows editions only) and doesn't
give a clean path to custom features like our lightweight pairing flow or
media/power shortcuts. A small first-party agent gives full control over UX
and works on Windows Home.

## 7. Cross-Cutting Docs

- Data persisted by each side: `06-DATA-MODEL.md`
- Full wire protocol: `07-API-SPECIFICATION.md`
- Threat model and auth details: `09-SECURITY-PRIVACY.md`
- Latency/throughput targets: `12-PERFORMANCE.md`
