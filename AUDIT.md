# PC Remote — Security & Production Audit Report

**Date**: September 14, 2026  
**Auditor**: Senior Security & Systems Engineer  
**Target Repository**: `https://github.com/Abhishek-karma/pc-remote`  
**Scope**: Windows Agent (`windows-agent/`), Android App (`android-app/`), Protocol Specification, CI/CD Workflows (`.github/`), Documentation  

---

## 1. System Architecture Summary

PC Remote is a zero-cloud, local-network (LAN) remote control solution pairing an Android client application (Kotlin/Jetpack Compose) with a Windows desktop background agent (C#/.NET 8 WinForms/Tray host).

* **Discovery**: UDP Multicast DNS (mDNS) via `Makaretu.Dns.Multicast` (agent) and Android `NsdManager` (app) advertising `_pcremote._tcp.local.` on port `58642`.
* **Transport**: Custom WebSocket-over-TLS (RFC 6455 over `SslStream`) on TCP port `58642` with self-signed X.509 certificate generation.
* **Authentication & Trust**: Trust-On-First-Use (TOFU) certificate fingerprint pinning on Android via `PinningTrustManager`. Authentication uses a 6-digit numeric pairing code rotated every 5 minutes, generating persistent 128-bit hex tokens stored via Windows DPAPI and Android `EncryptedSharedPreferences`.
* **Input Emulation**: Win32 `SendInput` API for relative mouse movement, button clicks, wheel scroll, virtual keystrokes, Unicode text injection, media controls, and system power commands (`SetSuspendState`, `LockWorkStation`, `InitiateSystemShutdownEx`).

---

## 2. Risk & Vulnerability Matrix

| ID | Component | Issue Description | Severity | Target File(s) | Recommended Mitigation |
|:---|:---|:---|:---:|:---|:---|
| **SEC-01** | Windows Agent | Non-cryptographic PRNG (`Random.Shared`) used for authentication pairing codes. | **P0** | `windows-agent/PairingStore.cs:44` | Replace with `RandomNumberGenerator.GetInt32(0, 1_000_000)`. |
| **SEC-02** | Windows Agent | Unmasked client WebSocket frames accepted without RFC 6455 enforcement. | **P0** | `windows-agent/WebSocketConnection.cs:139` | Require client frames to be masked (RFC 6455 §5.1); close connection if unmasked. |
| **SEC-03** | Windows Agent | No per-IP rate-limiting or lockout on failed pairing authentication attempts (brute-force exposure). | **P0** | `windows-agent/PairingStore.cs:64` | Track per-IP authentication failures and lock out IPs for 2 minutes after 5 consecutive failures. |
| **SEC-04** | Windows Agent | Abrupt network disconnects or socket errors leave mouse buttons logically pressed or key states stuck. | **P0** | `windows-agent/Program.cs:389` | Call `Win32Input.ReleaseAllButtons()` and clear modifier key states in connection cleanup `finally` block. |
| **SEC-05** | Windows Agent | Bounded resource limits missing for concurrent connections and payload sizes. | **P1** | `windows-agent/Program.cs:177`, `WebSocketConnection.cs:155` | Limit total concurrent connections (10 max, 3 per IP), cap WebSocket payloads at 64 KiB, and enforce 10s handshake / 15s auth timeouts. |
| **SEC-06** | Windows Agent | Token store persistence lacks atomic write guarantees (potential file corruption on crash). | **P1** | `windows-agent/PairingStore.cs:118` | Implement atomic file replacements (write to temp file then replace target file). |
| **SEC-07** | Protocol | Lack of explicit protocol versioning, request tracking IDs, and command execution ACKs. | **P1** | `windows-agent/Program.cs:330` | Add `version` (1) and `requestId` to `RemoteMessage`; return structured `ack` or `error_code` responses. |
| **REL-01** | Windows Agent | Unhandled socket exceptions in `Makaretu.Dns.Multicast` on network interface state changes. | **P1** | `windows-agent/MdnsAdvertiser.cs:25` | Wrap mDNS advertiser events in defensive try-catch handlers and fallback to manual IP entry gracefully. |
| **CICD-01**| CI / Build | Workflow status badges in `README.md` point to missing `android-ci.yml`. | **P2** | `README.md:9` | Update badge URLs to point to `android-app-ci.yml`. |
| **CICD-02**| CI / Build | Single-file publish and executable smoke tests are skipped on Pull Requests. | **P2** | `.github/workflows/windows-agent-ci.yml:33` | Remove `if` restrictions so single-file packaging is validated on all pull requests. |
| **CICD-03**| CI / Build | Missing build dependency caching (`gradle`, `nuget`) across workflows. | **P2** | `.github/workflows/*.yml` | Add `cache: 'gradle'` and `cache: 'nuget'` steps to GitHub Actions workflows. |
| **DOC-01** | Documentation | Referenced `docs/` specification directory missing; security/firewall docs incomplete. | **P3** | `README.md`, `windows-agent/README.md` | Rewrite `README.md` files and create dedicated `SECURITY.md`. |

---

## 3. Detailed Technical Findings

### P0-1: Non-Cryptographic Pairing Generator (`SEC-01`)
* **Impact**: `Random.Shared` in `PairingStore.cs` uses a linear congruential / xoshiro pseudo-random algorithm. Attackers on the local network can predict generated 6-digit pairing codes with minimal state observations.
* **Fix**: Use `System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6")`.

### P0-2: Unmasked Client Frame Processing (`SEC-02`)
* **Impact**: RFC 6455 Section 5.1 mandates that clients MUST mask all frames sent to the server. The current custom WebSocket parser reads unmasked frames without rejecting them, violating the protocol spec and allowing proxy cache poisoning vectors on local networks.
* **Fix**: Check `bool masked = (header[1] & 0x80) != 0;` and immediately drop the connection if `masked == false`.

### P0-3: Absence of Authentication Rate Limiting (`SEC-03`)
* **Impact**: A 6-digit numeric pairing code has only 1,000,000 combinations. Without rate-limiting, an attacker can attempt thousands of pairing attempts per second over TCP to gain access to the host PC within seconds.
* **Fix**: Implement a per-IP `FailureRecord` in `PairingStore.cs` tracking failed attempts. Lock out any IP exceeding 5 failures for 120 seconds.

### P0-4: Stuck Key & Mouse Drag State on Network Drop (`SEC-04`)
* **Impact**: If an Android phone loses Wi-Fi while sending a `mouse_click` `down` or `key_press` command, the Windows agent left the Win32 button/modifier in a down state, locking mouse input on the host PC.
* **Fix**: In `Program.cs` connection cleanup, call `Win32Input.ReleaseAllButtons()` and reset held modifier keys.

---

## 4. Remediation Plan

1. **Phase 2 (Security Hardening)**: Update `PairingStore.cs`, `WebSocketConnection.cs`, and `Program.cs` with secure PRNG, rate limiting, connection limits, and input clamping.
2. **Phase 3 (Protocol & ACKs)**: Add `version` and `requestId` to protocol messages; send command ACKs and structured error responses.
3. **Phase 4 (Testing)**: Implement comprehensive unit tests in `PcRemoteAgent.Tests/HardeningTests.cs`.
4. **Phase 5 (CI/CD)**: Update workflow YAML files and README badge links.
5. **Phase 6 (Documentation)**: Create `SECURITY.md` and rewrite root `README.md` and `windows-agent/README.md`.
