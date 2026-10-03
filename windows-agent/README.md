<div align="center">

# 🖥️ PC Remote — Windows Agent (0.2.1)

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
| `PCRemoteService.exe` | LocalSystem (session 0) | WSS control channel :58642, auth/pairing, session detection, SAS |
| `PCRemoteSession.exe` | logged-on user | normal desktop input injection |
| `PCRemoteSession.exe --secure-input` | SYSTEM in console session | UAC / lock-screen / logon desktop input |
| `PCRemoteTray.exe` | logged-on user | status + pairing code UI — talks to the service over authenticated IPC |

Three binaries, two privilege levels. Elevated windows are **not** reachable
(Windows UIPI blocks lower-integrity input; there is no UIAccess helper — an
unsigned `uiAccess` binary cannot be launched at all).

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full design and privilege
boundaries, and [TEST-MATRIX.md](TEST-MATRIX.md) for the validation matrix.

## 🛠️ Building

```powershell
dotnet build PcRemoteAgent.slnx
dotnet test src/PcRemote.Tests/PcRemote.Tests.csproj
# Installer (requires Inno Setup 6):
./installer/build.ps1          # → dist/PC-Remote-Setup.exe (+ .sha256)
```

To test a local build against an existing install, run
`deploy\deploy-update.ps1` (self-elevates) instead of building an installer.

## 🛡️ Security notes

- WSS-only (TLS mandatory, no plaintext fallback), trust-on-first-use
  certificate pinning with a **stable** PC identity — IP/DHCP changes never
  force re-pairing.
- Firewall rules are private/domain profile, local-subnet scoped, never Public.
- Remote keystrokes are executed and discarded — never logged, stored or
  analyzed. No global keyboard hooks. No custom credential provider.
- There is no in-app updater: an update is just running the new installer over
  the old one, so there is no downloaded-artifact execution path to harden.

