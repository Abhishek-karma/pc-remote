<div align="center">

# ⚡ PC Remote

**Ultra-low latency, zero-cloud remote control for Windows PCs from Android.**

[![Release](https://img.shields.io/badge/release-v0.1.2-00E5FF?style=for-the-badge)](https://github.com/Abhishek-karma/pc-remote/releases)
[![License](https://img.shields.io/github/license/Abhishek-karma/pc-remote?style=for-the-badge&color=gray)](LICENSE)
[![Android CI](https://img.shields.io/github/actions/workflow/status/Abhishek-karma/pc-remote/android-app-ci.yml?branch=main&label=Android%20CI&style=for-the-badge)](https://github.com/Abhishek-karma/pc-remote/actions)
[![Windows CI](https://img.shields.io/github/actions/workflow/status/Abhishek-karma/pc-remote/windows-agent-ci.yml?branch=main&label=Windows%20CI&style=for-the-badge)](https://github.com/Abhishek-karma/pc-remote/actions)

Control your cursor, launch shortcuts, type Unicode text, manage media, and trigger system power actions directly over local Wi-Fi.

[Features](#-key-features) • [Architecture](#-architecture--security) • [Quick Start](#-quick-start) • [Installation](#-installation) • [Protocol](#-protocol-specification) • [Security](#-security--privacy-statement) • [Building](#-building-from-source)

</div>

---

## 🚀 Key Features

### 🖱️ Precision Trackpad & Navigation
- **Ultra-Responsive Touchpad**: Fluid cursor tracking with configurable pointer acceleration, sensitivity, and natural scrolling.
- **Gesture Engine**: Single-tap left-click, two-finger right-click, two-finger vertical scrolling, and tap-and-drag window movement.
- **Crosshair Reticle & Precision Overlays**: Subtle visual tracking reticle and alignment brackets for exact cursor positioning.
- **Accessible D-Pad Mode**: Discrete cursor stepping and dedicated button triggers for high-precision navigation.

### ⌨️ Comprehensive Keyboard & Function Deck
- **Full F1–F24 Function Key Array**: 4×3 clustered function keypads with quick modifier combination triggers (`Alt+F4`, `Ctrl+F5`, `Shift+F10`, `F11`).
- **Modifier Latching**: Real-time latching for `Ctrl`, `Alt`, `Shift`, and `Win` modifiers.
- **Editing & Navigation Keys**: `Esc`, `Tab`, `Insert`, `Delete`, `Home`, `End`, `Page Up`, `Page Down`, and `Print Screen`.
- **Direct Unicode Typing**: Fast text transmission directly to the active PC window.

### 🎵 Media & Volume Control Hub
- **Transport Controls**: Play/Pause, Next Track, Previous Track, Stop.
- **Hold-to-Repeat Volume**: Continuous smooth volume increment/decrement, one-tap mute toggling, and fast 10s scrub skip buttons.

### ⚡ Power Management with Confirmation Guards
- **Power Actions**: Lock Workstation, Sleep, Restart, and Shutdown.
- **Safety Interlocks**: Double-confirmation dialogs prevent accidental power triggers during active remote sessions.

### 🔒 Zero-Cloud Privacy & Local Security
- **No Cloud, No Telemetry, No Accounts**: All traffic is strictly confined to your local Wi-Fi / LAN.
- **Encrypted WebSocket (WSS)**: End-to-end TLS encryption with per-machine generated certificates.
- **Trust-On-First-Use (TOFU) Certificate Pinning**: The Android client validates and pins the PC's unique SHA-256 certificate fingerprint on first pair.
- **Rotating 6-Digit Codes & DPAPI Encryption**: Pairing codes refresh every 5 minutes; persistent trust tokens are encrypted at rest using Windows DPAPI.
- **Auto-Discovery via mDNS**: Seamlessly discover and connect to PC agents broadcasting on `_pc-remote._tcp.local.`.

---

## 🏛️ Architecture & Security

```
┌─────────────────────────┐               Encrypted WSS                ┌─────────────────────────┐
│       Android App       │ ══════════════════════════════════════════> │      Windows Agent      │
│  (Jetpack Compose M3)   │         (Port 58642 / Local LAN)           │   (.NET 8 Tray Host)    │
└─────────────────────────┘                                            └─────────────────────────┘
             │                                                                      │
             ├─ mDNS Discovery (NsdManager) ────── UDP 5353 ───────────────────────┤─ mDNS Broadcaster
             ├─ Certificate Pinning (TOFU)  ────── SHA-256 Fingerprint ────────────┤─ Self-Signed TLS X.509
             ├─ 6-Digit Rotating Auth       ────── Token Verification ─────────────┤─ DPAPI Token Store
             └─ Haptic Touchpad & Key Deck  ────── JSON Control Payloads ──────────┘─ Win32 SendInput API
```

---

## 📦 Installation

Pre-built binaries and APK packages are available for every release:

1. Download the latest assets from **[GitHub Releases](https://github.com/Abhishek-karma/pc-remote/releases)**:
   - **Windows Agent**: `PC-Remote-Agent-win-x64.exe` (Self-contained, no .NET install required).
   - **Android App**: `PC-Remote-Android.apk`.
2. *(Optional)* Verify package integrity using the provided `SHA256SUMS.txt`.

---

## 🏁 Quick Start

### 1. Start the Windows Agent
Run `PC-Remote-Agent-win-x64.exe`. The agent starts quietly in your Windows system tray.

> **Firewall Setup (First Run)**:
> Ensure ports are allowed through Windows Firewall:
> ```powershell
> netsh advfirewall firewall add rule name="PC Remote Agent" dir=in action=allow protocol=TCP localport=58642
> netsh advfirewall firewall add rule name="PC Remote Agent mDNS" dir=in action=allow protocol=UDP localport=5353
> ```

### 2. Connect from Android
1. Open the **PC Remote** app on your Android phone connected to the same Wi-Fi network.
2. Select your PC from the **Discover nearby PC** list (or enter your local IP manually).
3. Check the 6-digit pairing code shown in the Windows tray balloon notification or tray context menu.
4. Enter the code to pair and begin controlling your PC!

---

## 🛠️ Building from Source

### Prerequisites
- **Windows Agent**: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Android App**: Android Studio Ladybug+ / JDK 17 / Android SDK (API 35)

### Build Windows Agent
```bash
cd windows-agent
dotnet restore
dotnet build -c Release
dotnet test PcRemoteAgent.Tests/PcRemoteAgent.Tests.csproj

# Run in development console mode:
dotnet run -- --console
```

### Build Android App
```bash
cd android-app
./gradlew testDebugUnitTest
./gradlew assembleRelease
```

---

## 📡 Protocol Specification

The agent exposes a hardened WebSocket endpoint (`wss://<IP>:58642`). Messages are exchanged as lightweight JSON frames with protocol versioning and request IDs:

### Authentication & Handshake
```json
// Android -> PC (Pairing / Auth Attempt)
{
  "version": 1,
  "requestId": "req-001",
  "type": "auth",
  "pairingCode": "123456",
  "token": null
}

// PC -> Android (Auth Success)
{
  "version": 1,
  "requestId": "req-001",
  "type": "auth_ok",
  "token": "dpapi_secured_token_value",
  "pcName": "DESKTOP-ALPHA"
}

// PC -> Android (Auth Failed / Rate Limited)
{
  "version": 1,
  "requestId": "req-001",
  "type": "auth_failed",
  "errorCode": "rate_limited" // "invalid_credentials" | "rate_limited"
}
```

### Control & Input Simulation Frames
```json
// Relative Cursor Movement (clamped to [-4096, 4096])
{ "version": 1, "requestId": "req-002", "type": "mouse_move", "dx": -14, "dy": 8 }

// Mouse Click / Drag
{ "version": 1, "requestId": "req-003", "type": "mouse_click", "button": "left", "action": "down" } // "down" | "up" | "click"

// Function & Navigation Keypress
{ "version": 1, "requestId": "req-004", "type": "key_press", "key": "F5", "modifiers": ["CTRL"] }

// Unicode Text Typing (max 1,000 characters)
{ "version": 1, "requestId": "req-005", "type": "text_input", "text": "Hello, World! 🚀" }

// Media Action
{ "version": 1, "requestId": "req-006", "type": "media_control", "action": "play_pause" } // "vol_up" | "vol_down" | "mute" | "next" | "prev"

// Power Management
{ "version": 1, "requestId": "req-007", "type": "system_power", "action": "lock" } // "sleep" | "restart" | "shutdown"

// Structured Execution Response (PC -> Android)
{ "version": 1, "requestId": "req-002", "type": "command_result", "success": true }
```

---

## 🔒 Security & Privacy Statement

- **Strict Local Scope**: All communications take place strictly between the Android device and Windows PC on the local area network.
- **Zero Cloud**: No remote servers, no STUN/TURN relays, no diagnostic telemetry, and no accounts required.
- **Hardened Security Policy**: Detailed in [SECURITY.md](SECURITY.md) and audited in [AUDIT.md](AUDIT.md). Includes cryptographic RNG pairing codes, per-IP rate-limiting/lockout, 64 KiB frame caps, RFC 6455 frame masking enforcement, and automatic key/button release on disconnect.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
