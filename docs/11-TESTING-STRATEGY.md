# Testing Strategy

## 1. Testing Layers

| Layer | Android | Windows Agent |
|---|---|---|
| Unit tests | JUnit 5 + kotlinx-coroutines-test for `RemoteConnection` logic, `TokenStore` | xUnit for `PairingStore`, message (de)serialization |
| Integration tests | Compose UI tests (`androidx.compose.ui.test`) for Pairing/Touchpad screens against a fake `RemoteConnection` | Integration test spinning up the real `HttpListener` + a test WebSocket client, asserting on `Win32Input` calls via an injected interface (see §3) |
| Manual/exploratory | Real device against real agent on a home network | Real agent against real phone |
| End-to-end | See §5 | See §5 |

## 2. Unit Test Targets

### Android
- `RemoteMessage` serializes/deserializes correctly for every message type
  in `07-API-SPECIFICATION.md`.
- `TokenStore.getToken`/`saveToken` round-trip correctly per host.
- `RemoteConnection` state transitions: CONNECTING → CONNECTED on `auth_ok`,
  CONNECTING → FAILED on `auth_failed`, → DISCONNECTED on socket close.
- `DiscoveryService` browse lifecycle against a fake `NsdGateway`: found →
  PC added to the list, duplicate `onServiceFound` ignored, `onServiceLost`
  → PC removed, `stop()` clears the list and releases the multicast gate.
- `TokenStore.allHosts`/`forget` round-trip: saving tokens for several hosts
  lists them all, forgetting removes just one.

### Windows Agent
- `PairingStore.TryAuthenticate` accepts a valid pairing code, rejects an
  invalid one, accepts a previously-issued token, rejects an unknown token.
- `PairingStore.IssueTokenIfNeeded` returns the same token for an
  already-trusted token, and a new distinct token otherwise.
- `RemoteMessage` JSON round-trips for every field combination used by each
  message type.

## 3. Testability Improvement Needed

`Win32Input` and `SystemPower` currently call Win32 APIs directly as static
methods, which makes them hard to unit test (calling `SendInput` in a CI
environment is undesirable/impossible in a headless runner). **Recommended
refactor:** extract an `IInputSimulator` interface implemented by
`Win32Input`, and inject it into the connection-handling code, so tests can
substitute a mock that records calls (e.g., "was `MoveMouseRelative(12, -4)`
called after receiving a `mouse_move` message") without touching the real OS
input queue.

## 4. Key Manual Test Scenarios

These should be run before any release (feed into
`16-RELEASE-CHECKLIST.md`):

| # | Scenario | Expected result |
|---|---|---|
| 1 | Fresh pairing with correct IP + code | Reaches CONNECTED within 5s |
| 2 | Fresh pairing with wrong code | `auth_failed`, clear error shown, no crash |
| 3 | Reconnect to previously-paired PC (agent still running) | Connects silently, no code prompt |
| 4 | Reconnect after agent restart (tokens now persisted — token survives) | Connects silently, no code prompt; verify no re-pair is needed |
| 5 | Touchpad drag in all 4 directions | Cursor moves correctly on the PC, no inverted axes |
| 6 | Tap vs. long-press | Left-click vs. right-click, no accidental double-fire |
| 7 | Disable Wi-Fi mid-session | Banner shows disconnected state, app doesn't crash |
| 8 | Re-enable Wi-Fi after #7 | Manual retry reconnects successfully |
| 9 | Send Shutdown command | Confirmation dialog required; on confirm, PC shuts down; app shows an appropriate (non-error) disconnected state |
| 10 | Type text into a focused text field on PC | Exact text appears, including punctuation and at least one non-ASCII character |
| 11 | Ctrl+C / Ctrl+V via modifier-lock keyboard | Copy/paste works in a PC text editor |
| 12 | Run agent without the one-time `netsh urlacl` step, not as Administrator | Confirm the failure mode is a clear, loggable error, not a silent hang (see `17-TROUBLESHOOTING.md`) |
| 13 | Two phones pairing to the same PC | Both can pair and control independently; verify no token collision |
| 14 | Firewall blocking the port | Connection attempt from app times out with the standard "couldn't connect" message, not a crash |
| 15 | mDNS discovery with agent running on the same Wi-Fi | Agent appears in the discovery list within ~5s of the Pairing screen opening; tapping a never-paired PC prefills the IP and pairing proceeds normally |
| 16 | mDNS discovery with multicast blocked (guest/isolated AP) | Discovery shows no PCs (or a "failed" state); manual IP entry still connects |
| 17 | Modifier-lock on the Keyboard screen | Tap Ctrl (highlighted), tap C → PC performs Ctrl+C; lock clears after one key |
| 18 | Shutdown confirmation dialog | Cancel sends nothing; confirm sends `system_power(shutdown)` and the PC shuts down |
| 19 | Hold Volume + on the Media screen | Volume steps repeatedly (~150ms) until release; a quick tap steps once |
| 20 | First pairing over WSS with a fresh agent | App reaches CONNECTED; a `certpin_<host>` entry exists for the host afterwards (TOFU pin capture) |
| 21 | Agent cert changes (delete `server-cert.dat` + restart) | App connection fails instead of silently trusting the new cert (pin mismatch) |
| 22 | Expected disconnect after Shutdown | App shows "PC is shutting down…", does not show the reconnect banner and does not auto-reconnect |

## 5. End-to-End Test Environment

Because this system has no server-side component to mock, true end-to-end
testing requires a real Windows machine (or VM with network access) and a
real or emulated Android device on the same virtual/physical network
segment. For CI:

- Windows agent: can run its unit/integration tests on a Windows CI runner
  (e.g., GitHub Actions `windows-latest`) without needing an Android device.
- Android app: unit and Compose UI tests run against a fake
  `RemoteConnection` (no real network) on standard Android CI runners; full
  end-to-end (real agent + real/emulated app) is a manual pre-release step
  for now, not automated in CI, since it requires cross-machine networking
  CI doesn't provide by default.

## 6. Performance Testing

See `12-PERFORMANCE.md` for specific latency/throughput targets and how
they should be measured (e.g., round-trip timestamp logging for mouse-move
messages during manual testing).

## 7. Accessibility Testing

See `13-ACCESSIBILITY.md` §4 for the TalkBack-specific manual test pass
required before release, since automated Compose tests do not fully
exercise screen-reader behavior.

## 8. Regression Checklist Ownership

Every new feature added per `02-FEATURE-SPECIFICATION.md` should extend §4
of this document with new manual scenarios before being marked "Implemented"
in that doc.
