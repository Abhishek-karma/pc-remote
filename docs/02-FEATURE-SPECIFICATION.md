# Feature Specification

Each feature below lists functional requirements and acceptance criteria.
Status reflects the current codebase (`windows-agent/`, `android-app/`).

---

## F1. Pairing & Connection

**Status:** Implemented (manual IP + pairing code; mDNS discovery)

**Requirements:**
- FR1.1: Agent generates a random 6-digit pairing code on startup, shown in
  its console/UI.
- FR1.2: Android app accepts a host IP and pairing code, sends an `auth`
  message, and transitions to CONNECTED on `auth_ok`.
- FR1.3: On successful pairing, the agent issues a trust token; the app
  persists it (keyed by host) so future connections to the same IP
  authenticate silently. The agent persists trusted tokens too
  (DPAPI-protected file), so devices stay paired across agent restarts.
- FR1.4: Failed auth (wrong code, expired code) returns `auth_failed` and the
  app shows a clear error without crashing or hanging.
- FR1.5: Agent advertises itself via mDNS (`_pc-remote._tcp.local.`, see
  `07-API-SPECIFICATION.md` §7) so the app can list discoverable PCs instead
  of requiring manual IP entry. Manual IP entry remains as a fallback.

**Acceptance criteria:**
- Given a fresh install of both agent and app, entering the correct IP and
  pairing code results in a CONNECTED state within 5 seconds on a normal LAN.
- Given a previously-paired PC at the same IP, opening the app and tapping
  Connect (no code) reaches CONNECTED without prompting for a code.
- Given a wrong pairing code, the app shows an error and does not enter a
  false-CONNECTED state.
- Given agent and app on the same subnet with multicast allowed, the agent
  appears in the app's discovery list within ~5 seconds of the Pairing
  screen opening.
- Given a previously-paired PC shown in the discovery list, tapping it
  connects with the saved token (no code prompt).
- Given a discovered PC that was never paired, tapping it prefills the IP
  field and pairing proceeds with the code as normal.
- Given multicast blocked (e.g. AP client isolation), discovery shows no PCs
  but manual IP entry still works.

---

## F2. Touchpad (Mouse Control)

**Status:** Implemented

**Requirements:**
- FR2.1: Single-finger drag sends relative `mouse_move` deltas.
- FR2.2: Tap sends a left `mouse_click`.
- FR2.3: Long-press sends a right `mouse_click`.
- FR2.4: Dedicated Left/Right buttons available below the touch surface for
  unambiguous clicks.
- FR2.5 *(planned)*: Two-finger drag maps to `mouse_scroll`.
- FR2.6: Adjustable sensitivity multiplier in settings (Settings →
  "Touchpad sensitivity", 0.5×–3.0×, applied live).
- FR2.7 *(planned)*: Press-and-drag (long-press then move without lifting)
  performs a click-and-drag rather than a right-click + move.

**Acceptance criteria:**
- Dragging a finger across the touchpad moves the PC cursor in the same
  relative direction with no perceptible (>150ms) lag on a normal LAN.
- A quick tap does not also register as the start of a drag (no accidental
  cursor jump on tap).

---

## F3. Keyboard Input

**Status:** Implemented (protocol + Android UI incl. modifier-lock)

**Requirements:**
- FR3.1: A text field lets the user type; each keystroke or a debounced
  batch is sent as `text_input`.
- FR3.2: A row of special-key buttons (Enter, Backspace, Tab, Esc, arrows,
  Delete) sends `key_press` with the corresponding key name.
- FR3.3: Modifier keys (Ctrl, Alt, Shift, Win) can be combined with a
  subsequent key press (e.g., Ctrl+C) via `modifiers[]`.
- FR3.4: A "modifier lock" mode where tapping Ctrl highlights it and the
  next key sent includes it as a modifier, then it un-highlights.

**Acceptance criteria:**
- Typing in the text field results in the same text appearing wherever the
  PC's focus currently is (a text editor, browser address bar, etc.).
- Tapping Ctrl then C (in modifier-lock mode) performs a copy action on the
  PC, equivalent to a physical Ctrl+C.

---

## F4. Media Control

**Status:** Implemented (protocol + Android UI)

**Requirements:**
- FR4.1: Buttons for Play/Pause, Next, Previous, Volume Up, Volume Down, Mute.
- FR4.2: Each button sends a single `media_control` message with the
  corresponding `action`.
- FR4.3: Volume up/down support press-and-hold to repeat (send repeated
  messages at a fixed 150ms interval while held).

**Acceptance criteria:**
- Tapping Play/Pause toggles playback in whatever app currently owns media
  focus on the PC (matches native Windows media-key behavior since the
  agent simulates the same virtual key).
- Holding Volume + steps the volume up repeatedly until the button is
  released.

---

## F5. Power Control

**Status:** Implemented (protocol + Android UI)

**Requirements:**
- FR5.1: Buttons for Sleep, Lock, Restart, Shutdown.
- FR5.2: Restart and Shutdown require an in-app confirmation dialog before
  sending the message (destructive/hard-to-undo actions).
- FR5.3: Sleep and Lock execute immediately without confirmation (low risk,
  easily reversible).

**Acceptance criteria:**
- Tapping Shutdown shows a confirmation dialog; only tapping "Confirm" sends
  the `system_power` message.
- Tapping Lock immediately locks the PC's session with no dialog.

---

## F6. Clipboard Sync *(planned, not started)*

**Requirements (future):**
- FR6.1: Opt-in setting per PC — off by default.
- FR6.2: When enabled, PC clipboard text changes are pushed to the phone and
  vice versa via `clipboard_sync` messages.
- FR6.3: Never sync clipboard content silently in the background without the
  setting explicitly enabled — see `09-SECURITY-PRIVACY.md`.

---

## F7. File Browser & Transfer *(planned, not started)*

**Requirements (future):**
- FR7.1: Browse a PC-designated set of folders (not the full filesystem by
  default) via `file_list_request`/`file_list_response`.
- FR7.2: Download a file from PC to phone, and upload from phone to PC, with
  progress indication and cancel support.
- FR7.3: Large files transferred over chunks (see `12-PERFORMANCE.md` for
  chunk-size targets) rather than a single in-memory blob.

---

## F8. Screen Mirroring *(planned, not started)*

**Requirements (future):**
- FR8.1: PC streams periodic screenshots (JPEG) at a configurable frame rate.
- FR8.2: Phone displays the stream full-screen with pinch-to-zoom.
- FR8.3 *(later)*: Upgrade to a proper video codec stream for smoother
  mirroring, treated as a separate, larger effort (see
  `05-TECHNICAL-ARCHITECTURE.md` §6.2).

---

## F9. Settings

**Status:** Implemented (paired-PC list with rename/forget, touchpad
sensitivity, about). The clipboard-sync toggle (FR9.3) is deferred until F6.

**Requirements (future):**
- FR9.1: List of paired PCs with the ability to rename or forget (revoke)
  each. Implemented — display names are stored in the app and shown in the
  list; "Forget" deletes the local token.
- FR9.2: Touchpad sensitivity slider. Implemented (0.5×–3.0×, persisted,
  applied live to the touchpad screen).
- FR9.3: Toggle for clipboard sync (per PC) — deferred until F6 ships.
- FR9.4: App version/about screen. Implemented.
