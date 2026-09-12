# Performance

## 1. Latency Targets

| Interaction | Target (perceived, end-to-end) | Notes |
|---|---|---|
| Mouse move (drag → cursor moves on PC) | < 100 ms on typical home Wi-Fi (both devices on same AP, decent signal) | Dominated by Wi-Fi round-trip, not app/agent processing |
| Click (tap → click registers) | < 100 ms | Single small message, same path as mouse move |
| Key press | < 100 ms | Same |
| Media control | < 150 ms acceptable | Less latency-sensitive than cursor tracking |
| Power action | No strict target — user expects a brief delay for shutdown/restart | Confirmation dialog itself adds intentional friction, see `02-FEATURE-SPECIFICATION.md` F5 |

These targets assume the local-network-only architecture
(`05-TECHNICAL-ARCHITECTURE.md` §2.1); they do not apply once/if remote
relay access is added, which would need its own budget accounting for
relay hop latency.

## 2. Message Throughput & Throttling

- **Mouse-move flood risk:** a fast drag gesture on Compose can generate
  many `pointerInput` callback invocations per second. `TouchpadScreen.kt`
  currently sends one `mouse_move` message per drag delta with no explicit
  throttle. **Recommendation:** cap outgoing mouse-move messages to roughly
  60/sec (aligned with typical display refresh rate) by coalescing deltas
  that arrive faster than a ~16ms window, rather than sending every single
  pointer event — reduces socket/JSON overhead without a perceptible loss
  of smoothness.
- **Volume press-and-hold** (`02-FEATURE-SPECIFICATION.md` F4.3): repeat
  interval of ~150ms is deliberately much coarser than mouse-move, since
  volume steps are discrete and don't need frame-rate-level responsiveness.

## 3. Payload Size

- Every current message type is a small flat JSON object (well under 200
  bytes) — negligible overhead relative to typical Wi-Fi MTU/latency.
- **Future concern:** `screen_frame` messages (F8, screen mirroring) will be
  orders of magnitude larger (JPEG frames). These should use **binary**
  WebSocket messages rather than base64-encoded JSON to avoid ~33% size
  bloat from base64 encoding, and should target a frame budget (e.g., a
  1280x720 JPEG at quality 60 is typically 50-150KB) that keeps a 5-15 FPS
  stream within reasonable home Wi-Fi bandwidth (a few Mbps).
- **Future concern:** file transfer (F7) should chunk at a size (e.g. 64KB
  per chunk, per the original architecture doc) that balances per-message
  overhead against not holding an entire large file in memory on either
  side at once.

## 4. Agent-Side Performance

- Each connection's Win32 input calls (`SendInput`, `keybd_event`) are fast,
  synchronous OS calls — not a bottleneck at the scale of a single active
  controlling phone.
- The agent handles each connection on its own `Task` (see
  `05-TECHNICAL-ARCHITECTURE.md` §3) — fine for the expected concurrency
  (a handful of phones at most, realistically one active controller at a
  time), no thread-pool tuning needed at this scale.

## 5. Android App Performance

- **Battery:** maintaining an open WebSocket (with OkHttp's 15s ping
  interval, see `RemoteConnection`) while the app is in the foreground has
  negligible battery impact for typical session lengths (minutes, not
  hours). A planned foreground service (for background operation,
  `05-TECHNICAL-ARCHITECTURE.md` §4) should be scoped to only run while the
  user has an active control session, not persist indefinitely, to avoid
  unnecessary battery drain.
- **UI thread:** all WebSocket send/receive happens on OkHttp's own
  dispatcher thread, not the Compose UI thread — gesture handling in
  `TouchpadScreen.kt` should remain lightweight (just computing deltas and
  calling `sendMouseMove`) to avoid any UI jank during fast drags.

## 6. Measuring Latency in Practice

For manual performance testing (`11-TESTING-STRATEGY.md` §4), the simplest
approach without adding permanent instrumentation:
1. Temporarily log a timestamp when a drag delta is captured in
   `TouchpadScreen.kt` and again when the corresponding `Win32Input.MoveMouseRelative`
   call executes on the agent (requires clocks reasonably in sync, or just
   measure the agent's processing time separately from network RTT via a
   simple ping/pong round trip).
2. Use `adb` Wi-Fi RTT to the PC's IP as a baseline network-latency sanity
   check independent of the app.

A proper latency-tracking `ping`/`pong` message pair (listed as planned in
`07-API-SPECIFICATION.md` §4.11) would make this repeatable without ad hoc
logging and should be prioritized once mouse-move responsiveness needs to
be tuned further.
