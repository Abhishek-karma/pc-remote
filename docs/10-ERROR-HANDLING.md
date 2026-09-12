# Error Handling

## 1. Error Categories

| Category | Examples | Where handled |
|---|---|---|
| Connection errors | Host unreachable, connection refused, timeout | `RemoteConnection` (Android), surfaced via `ConnectionState.FAILED` |
| Authentication errors | Wrong pairing code, revoked/invalid token | `auth_failed` message, handled in `RemoteConnection.onMessage` |
| Mid-session disconnects | Wi-Fi drop, PC sleeps, agent crashes | `onClosed`/`onFailure` callbacks → `ConnectionState.DISCONNECTED` |
| Input-simulation failures | Win32 call fails (rare — e.g. no active desktop session, locked workstation) | Agent-side, currently unhandled/unlogged — gap, see §4 |
| Malformed/unexpected messages | Bad JSON, unknown `type` | Agent: dropped silently (see `07-API-SPECIFICATION.md` §5); Android: `runCatching` around decode |
| Expected disconnects | User-initiated shutdown/restart/lock | Should be distinguished from error disconnects — see §3 |

## 2. Android App: User-Facing Error States

Per `04-UI-UX-SPECIFICATION.md` §8, errors are shown via:
- Inline error text on the Pairing screen for auth/connect failures.
- A persistent top banner on control screens for post-connection drops.

**Message copy guidelines** (see `04-UI-UX-SPECIFICATION.md` §10): always
plain-language and actionable, never a raw exception message or stack trace
surfaced to the user.

| Situation | User-facing message |
|---|---|
| Can't reach host at all (timeout/refused) | "Couldn't connect — check the IP/pairing code and that the agent is running." |
| `auth_failed` received | Same message as above (deliberately not distinguishing wrong-IP vs wrong-code, see `09-SECURITY-PRIVACY.md` §1) |
| Connection drops mid-session, not user-initiated | "Disconnected — trying to reconnect…" (banner, non-blocking) |
| Reconnect attempts exhausted | "Can't reach PC — check it's on and on the same network" + manual Retry button |

## 3. Distinguishing Expected vs. Unexpected Disconnects

When the user taps Shutdown/Restart (`02-FEATURE-SPECIFICATION.md` F5), the
resulting disconnect is **expected** and must not trigger the same alarming
"Disconnected — trying to reconnect" banner as an accidental Wi-Fi drop.

**Current behavior:** the agent sends a final
`{ "type": "disconnecting", "reason": "shutdown" }` message before
executing the power action (`07-API-SPECIFICATION.md` §4.11). The app shows
"PC is shutting down…" in place of the retry banner and suppresses all
auto-reconnect attempts for that drop.

## 4. Agent-Side Error Handling

**Current state:**
- `HandleConnectionAsync` wraps its main loop in a `try/catch` that logs the
  exception to the console and closes the connection cleanly — a crash in
  one connection's handling does not take down the whole agent process.
- `HandleCommand` does **not** currently catch exceptions from the
  `Win32Input`/`SystemPower` calls it dispatches to. A failure inside, e.g.,
  `Win32Input.TypeText` would propagate up and be caught by the outer
  `try/catch` in `HandleConnectionAsync`, terminating that connection rather
  than the whole agent — acceptable as a fallback, but the specific failure
  reason is only visible in the console log, not reported back to the app.

**Planned improvement:** wrap each `HandleCommand` dispatch individually so
one bad command (e.g., an unrecognized key name causing an unexpected
exception) logs and continues rather than closing the entire connection.

## 5. Retry & Reconnection Policy

**Implemented** in `RemoteConnection.kt`:

- On unexpected disconnect (socket failure or server close), automatic
  reconnect with capped exponential backoff: 1s, 2s, 4s, 8s, 16s, 30s
  (capped).
- Reconnects re-authenticate with the saved trust token only — no pairing
  code prompt.
- Never retry automatically after an `auth_failed` (the token was rejected —
  that requires user input, not a network retry) or after a certificate-pin
  mismatch (a changed cert cannot be fixed by retrying).
- `disconnecting` from the agent (expected shutdown) never triggers
  reconnect, and the banner reads "PC is shutting down…".
- The reconnect loop currently runs indefinitely while the token is valid;
  capping attempts after ~5 minutes with a manual Retry fallback remains a
  planned refinement (`04-UI-UX-SPECIFICATION.md` §2 banner states).

## 6. Logging of Errors

See `14-OBSERVABILITY-LOGGING.md` for full logging policy. Summary as it
relates to error handling: connection/auth errors are logged locally
(Logcat on Android, console on the agent) for developer debugging; no error
telemetry is sent anywhere automatically (consistent with the no-cloud
stance in `09-SECURITY-PRIVACY.md` §5).

## 7. Testing Error Paths

See `11-TESTING-STRATEGY.md` §4 for the specific test cases covering: wrong
pairing code, agent not running, Wi-Fi disabled mid-session, and agent
restart while a phone was previously trusted.
