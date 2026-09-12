# API Specification — WebSocket Protocol

This is the authoritative reference for the message protocol between the
Android app and the Windows agent. Both `RemoteConnection.kt` (Android) and
`RemoteMessage` in `Program.cs` (Windows) must stay in sync with this doc.

## 1. Transport

- Protocol: WebSocket (RFC 6455) over **TLS**.
- URL: `wss://<pc-ip>:58642/`
- Encoding: UTF-8 JSON, one object per WebSocket **text** frame.
- TLS: the agent serves a self-signed certificate generated on first run; the
  app pins it per host on first successful handshake (trust-on-first-use) —
  see `09-SECURITY-PRIVACY.md` §2. Plaintext `ws://` is not served or
  accepted.

## 2. Message Envelope

Every message is a flat JSON object with a required `type` field and
type-dependent optional fields. There is no message ID/correlation system in
v1 (all commands are fire-and-forget except the auth handshake).

```json
{ "type": "<message_type>", "...": "..." }
```

## 3. Field Reference

| Field | JSON type | Used by |
|---|---|---|
| `type` | string | all messages (required) |
| `reason` | string | `disconnecting` |
| `dx`, `dy` | int | `mouse_move`, `mouse_scroll` |
| `button` | string (`"left"` \| `"right"` \| `"middle"`) | `mouse_click` |
| `action` | string | `mouse_click`, `media_control`, `system_power` |
| `key` | string | `key_press` |
| `modifiers` | string[] (`"CTRL"`, `"ALT"`, `"SHIFT"`, `"WIN"`) | `key_press` |
| `text` | string | `text_input` |
| `token` | string | `auth`, `auth_ok` |
| `pairingCode` | string | `auth` |

## 4. Message Types

### 4.1 `auth` (app → agent)

First message required on every connection.

```json
{ "type": "auth", "token": "3f9c1a2b...", "pairingCode": null }
```

- Send `token` (from a previous successful pairing) if available; omit/null
  otherwise.
- Send `pairingCode` when pairing for the first time (user-entered 6-digit
  code); omit/null on subsequent connections.
- At least one of `token` or `pairingCode` must be present for the agent to
  possibly authenticate the connection.

### 4.2 `auth_ok` (agent → app)

```json
{ "type": "auth_ok", "token": "3f9c1a2b..." }
```

Sent once auth succeeds. `token` is always included — either the token the
app already had (unchanged) or a newly issued one if the app authenticated
via pairing code for the first time. The app must persist this token keyed
by host (see `06-DATA-MODEL.md` §1.1).

### 4.3 `auth_failed` (agent → app)

```json
{ "type": "auth_failed" }
```

Sent when neither the token nor the pairing code validates. The app should
surface a generic connection error (see `04-UI-UX-SPECIFICATION.md` §9) —
deliberately no detail on *why* it failed.

### 4.4 `mouse_move` (app → agent)

```json
{ "type": "mouse_move", "dx": 12, "dy": -4 }
```

Relative cursor movement in pixels (agent-side pixel scale, not
phone-screen pixels). Sent continuously during a drag gesture.

### 4.5 `mouse_click` (app → agent)

```json
{ "type": "mouse_click", "button": "left", "action": "click" }
```

- `button`: `"left"` | `"right"` | `"middle"` (defaults to `"left"` if omitted).
- `action`: `"click"` (down+up), `"down"`, or `"up"` (defaults to `"click"`).
  `"down"`/`"up"` exist to support press-and-drag gestures (planned).

### 4.6 `mouse_scroll` (app → agent)

```json
{ "type": "mouse_scroll", "dy": -3 }
```

`dy` is a scroll-wheel delta (agent multiplies by 120 per the Windows wheel
convention internally — the app should send small integers, e.g. -5..5 per
event, not raw pixel deltas).

### 4.7 `key_press` (app → agent)

```json
{ "type": "key_press", "key": "ENTER", "modifiers": ["CTRL"] }
```

- `key`: one of the named special keys (`ENTER`, `BACKSPACE`, `TAB`, `ESC`,
  `SPACE`, `LEFT`, `UP`, `RIGHT`, `DOWN`, `DELETE`, `CTRL`, `ALT`, `SHIFT`,
  `WIN`) or a single printable character (e.g. `"C"`), matched
  case-insensitively.
- `modifiers`: optional list of modifier key names, held for the duration of
  the key press.

