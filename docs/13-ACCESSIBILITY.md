# Accessibility

## 1. Core Challenge

This app's primary interaction — a relative-drag touchpad gesture — is
inherently visual/motor-dependent and does not map cleanly onto standard
screen-reader (TalkBack) interaction patterns, which are built around
discrete, announceable elements rather than continuous freeform gestures.
Accessibility here means providing a **fully-functional alternative path**
to every capability, not making the touchpad gesture itself screen-reader
friendly (which isn't really achievable for a true analog drag surface).

## 2. Requirements by Screen

### Touchpad screen
- The gesture surface itself should be marked so TalkBack does not attempt
  to intercept/consume drag gestures as navigation swipes (Compose:
  appropriate `semantics {}` / `clearAndSetSemantics` handling needed —
  **not yet implemented** in `TouchpadScreen.kt`).
- The explicit Left/Right click buttons already present are standard
  `Button` composables and should be accessible by default (contentDescription
  = visible label "Left"/"Right" is sufficient) — verify with TalkBack once
  built, don't assume.
- **Planned "D-pad mode":** a discrete alternative control (up/down/left/
  right/click buttons, each independently focusable and TalkBack-navigable)
  as the primary accessible path to cursor movement, since incremental
  button-based movement is achievable via screen reader where a continuous
  drag surface is not. This should be reachable via a clearly labeled
  toggle, not buried in Settings.

### Keyboard screen (implemented)
- All special-key and modifier buttons are text-labeled buttons, so their
  labels are their accessibility text; the modifier-lock state is exposed
  via `selected` semantics (announced by TalkBack as on/off).
- The text input field is a standard `OutlinedTextField` (native
  `TextField` semantics are accessible by default).

### Media / Power screens (implemented)
- All controls are text-labeled buttons (no icon-only buttons), so every
  control has a text alternative for TalkBack.
- The Shutdown/Restart confirmation dialog is a standard
  `AlertDialog` so TalkBack correctly announces it and focuses the dialog's
  actionable buttons — not a custom overlay that might not receive
  accessibility focus.

## 3. General Requirements (all screens)

- **Font scaling:** UI should use `sp` units for text and scale gracefully
  up to at least 130% system font size without clipped or overlapping text
  — verify manually with the system "largest font" accessibility setting.
- **Contrast:** the dark-first theme (`04-UI-UX-SPECIFICATION.md` §3) must
  meet WCAG AA contrast ratios (4.5:1 for normal text, 3:1 for large
  text/icons) for all text and icon-on-background combinations, not just
  "looks fine at a glance."
- **Touch target size:** already specified at minimum 48dp in
  `04-UI-UX-SPECIFICATION.md` §3 — this benefits both general usability and
  users with motor-control difficulty; keep this as a hard floor, not a
  guideline to trim for visual density.
- **Color-independent state:** connection status (the top banner,
  `04-UI-UX-SPECIFICATION.md` §2) must not rely on color alone (e.g., green
  vs. red) — pair with icon and text ("Connected" / "Disconnected") so
  color-blind users get the same information.

## 4. Windows Agent Accessibility

The agent's current console-app interface is not a real end-user
accessibility surface (it's a developer/setup-time tool). Once it becomes a
tray app (`05-TECHNICAL-ARCHITECTURE.md` §3), it should follow standard
Windows accessibility guidelines (accessible names on tray menu items,
keyboard-navigable settings window) since some users setting up the PC side
may also rely on assistive technology.

## 5. Testing

- Manual TalkBack pass required before release: navigate every screen using
  only TalkBack gestures (no direct touch-and-look), confirm every
  interactive element is reachable, correctly labeled, and actionable.
- Manual test with system font size set to maximum and with high-contrast
  text enabled, checking every screen in this doc for clipping/overlap.
- Add these two passes to `11-TESTING-STRATEGY.md` §4's scenario list and to
  `16-RELEASE-CHECKLIST.md` before shipping the Keyboard/Media/Power screens.

## 6. Known Current Gap

As of this writing, `TouchpadScreen.kt` and `PairingScreen.kt` have not been
audited with TalkBack at all — this doc's requirements above are the target
state, not a description of verified-compliant current behavior. Treat
accessibility auditing as required work before release, not a "should
already work" assumption.
