# User Flows

## UF1. First-Time Pairing

```
User installs & runs Windows agent
        │
        ▼
Agent shows: pairing code + IP address(es) in console
(and advertises itself via mDNS as "_pc-remote._tcp.local.")
        │
        ▼
User opens Android app (first launch → Pairing screen)
        │
        ├── PC appears in the discovery list ──► user taps it (IP prefills)
        │
        └── no PC found ──► user enters the IP manually
        │
        ▼
User enters pairing code, taps "Connect"
        │
        ▼
App sends `auth` message with pairingCode ──────► Agent validates code
        │                                                  │
        │                                          ┌───────┴────────┐
        │                                     valid │                │ invalid
        │                                          ▼                ▼
        │                              Agent issues trust token   Agent sends auth_failed
        │                                          │                │
        ◄──────────────────────────────────────────┘                │
        ▼                                                            ▼
App saves token, shows Touchpad screen                 App shows error, stays on Pairing screen
```

**Edge cases:**
- Wrong IP entered → connection attempt times out → app shows "Couldn't
  connect — check the IP/pairing code and that the agent is running."
- Correct IP, wrong/expired code → `auth_failed` → same error message
  (deliberately not distinguishing "wrong IP" vs "wrong code" over the wire,
  since a would-be attacker shouldn't get hints about which part failed).
- Discovery finds nothing → app shows the "No PCs found" state; user checks
  same-Wi-Fi / agent running / AP isolation, then either taps Refresh or
  falls back to entering the IP manually.
- Tapping a discovered PC that was paired before (a token is saved for its
  host) → app connects directly without asking for a code.

## UF2. Returning User (Already Paired)

```
User opens app → app has a saved token for the last-used host
        │
        ▼
Discovery list shows the PC — also covers IP changes from DHCP:
the PC advertises by name, so it stays findable when its IP changed
        │
        ▼
User taps the PC → app connects with the saved token, no code needed
        │
        ├── success → Touchpad screen, no user action needed
        │
        └── failure (agent not running / token revoked)
                │
                ▼
        Pairing screen shown with the IP prefilled, pairing code field empty
        (user re-enters code only if the agent no longer recognizes the token)
```

## UF3. Using the Touchpad

```
User is on Touchpad screen, CONNECTED state
        │
        ├─ drags finger ─────► app sends mouse_move deltas ─► PC cursor moves
        ├─ taps ─────────────► app sends mouse_click(left, click) ─► PC left-clicks
        ├─ long-presses ─────► app sends mouse_click(right, click) ─► PC right-clicks
        └─ taps Left/Right button ─► same as above, explicit
```

If the connection drops mid-gesture (Wi-Fi hiccup), the app should show a
non-blocking "Reconnecting…" indicator and queue nothing — dropped mouse
deltas during a brief disconnect are acceptable and expected (see
`10-ERROR-HANDLING.md`).

## UF4. Sending Text / Special Keys

```
User switches to Keyboard tab/screen
        │
        ▼
User types in the text field ──► app sends text_input on each debounced change
        │
        ▼
User taps a special key (e.g. Enter) ──► app sends key_press("ENTER")
        │
        ▼
User taps Ctrl (modifier-lock highlights it), then taps "C"
        │
        ▼
app sends key_press("C", modifiers=["CTRL"]) ──► PC performs Ctrl+C
```

## UF5. Media Control

```
User switches to Media tab
        │
        ▼
User taps Play/Pause ──► media_control(play_pause) ──► toggles PC media playback
User holds Volume Up ──► repeated media_control(vol_up) every ~150ms while held
```

## UF6. Power Action (Destructive)

```
User taps "Shutdown"
        │
        ▼
App shows confirmation dialog: "Shut down this PC?"  [Cancel] [Shut Down]
        │
   Cancel │                                    │ Shut Down
        ▼                                       ▼
Dialog closes, no message sent      app sends system_power(shutdown)
                                                │
                                                ▼
                                     Agent executes `shutdown /s /t 0`
                                                │
                                                ▼
                                     Connection drops (PC powering off) —
                                     app shows DISCONNECTED, not an error
```

Note: a disconnect immediately following a user-initiated shutdown/restart
should be treated as *expected*, not surfaced as a connection error — see
`10-ERROR-HANDLING.md` for how the app should distinguish this.

## UF7. Losing and Regaining Connection

```
CONNECTED
    │
    ▼ (Wi-Fi drops / PC sleeps / app backgrounded and killed)
DISCONNECTED — app shows a banner: "Disconnected — trying to reconnect…"
    │
    ▼ (automatic retry every few seconds, capped backoff)
    ├── PC reachable again → re-authenticates with saved token → CONNECTED
    └── still unreachable after N attempts → banner changes to
        "Can't reach PC — check it's on and on the same network" with a
        manual "Retry" button
```

## UF8. Forgetting a Paired PC *(future, once Settings screen exists)*

```
User opens Settings → Paired PCs list
        │
        ▼
User taps "Forget" next to a PC
        │
        ▼
App deletes the saved token for that host locally
        │
        ▼
(Optional, future) App notifies the agent to revoke the token server-side too,
so a stolen/old phone can't reuse it if it somehow retained a copy
```
