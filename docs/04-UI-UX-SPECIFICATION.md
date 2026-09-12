# UI/UX Specification

## 1. Screen Inventory

| Screen | Status | Purpose |
|---|---|---|
| Pairing | Implemented | List discovered PCs (mDNS) + manual IP/code fallback, initiate connection |
| Touchpad | Implemented | Primary mouse control surface |
| Keyboard | Implemented | Text entry + special keys + modifier-lock row |
| Media | Implemented | Playback/volume controls (hold-to-repeat volume) |
| Power | Implemented | Sleep/lock/restart/shutdown (destructive actions confirmed) |
| Settings | Implemented | Paired PCs (forget), touchpad sensitivity, about |
| (Connection status banner) | Planned | Persistent overlay indicating connection health |

## 2. Navigation Structure

Once past pairing, the app uses a **bottom navigation bar** with four
destinations, matching the core feature set:

```
┌───────────────────────────────────┐
│                                     │
│         [ active screen ]          │
│                                     │
├───────────────────────────────────┤
│  Touchpad │ Keyboard │ Media │ Power │
└───────────────────────────────────┘
```

Settings is accessed via a toolbar icon (top-right), not a bottom tab, since
it's used infrequently relative to the four core controls.

A **persistent thin status banner** appears at the very top of every screen
when the connection is anything other than CONNECTED (e.g., "Reconnecting…",
"Disconnected — tap to retry"). It collapses to nothing when CONNECTED to
avoid visual clutter during normal use.

## 3. Visual Design Principles

- **Minimal chrome, maximize control surface.** The touchpad screen in
  particular should give as much screen real estate as possible to the
  gesture area — the app's whole value is in that surface feeling responsive
  and generous, not cramped.
- **Dark-first.** Since this app is often used in a dim room in front of a
  TV (HTPC scenario), default to a dark theme (Material 3 dark color scheme)
  regardless of system setting, with a light-theme override in Settings for
  desk users who prefer it.
- **Large touch targets.** Buttons (media, power, keyboard special keys) use
  a minimum 48dp touch target per Material guidelines — this is a
  "reach for it without looking closely" app, often used one-handed.
- **No ads, no upsell UI.** Nothing in the interface should visually resemble
  or make room for advertising — this is a personal/utility tool.

## 4. Touchpad Screen — Interaction Detail

- The gesture surface fills all available vertical space above the
  Left/Right button row (see `TouchpadScreen.kt`).
- Visual feedback on touch-down: a subtle ripple or opacity change at the
  touch point, so the user gets confirmation the surface registered their
  finger (important since there's no physical click feedback).
- The instructional text ("Drag to move • Tap = left click • Long-press =
  right click") shown in the empty implementation should be replaced with a
  **first-run overlay/tooltip** that appears once and then never again
  (tracked via a local "hasSeenTouchpadTutorial" flag), rather than
  permanent on-screen text that wastes space for returning users.

## 5. Keyboard Screen — Layout (implemented)

```
┌───────────────────────────────────┐
│  [ CTRL ] [ ALT ] [ SHIFT ] [ WIN ]│  ← modifier-lock row (toggle; the
│                                     │    next key press uses them, then
│                                     │    they unlock again)
├───────────────────────────────────┤
│  [ Esc ] [ Tab ] [ Up ]  [ Del ]    │
│  [ Left ] [ Down ] [ Right ] [ Bksp]│  ← special keys grid
├───────────────────────────────────┤
│  [ Type here...              ][Ent]│  ← text field, sends debounced
│                                     │    text_input; Enter sends key_press
└───────────────────────────────────┘
```

## 6. Media Screen — Layout (implemented)

The volume control ships as press-and-hold `−`/`+` buttons rather than a
drag slider — FR4.3 requires hold-to-repeat, and a slider's step mapping
can't express "keep going while held" as cleanly.

```
┌───────────────────────────────────┐
│  [ Prev ]  [ Play / Pause ]  [ Next ] │  ← transport, tap-to-send
│                                     │
│  [ Volume − ]  [ Mute ]  [ Volume + ]│  ← hold −/+ to repeat every 150ms
└───────────────────────────────────┘
```

## 7. Power Screen — Layout (implemented)

```
┌───────────────────────────────────┐
│   [ Sleep ]      [ Lock ]          │  ← immediate, no confirmation
│                                     │
│  [ Restart ]   [ Shut Down ]        │  ← confirmation dialog required
└───────────────────────────────────┘
```

## 8. Pairing Screen — Discovery Section

The pairing screen keeps the existing manual IP + pairing-code fields and
adds a discovery list above them:

```
┌───────────────────────────────────┐
│  Discover nearby PC                 │
│  [ Looking for PCs… ]               │
│  ┌─────────────────────────────┐   │
│  │ ○ My Desktop PC            │   │  ← discovered PC, tap to select
│  │   192.168.1.23:58642        │   │
│  └─────────────────────────────┘   │
│  [ Refresh ]                       │
│  PC IP address  [ ______________ ] │  ← manual fallback; prefilled when
│                                     │    a PC is tapped
│  Pairing code   [ ______________ ] │
│  [ Connect ]                       │
└───────────────────────────────────┘
```

- **States:** Searching ("Looking for PCs…"), **Found** (list of PCs, each
  showing the advertised name plus `host:port`), **Empty** ("No PCs found —
  make sure both devices are on the same Wi-Fi and the agent is running"),
  **Failed** (multicast blocked → "Discovery failed — enter the IP
  manually").
- Tapping a discovered PC prefills the IP field. If a trust token already
  exists for that host, the app connects directly (no code prompt) — never
  auto-connects/auto-pairs with an unpaired host.
- A Refresh button restarts the browse. Discovery runs only while this
  screen is visible.

## 9. Empty & Error States

| State | Screen | Treatment |
|---|---|---|
| No PC paired yet | App launch | Show Pairing screen directly, no empty dashboard |
| Discovery found no PCs | Pairing screen | "No PCs found" message (see §8); manual fields remain usable |
| Connection lost | Any control screen | Top banner (see §2), controls remain visible but visually dimmed/disabled |
| Pairing failed | Pairing screen | Inline error text under the form, form remains editable |
| No paired PCs (Settings) | Settings | "No PCs paired yet" with a button back to Pairing flow |

## 10. Accessibility Cross-Reference

See `13-ACCESSIBILITY.md` for the full treatment. Key UI implication: the
touchpad's core interaction (relative-drag gestures) is not meaningfully
accessible to screen-reader/switch-access users, so the Keyboard and a
future "D-pad mode" (discrete up/down/left/right/click buttons) must remain
fully operable via TalkBack as the accessible alternative path to cursor
control.

## 11. Copy/Tone Guidelines

- Error messages are plain-language and actionable ("Check that the agent is
  running and both devices are on the same Wi-Fi," not "Error: ECONNREFUSED").
- Confirmation dialogs use the action as the affirmative button label
  ("Shut Down", not a generic "OK") so the consequence is unambiguous at a
  glance.
