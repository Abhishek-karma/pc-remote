<div align="center">

# 🖥️ PC Remote — Windows Agent

**High-performance, lightweight background tray server for PC Remote on Windows 10/11.**

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D6?style=flat-square&logo=windows)](https://microsoft.com/windows)
[![WSS](https://img.shields.io/badge/Protocol-TLS%20WebSocket-00E5FF?style=flat-square)](https://en.wikipedia.org/wiki/WebSocket)

Runs cleanly in your Windows system tray, exposing an encrypted WebSocket (WSS) endpoint to simulate mouse, keyboard, media, and power actions via Win32 API.

</div>

---

## ⚡ Overview

The Windows Agent serves as the local host daemon for PC Remote:
- **Zero Configuration Needed**: Generates a self-signed X.509 certificate automatically upon first run—no `HttpListener` or administrator rights (`netsh urlacl`) required.
- **System Tray Integration**: Operates completely in the background with context menu shortcuts for live pairing codes, startup auto-run, and diagnostic logs.
- **Zero-Cloud Discovery**: Uses Multicast DNS (mDNS) broadcasting (`_pc-remote._tcp.local.`) for instant recognition by Android devices on the LAN.
- **Secure by Default**: Encrypts trusted client tokens using Windows Data Protection API (DPAPI) and refreshes active pairing codes every 5 minutes.

---

## 📋 Requirements

- **Operating System**: Windows 10 or Windows 11 (x64 / ARM64)
- **Runtime**:
  - Pre-built binary from GitHub Releases is **fully self-contained** (no .NET installation required).
  - For source builds: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

---

## 🛡️ Windows Firewall Setup & Security Scoping

To allow the Android app to connect and discover the agent over local Wi-Fi, add rules for the WebSocket port and mDNS multicast. **Always scope rules to Private Wi-Fi / Ethernet profiles**:

```powershell
# Inbound TCP for WebSocket TLS (Port 58642, Private profile only)
netsh advfirewall firewall add rule name="PC Remote Agent" dir=in action=allow protocol=TCP localport=58642 profile=private

# Inbound UDP for mDNS Discovery (Port 5353, Private profile only)
netsh advfirewall firewall add rule name="PC Remote Agent mDNS" dir=in action=allow protocol=UDP localport=5353 profile=private
```

---

## 🔒 Hardened Security Controls

- **Cryptographic Pairing**: Codes generated via `RandomNumberGenerator.GetInt32` (single-use, 5-min lifespan).
- **Brute-Force Lockout**: 5 failed pairing attempts triggers an automatic 2-minute IP lockout.
- **Resource Caps**: Limited to 10 total concurrent connections (3 per IP) and 64 KiB WebSocket frame size.
- **RFC 6455 Enforcement**: Rejects unmasked client frames and drops connection to prevent proxy poisoning.
- **Disconnect Button Release**: Automatically fires key-up signals for mouse buttons and modifier keys (`Ctrl`, `Alt`, `Shift`, `Win`) on socket closure.

---

## 🚀 Usage & Features

### Standard Tray Execution
Simply run `PC-Remote-Agent-win-x64.exe` (or `dotnet run` from source).
- A notification balloon displays the initial 6-digit pairing code upon startup.
- The system tray context menu provides:
  - **Live 6-Digit Code**: Displays current code with a one-click "Copy code" action.
  - **Connection Counter**: Real-time connected client status.
  - **Run at Startup**: User-level auto-start toggle (HKCU Run entry; no admin privileges required).
  - **Open Logs Directory**: Direct access to `%AppData%\PcRemoteAgent\logs\`.
  - **Exit**: Cleanly shuts down active listener loops and mDNS advertisements.

### Console Development Mode
For development, real-time logging, and terminal output:
```bash
dotnet run -- --console
# or:
PC-Remote-Agent-win-x64.exe --console
```

---

## ⌨️ Supported Input Actions

| Feature Category | Win32 API Implementation | Supported Commands / Keys |
| :--- | :--- | :--- |
| **Cursor / Trackpad** | `SendInput` (`MOUSEINPUT`) | Relative coordinates (`dx`, `dy`), Left, Right, Middle click, Drag, Wheel scroll |
| **Function Keys** | `keybd_event` (`VkMap`) | `F1`–`F24` with `Ctrl`, `Alt`, `Shift`, `Win` modifiers |
| **Navigation & Edit** | `keybd_event` (`VkMap`) | `Insert`, `Delete`, `Home`, `End`, `Page Up`, `Page Down`, `Print Screen`, `Escape`, `Tab` |
| **Direct Text** | `SendInput` (`KEYEVENTF_UNICODE`) | Full UTF-16 / Unicode text typing |
| **Media Keys** | `keybd_event` (`VK_VOLUME_*`, `VK_MEDIA_*`) | Volume Up/Down, Mute, Play/Pause, Next Track, Previous Track |
| **System Power** | `SetSuspendState`, `ExitWindowsEx`, `LockWorkStation` | Lock, Sleep, Restart, Shutdown |

---

## 🧪 Testing

Run unit tests locally with `dotnet test`:
```bash
dotnet test PcRemoteAgent.Tests/PcRemoteAgent.Tests.csproj
```