### 4.8 `text_input` (app → agent)

```json
{ "type": "text_input", "text": "hello world" }
```

Types arbitrary Unicode text via synthetic key events, one character at a
time. Preferred over many individual `key_press` messages for bulk text.

### 4.9 `media_control` (app → agent)

```json
{ "type": "media_control", "action": "play_pause" }
```

`action` ∈ `play_pause`, `next`, `prev`, `vol_up`, `vol_down`, `mute`.

### 4.10 `system_power` (app → agent)

```json
{ "type": "system_power", "action": "sleep" }
```

`action` ∈ `sleep`, `shutdown`, `restart`, `lock`. See
`02-FEATURE-SPECIFICATION.md` F5 for which actions require app-side
confirmation before sending.

### 4.11 `disconnecting` (agent → app)

```json
{ "type": "disconnecting", "reason": "shutdown" }
```

Sent immediately **before** the agent executes a `system_power` action that
terminates the connection (`shutdown` / `restart`). `reason` is the action
that was requested. The app treats the subsequent socket close as expected —
it shows "PC is shutting down…" instead of the reconnect banner and never
auto-reconnects (`10-ERROR-HANDLING.md` §3).

### 4.12 Planned message types (not yet implemented)

| Type | Direction | Purpose |
|---|---|---|
| `ping` / `pong` | both | Keep-alive / latency measurement (currently OkHttp's built-in ping interval covers keep-alive; app-level pong not yet used) |
| `clipboard_sync` | both | Push/pull clipboard text (F6) |
| `file_list_request` / `file_list_response` | app→agent / agent→app | Directory browsing (F7) |
| `file_transfer_start` / binary chunks | both | File upload/download (F7) |
| `screen_frame` | agent → app | Streamed screenshot/video frame, binary WebSocket message rather than JSON (F8) |

## 5. Error Handling at the Protocol Level

- Unknown `type` values are logged and ignored by the agent (see
  `HandleCommand`'s `default` case) — never crash the connection over an
  unrecognized message, since this allows the app and agent to be updated
  independently without hard version-locking for minor additions.
- Malformed JSON is silently dropped (`Deserialize` returning null is
  checked) rather than closing the socket, to tolerate occasional
  corrupted/partial frames without ending the session.
- Messages sent before `auth_ok` (other than `auth` itself) are currently
  **not explicitly rejected** in `HandleConnectionAsync` beyond the
  `if (!authenticated)` branch handling only auth-phase messages — this is
  correct behavior (pre-auth messages are effectively ignored since the
  `continue` skips straight to the next loop iteration) but should be
  covered by a test (`11-TESTING-STRATEGY.md`).

## 6. Versioning

There is no explicit protocol version field in v1. Since the app and agent
are developed together and users install both from the same project, a
breaking protocol change should for now be handled by bumping both
`CHANGELOG.md` entries together. A `protocolVersion` field should be added
to the `auth` handshake before this project has any independent
distribution of app vs. agent versions.

## 7. Discovery (mDNS) — pre-connection

Discovery is how the app finds the agent **before** any WebSocket connection
exists; it is not part of the WS protocol in §1–§6, so this section describes
conventions the agent's advertisement and the app's browser must share.

- Service type: `_pc-remote._tcp.local.` (DNS-SD, RFC 6763, carried over
  mDNS / multicast UDP 5353).
- Advertiser: the Windows agent announces itself on each local IPv4
  interface, on the same port the WebSocket listener uses (`58642`), so a
  discovered `host:port` feeds the §1 URL unchanged.
- TXT records: `name=<Windows computer name>` — a display label only. Never
  include tokens or pairing codes in TXT records; they are readable by
  anyone on the LAN.
- Browser: the Android app uses `NsdManager` (floor API 26 keeps it
  available). `discoverServices` is deprecated from API 33 but remains
  functional; the app deliberately keeps a single code path for now, to be
  revisited when per-network discovery
  (`registerServiceInfoCallback`/`DnsSdServiceDiscoverer`) is no longer
  experimental.
- Connection: a discovered `host:port` connects exactly like a manually
  entered IP — `ws://<host>:<port>/`, the `auth` handshake from §4.1–4.2,
  and trust tokens keyed by host as usual.
- Failure mode: discovery requires multicast on the LAN. If it is blocked
  (AP client isolation, corporate policy), the app lists no PCs; the manual
  IP entry path (§1) remains the documented fallback.
