# PC Remote — Android App

The Android client half of PC Remote. Pairing screen (mDNS discovery of PCs
+ manual IP/code fallback), and a full control set behind a dark-first
bottom-nav shell: touchpad (gestures + D-pad accessible mode), keyboard
(modifier-lock + special keys + debounced text), media (hold-to-repeat
volume), power (confirmed destructive actions), and settings (paired PCs,
sensitivity, about). The connection is **WSS-only** with per-host certificate
pinning (trust-on-first-use), `wss://<pc-ip>:58642/`.

## Required manifest permission

All permissions are normal (auto-granted) except `POST_NOTIFICATIONS`,
which is requested at runtime when a session starts:

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_DATA_SYNC" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
```

- `CHANGE_WIFI_MULTICAST_STATE` — MulticastLock while browsing mDNS, so the
  agent's multicast replies are received.
- `FOREGROUND_SERVICE*` + `POST_NOTIFICATIONS` — the "Controlling your PC"
  foreground service that keeps a session alive when backgrounded.
- No cleartext: the app only connects to `wss://` (`usesCleartextTraffic` is
  off by manifest).

See `docs/09-SECURITY-PRIVACY.md` §7 for the full justification list.

## Files

- `network/RemoteConnection.kt` — OkHttp WebSocket client, pairing/auth
  handshake, and the command-sending API (`sendMouseMove`, `sendKey`, etc.)
  matching the Windows agent's JSON protocol.
- `network/DiscoveryService.kt` — mDNS browser (`NsdManager` wrapper) that
  lists PCs advertising `_pc-remote._tcp.`; starts/stops with the pairing
  screen's lifecycle.
- `service/ConnectionForegroundService.kt` — keeps the session alive while
  backgrounded; started/stopped with the CONNECTED state.
- `ui/PairingScreen.kt` — "Discover nearby PC" list + manual IP/code
  fallback.
- `ui/TouchpadScreen.kt` — Compose gesture surface: drag to move the cursor,
  tap for left-click, long-press for right-click, plus explicit Left/Right
  buttons; sensitivity comes from Settings.
- `ui/KeyboardScreen.kt` / `ui/MediaScreen.kt` / `ui/PowerScreen.kt` /
  `ui/SettingsScreen.kt` — the control screens (see `docs/04-UI-UX-SPECIFICATION.md`
  §5–§7): modifier-lock, hold-to-repeat volume, `AlertDialog`-confirmed
  shutdown, and paired-PC/sensitivity settings.
- `ui/theme/Theme.kt` — the app's dark-first custom palette.
- `MainActivity.kt` — wires everything: Pairing ↔ bottom-nav ControlHub
  (Touchpad/Keyboard/Media/Power) with Settings reachable from the title row.

## Not yet implemented here (see main docs)

- Compose Navigation (`05` §4 planned addition) — the bottom nav is
  remembered-tab state in `MainActivity`.
- Reconnect-attempt ceiling (auto-retry currently runs while the token is
  valid; a ~5-minute cap with a manual Retry fallback is planned, `10` §5).
- TalkBack end-to-end verification on every screen
  (`13-ACCESSIBILITY.md` §6 audit).

## Try it end to end

1. Run the Windows agent (`windows-agent/README.md`) and note its pairing code.
2. Make sure your phone and PC are on the same Wi-Fi network.
3. Build/run this app on your phone — your PC should appear in
   "Discover nearby PC"; tap it, enter the pairing code (first time only),
   tap Connect. If discovery doesn't work on your network, type the PC's IP
   manually instead.
4. Drag on the touchpad — the PC's cursor should move.
