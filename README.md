# PC Remote

Remote-control a Windows PC from an Android phone, on your local network
only. **No cloud, no telemetry, no accounts** — the phone talks directly to
the PC over WSS.

- **Windows agent** (`windows-agent/`) — .NET 8 console app: TLS WebSocket
  server (self-signed cert, no admin rights), pairing codes that rotate
  every 5 minutes, DPAPI-persisted trust tokens, Win32 mouse/keyboard/media/
  power control, mDNS advertisement (`_pc-remote._tcp.local.`).
- **Android app** (`android-app/`) — Kotlin/Jetpack Compose: mDNS discovery +
  manual pairing, WSS with per-host certificate pinning (trust-on-first-use),
  auto-reconnect, touchpad (gestures + D-pad accessible mode), keyboard,
  media, power (confirmed destructive actions), settings.

## Quickstart

```bash
# Windows agent (no admin required, first run creates %AppData%\PcRemoteAgent\)
cd windows-agent
dotnet run

# Android app
cd android-app
./gradlew :app:installDebug   # or open in Android Studio
```

Open the app, tap your PC in "Discover nearby PC", enter the 6-digit pairing
code from the agent console. The firewall needs TCP 58642 (and UDP 5353 for
mDNS) allowed inbound — see `windows-agent/README.md`.

## Documentation

The `docs/` folder is the single source of truth: start at
[`docs/00-README.md`](docs/00-README.md), then
[`02-FEATURE-SPECIFICATION.md`](docs/02-FEATURE-SPECIFICATION.md) (features),
[`07-API-SPECIFICATION.md`](docs/07-API-SPECIFICATION.md) (wire protocol), and
[`09-SECURITY-PRIVACY.md`](docs/09-SECURITY-PRIVACY.md) (trust model).

## Status

Early-development M1/M2, fully building and tested (agent: 17 xUnit tests
incl. a WSS integration test; app: unit + Compose instrumented tests, plus a
live LAN E2E scenario). Security blockers from the release checklist are
closed (WSS + TOFU pinning, encrypted storage on both sides, rotating codes).
Remaining roadmap: clipboard sync / file transfer / screen mirroring
(docs `02` F6–F8), a tray app for the agent, and the CI pipelines being
exercised against this repository.

## License / scope

Personal tool, local-network-only by design. See
[`docs/01-PRODUCT-REQUIREMENTS.md`](docs/01-PRODUCT-REQUIREMENTS.md) for
non-goals.