<div align="center">

# 🖥️ PC Remote — Windows Agent (0.2.0)

**Service-based Windows remote-control host for Windows 10/11.**

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D6?style=flat-square&logo=windows)](https://microsoft.com/windows)
[![WSS](https://img.shields.io/badge/Protocol-TLS%20WebSocket-00E5FF?style=flat-square)](https://en.wikipedia.org/wiki/WebSocket)

Install once (`PC-Remote-Setup.exe`) and the PC is always reachable: a
LocalSystem Windows service owns the encrypted WebSocket endpoint, pairing
state and session management — surviving boot, logoff, lock and tray crashes.

</div>

---

## ⚡ Architecture at a glance

| Component | Token | Role |
|---|---|---|
| `PCRemoteService.exe` | LocalSystem (session 0) | WSS control channel :58642, auth/pairing, session detection, SAS, updates |
| `PCRemoteSession.exe` | logged-on user | normal desktop input injection |
| `PCRemoteSession.exe --secure-input` | SYSTEM in console session | UAC / lock-screen / logon desktop input |
| `PCRemoteSession.UIA.exe` | user + uiAccess manifest | elevated-window input (signed builds) |
| `PCRemoteTray.exe` | logged-on user | status UI only — talks to the service over authenticated IPC |

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full design and privilege
boundaries, and [TEST-MATRIX.md](TEST-MATRIX.md) for the validation matrix.

## 🛠️ Building

```powershell
dotnet build PcRemoteAgent.slnx
dotnet test src/PcRemote.Tests/PcRemote.Tests.csproj
# Installer (requires Inno Setup 6):
./installer/build.ps1          # → dist/PC-Remote-Setup.exe (+ .sha256)
```

## 🛡️ Security notes

- WSS-only (TLS mandatory, no plaintext fallback), trust-on-first-use
  certificate pinning with a **stable** PC identity — IP/DHCP changes never
  force re-pairing.
- Firewall rules are private/domain profile, local-subnet scoped, never Public.
- Remote keystrokes are executed and discarded — never logged, stored or
  analyzed. No global keyboard hooks. No custom credential provider.
- Updates verify SHA-256 **and** Authenticode before anything is executed.

