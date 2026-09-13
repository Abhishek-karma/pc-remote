<div align="center">

# 📱 PC Remote — Android App

**Modern, dark-first Jetpack Compose client for zero-cloud PC control over local Wi-Fi.**

[![Android](https://img.shields.io/badge/Platform-Android%208.0%2B%20(API%2026%2B)-3DDC84?style=flat-square&logo=android)](https://developer.android.com)
[![Kotlin](https://img.shields.io/badge/Language-Kotlin%202.0%2B-7F52FF?style=flat-square&logo=kotlin)](https://kotlinlang.org/)
[![Jetpack Compose](https://img.shields.io/badge/UI-Jetpack%20Compose%20M3-4285F4?style=flat-square&logo=jetpackcompose)](https://developer.android.com/jetpack/compose)
[![WSS](https://img.shields.io/badge/Security-TOFU%20Pinning%20%2F%20TLS-00E5FF?style=flat-square)](https://en.wikipedia.org/wiki/Trust_on_first_use)

Control your Windows PC's cursor, full keyboard with F1–F24 decks, media playback, and system power with ultra-low latency and zero cloud reliance.

</div>

---

## 🚀 Overview

The PC Remote Android client delivers an intuitive, tactile interface engineered for low-latency command streaming over local Wi-Fi:
- **Dark-First Technical Aesthetic**: High-contrast obsidian canvas (`#0A0E17`), electric cyan highlights (`#00E5FF`), and crisp typography designed for low-light PC companion use.
- **Hardware-Accelerated Input**: Touchpad gesture engine with pointer acceleration, natural scroll smoothing, and accessible D-pad cursor stepping.
- **Full Keyboard Deck**: Clustered F1–F24 function keys, quick modifier combos (`Alt+F4`, `Ctrl+F5`, `Shift+F10`), sticky modifier latches, and direct Unicode typing.
- **Rock-Solid Connectivity**: Automatic mDNS service discovery, exponential backoff reconnect policy with smart timeout ceilings, and background foreground service support.
- **Zero-Cloud Local Security**: Trust-On-First-Use (TOFU) SHA-256 certificate pinning over encrypted WebSockets (`wss://`).

---

## 📱 Screens & Capabilities

### 🖱️ Touchpad Screen (`ui/TouchpadScreen.kt`)
- **Fluid Gesture Surface**: Multi-touch canvas supporting 1-finger move, 1-finger tap (left click), 2-finger tap (right click), 2-finger scroll, and tap-and-drag.
- **Precision Visuals**: Integrated crosshair reticle, corner alignment brackets, and subtle coordinate feedback overlay.
- **Accessible D-Pad Mode**: Switchable direction-pad layout with discrete cursor stepping buttons, middle click, and dedicated left/right mouse triggers.

### ⌨️ Keyboard & Function Key Deck (`ui/KeyboardScreen.kt`)
- **Full Function Array**: Complete `F1`–`F12` (and extended `F13`–`F24`) function keys arranged in ergonomic 4×3 grouped or 6×2 compact layouts.
- **Quick Combo Triggers**: Instant one-tap shortcuts for common tasks:
  - `Alt + F4` (Close window)
  - `Ctrl + F5` (Hard refresh / Run)
  - `Shift + F10` (Context menu)
  - `F11` (Toggle fullscreen)
- **Modifier Latching**: Sticky state toggles for `Ctrl`, `Alt`, `Shift`, and `Win` keys.
- **Navigation & Editing Bar**: Dedicated keys for `Esc`, `Tab`, `Insert`, `Delete`, `Home`, `End`, `Page Up`, `Page Down`, `Print Screen`, and `Enter`.
- **Direct Unicode Typing Field**: Real-time transmission of text and symbols to the active PC window.

### 🎵 Media Controller (`ui/MediaScreen.kt`)
- **Hero Transport Controls**: Play / Pause, Stop, Previous Track, and Next Track.
- **Hold-to-Repeat Volume**: Long-press volume buttons for continuous smooth adjustment.
- **Fine Scrubbing**: Dedicated ±10 second skip buttons for video and audio playback.
- **Quick Mute Toggle**: Instant single-tap system volume muting.

### ⚡ System Power Hub (`ui/PowerScreen.kt`)
- **System Actions**: Lock Workstation, Sleep, Restart, and Shutdown.
- **Safety Confirmation Interlocks**: Material 3 alert dialogs with clear warning semantics prevent accidental shutdowns during remote sessions.

### ⚙️ Settings & Device Management (`ui/SettingsScreen.kt`)
- **Paired PC Profiles**: Manage remembered hosts with one-tap disconnect, reconnect, and certificate unpinning (forget).
- **Haptic & Sensitivity Sliders**: Fine-tune cursor speed, pointer acceleration curve, scroll speed, and haptic feedback strength.

---

## 🏛️ Project Architecture

```
android-app/app/src/main/java/com/example/pcremote/
├── MainActivity.kt                  # Root activity & navigation hub
├── network/
│   ├── RemoteConnection.kt          # OkHttp WSS client, pairing handshake, and message dispatcher
│   ├── DiscoveryService.kt          # Android NsdManager mDNS discovery service
│   ├── ReconnectPolicy.kt           # Exponential backoff reconnect engine with 5-minute ceiling
│   └── models/                      # Type-safe protocol data models & payloads
├── service/
│   └── ConnectionForegroundService.kt # Android Foreground Service for persistent background sessions
└── ui/
    ├── PairingScreen.kt             # PC discovery list & manual IP/PIN connection flow
    ├── TouchpadScreen.kt            # Gesture surface, crosshair reticle, & D-pad layout
    ├── KeyboardScreen.kt            # F1-F24 function keys, modifier latching, & text input
    ├── MediaScreen.kt               # Media transport & hold-to-repeat volume controls
    ├── PowerScreen.kt               # Confirmed power management actions
    ├── SettingsScreen.kt            # Sensitivity sliders, paired devices, & app info
    ├── components/                  # Reusable connection banners, PC cards, and top bars
    └── theme/                       # Modern M3 technical color scheme & typography
```

---

## 🔒 Permissions & Security Model

The app adheres to least-privilege security principles:

```xml
<!-- Network & Discovery -->
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE" />

<!-- Background Session Persistence -->
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_DATA_SYNC" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />

<!-- Tactile Feedback -->
<uses-permission android:name="android.permission.VIBRATE" />
```

- **Cleartext Traffic Disabled**: The app strictly rejects unencrypted `ws://` connections (`android:usesCleartextTraffic="false"`).
- **TOFU Pinning**: On initial pairing, the SHA-256 fingerprint of the PC's TLS certificate is saved. Subsequent connections verify this fingerprint, protecting against local network MITM attacks.

---

## 🛠️ Building & Testing

### Prerequisites
- **JDK**: Java 17 or higher
- **Android SDK**: Android 14 / API 35 SDK
- **Gradle**: 8.9+ (configured via `gradlew`)

### Build Debug APK
```bash
cd android-app
./gradlew assembleDebug
```

### Run Unit Tests
```bash
./gradlew testDebugUnitTest
```

### Install to Device
```bash
./gradlew installDebug
```

---

## 📄 License

This project is licensed under the [MIT License](../LICENSE).
