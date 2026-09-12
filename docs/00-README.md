# PC Remote — Documentation

**Project:** Android app that remotely controls a Windows PC (mouse, keyboard,
media, power, and eventually files/screen) over the local network.

This `docs/` folder is the single source of truth for product, design, and
engineering decisions. Code lives in `windows-agent/` (C#/.NET agent) and
`android-app/` (Kotlin/Compose client) alongside this folder.

## How to use these docs

- **New to the project?** Read this file, then `01-PRODUCT-REQUIREMENTS.md`
  and `05-TECHNICAL-ARCHITECTURE.md` in that order.
- **Building a feature?** Check `02-FEATURE-SPECIFICATION.md` for what to
  build, `03-USER-FLOWS.md` for how it should behave step by step, and
  `07-API-SPECIFICATION.md` for the exact wire protocol.
- **Shipping a release?** Follow `15-DEPLOYMENT.md` and
  `16-RELEASE-CHECKLIST.md`.
- **Something broken?** Start with `17-TROUBLESHOOTING.md`.

## Document index

| # | Doc | Purpose |
|---|---|---|
| 00 | README.md | This file — navigation and project status |
| 01 | PRODUCT-REQUIREMENTS.md | Problem statement, goals, non-goals, success metrics |
| 02 | FEATURE-SPECIFICATION.md | Detailed functional requirements per feature |
| 03 | USER-FLOWS.md | Step-by-step flows for every user journey |
| 04 | UI-UX-SPECIFICATION.md | Screens, navigation, visual/interaction design |
| 05 | TECHNICAL-ARCHITECTURE.md | System design, components, tech stack |
| 06 | DATA-MODEL.md | Local storage schemas on both app and agent |
| 07 | API-SPECIFICATION.md | WebSocket message protocol reference |
| 08 | AI-SYSTEM-SPECIFICATION.md | AI/ML scope (none in v1; roadmap notes) |
| 09 | SECURITY-PRIVACY.md | Threat model, pairing/auth, data handling |
| 10 | ERROR-HANDLING.md | Error categories, user messaging, recovery |
| 11 | TESTING-STRATEGY.md | Unit/integration/manual test plans |
| 12 | PERFORMANCE.md | Latency/throughput targets and budgets |
| 13 | ACCESSIBILITY.md | Accessibility requirements and known gaps |
| 14 | OBSERVABILITY-LOGGING.md | What gets logged, where, and retention |
| 15 | DEPLOYMENT.md | Build, signing, and distribution process |
| 16 | RELEASE-CHECKLIST.md | Pre-release verification checklist |
| 17 | TROUBLESHOOTING.md | Common problems and fixes |
| — | CHANGELOG.md | Version history |

## Project status (as of this writing)

**Phase:** Early development (M1/M2 from the roadmap in
`05-TECHNICAL-ARCHITECTURE.md`).

Implemented (both codebases, builds + tests green in CI):
- Windows agent: TLS (`wss://`, self-signed cert, no admin required),
  pairing-code auth with 5-minute expiry + auto-rotation, DPAPI-persisted
  trust tokens, mDNS advertisement, connected-device counter, and
  mouse/keyboard/media/power simulation.
- Android app: mDNS discovery + manual pairing, WSS client with per-host
  TOFU certificate pinning, auto-reconnect with backoff, encrypted token
  storage, touchpad (gestures + D-pad accessible mode), keyboard, media,
  power (confirmed destructive actions), settings, and a foreground session
  service.

Not yet implemented: file transfer, screen mirroring, clipboard sync
(F6–F8 are each their own feature project), a tray app for the agent
(blocking the persistent visual connected indicator), and Compose
Navigation (bottom-nav tabs are a remembered-tab state for now). Each doc
below calls out what's built vs. planned where relevant.

## Ownership & scope note

This is currently a single-target project: **Windows-only PC agent**,
**Android-only client**, **local network (LAN) only** — no cloud relay, no
macOS/Linux agent. Every doc in this set assumes that scope unless it
explicitly says otherwise under a "Future" heading.
