# Release Checklist

Use this before tagging any release that goes beyond the developer's own
machine (i.e., before sharing with even a small group of testers). Items
marked **[BLOCKER]** must not be skipped for any release beyond purely
personal/local use.

## Security (see `09-SECURITY-PRIVACY.md`)

- [x] **[BLOCKER]** TLS (`wss://`) enabled between app and agent with
      per-host TOFU certificate pinning (§2 of that doc).
- [x] **[BLOCKER]** Android trust tokens migrated to `EncryptedSharedPreferences`.
- [x] **[BLOCKER]** Windows agent trust tokens persisted with DPAPI-protected
      storage, not lost on every restart.
- [x] Pairing codes expire after a reasonable window (5 minutes, auto-rotated).
- [ ] A persistent visible "device connected" indicator on the PC side —
      currently a live console counter only (`09` §9.1).
- [ ] Reviewed: no tokens, pairing codes, or full typed text ever appear in
      logs (`14-OBSERVABILITY-LOGGING.md` §2–3).

## Functional

- [ ] All manual test scenarios in `11-TESTING-STRATEGY.md` §4 pass.
- [ ] Keyboard, Media, and Power screens are implemented and match
      `02-FEATURE-SPECIFICATION.md` acceptance criteria (if included in this
      release).
- [ ] Shutdown/Restart confirmation dialogs are present and cannot be
      bypassed accidentally.
- [ ] Reconnect flow works after a genuine Wi-Fi drop and after an agent
      restart (re-pairing prompt shown cleanly, no crash).

## Accessibility (see `13-ACCESSIBILITY.md`)

- [ ] Manual TalkBack pass completed on every screen included in this release.
- [ ] Font-scaling test at maximum system font size completed, no clipped text.
- [ ] Contrast checked against WCAG AA for the shipped theme.
- [ ] "D-pad mode" (or equivalent accessible cursor-control alternative)
      available if the Touchpad screen is part of this release.

## Performance (see `12-PERFORMANCE.md`)

- [ ] Mouse-move perceived latency verified < 100ms on a typical home Wi-Fi
      setup during manual testing.
- [ ] Mouse-move message throttling in place (no unthrottled event flood
      from fast drags).

## Deployment (see `15-DEPLOYMENT.md`)

- [ ] Windows agent published as a self-contained single-file executable,
      wrapped in a signed installer.
- [ ] Windows Firewall exception handled by the installer, not left as a
      manual user step.
- [ ] Android app built as a signed release AAB from a securely-stored
      keystore (not the debug keystore).
- [ ] Play Store listing content (if publishing there) accurately reflects
      the no-telemetry/local-only privacy stance.
- [ ] Version numbers for agent and app match/are coordinated for this release.

## Documentation

- [ ] `CHANGELOG.md` updated with this release's changes.
- [ ] `00-README.md` "Project status" section updated to reflect what
      shipped in this release.
- [ ] Any doc marked "planned" for a feature that shipped in this release
      is updated to "Implemented" (`02-FEATURE-SPECIFICATION.md` status column).
- [ ] `17-TROUBLESHOOTING.md` reviewed for any new known issues surfaced
      during this release's testing.

## Post-Release

- [ ] Tag the release in version control matching the coordinated version
      number (§ `15-DEPLOYMENT.md` §4).
- [ ] If using a staged Play Store rollout, monitor crash/ANR rate before
      increasing rollout percentage.
- [ ] Confirm rollback plan (`15-DEPLOYMENT.md` §6) is understood before
      wide release, in case a critical issue surfaces.
