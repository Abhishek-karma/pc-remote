# PC Remote

Remote control for your Windows PC from an Android device. Designed for the
local network: **no cloud, no accounts, no telemetry** — the phone connects
directly to the PC over an encrypted WebSocket (WSS) connection.

## Components

**Windows agent** (`windows-agent/`) — .NET 8 application that

- serves a TLS WebSocket endpoint with a self-signed certificate (no
  administrator rights required),
- pairs devices with 6-digit codes that rotate every five minutes,
- persists trusted devices using DPAPI encryption,
- controls mouse, keyboard, media, and power via the Win32 API,
- advertises itself via mDNS (`_pc-remote._tcp.local.`) for automatic
  discovery.

**Android app** (`android-app/`) — Kotlin / Jetpack Compose client that

- discovers PCs via mDNS, with manual IP entry as a fallback,
- pins the agent's certificate on first pairing (trust-on-first-use),
- reconnects automatically after interruptions,
- provides a touchpad (gesture and accessible D-pad modes), keyboard, media,
  and power controls — destructive power actions require confirmation.

## Requirements

- Windows 10/11 for the agent (the published build is self-contained; no
  .NET runtime installation is needed)
- Android 8.0+ (API 26) for the app

## Installation

Download `PC-Remote-Agent-win-x64.exe` and `PC-Remote-Android.apk` from the
[latest GitHub Release](../../releases) and verify them against
`SHA256SUMS.txt`. Building from source:

```bash
# Windows agent
cd windows-agent
dotnet run

# Android app
cd android-app
./gradlew :app:installDebug   # or open in Android Studio
```

## Getting connected

1. Run the agent. Allow **TCP 58642** (WebSocket) and **UDP 5353** (mDNS)
   through the Windows Firewall — see `windows-agent/README.md` for the
   manual rules.
2. Open the app and tap your PC under "Discover nearby PC".
3. Enter the 6-digit pairing code shown in the agent console (also written
   to its log file).

## Releases

Pushing a `v*` tag (e.g. `v1.0.0`) triggers a GitHub Actions workflow that
builds and tests both platforms, smoke-tests the agent, and attaches the
Windows executable and Android APK with SHA-256 checksums to a GitHub
Release. The Windows build is self-contained (no .NET runtime required);
the Android APK is signed when signing secrets are configured and clearly
named unsigned otherwise.

## Documentation

Detailed design and specification documents (product requirements, feature
and API specs, security model, deployment pipeline, and so on) are
maintained locally and intentionally kept out of this repository.

## Scope

A personal tool, intentionally limited to the local network: no cloud relay,
no remote access over the internet, no telemetry, no accounts.
