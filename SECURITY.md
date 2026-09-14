# Security Policy & Architecture Guide

## 1. Security Architecture & Threat Model

PC Remote is designed as a **zero-cloud, local-network-only (LAN)** remote control application connecting Android devices to Windows PCs.

### Trust Boundaries
1. **Network**: Operates over WebSocket-over-TLS (WSS) on TCP port `58642`. The local area network (LAN) is treated as un-trusted; all traffic is encrypted end-to-end using TLS.
2. **Identity & Pinning**: Post-Authentication Trust-On-First-Use (TOFU) certificate pinning. The Android client inspects the SHA-256 fingerprint of the Windows agent's self-signed X.509 certificate and persists it only after successful pairing authentication (`auth_ok`), preventing unauthenticated certificate poisoning on untrusted networks. Subsequent connections strictly verify the pinned certificate fingerprint.
3. **Authentication**: 
   - 6-digit numeric pairing code generated using `System.Security.Cryptography.RandomNumberGenerator`.
   - Pairing codes auto-expire after 5 minutes and are single-use.
   - Successful pairing issues a 128-bit persistent trust token protected via Windows DPAPI on the PC and `EncryptedSharedPreferences` on Android.

---

## 2. Threat Mitigations & Defensive Features

### Rate Limiting & Brute-Force Lockout
- **Per-IP Failure Tracking**: Exceeding 5 failed authentication attempts from a single IP address triggers a mandatory **2-minute lockout window** for that IP.
- **Single-Use Pairing Code**: Pairing codes are invalidated immediately upon successful pairing to prevent replay attacks.

### Resource Bounding & Connection Limits
- **Connection Caps**: Maximum 10 total concurrent connections; maximum 3 connections per IP address.
- **Payload Limits**: Max WebSocket frame payload is strictly capped at **64 KiB** to prevent memory allocation denial-of-service.
- **RFC 6455 Compliance**: All client frames received by the server MUST be masked. Unmasked client frames trigger immediate connection closure.
- **UTF-8 Resilience**: Strict UTF-8 validation with clean fallback handling to prevent unhandled decoder exceptions.

### Win32 Input Safety & Automatic Key Cleanup
- **Input Parameter Clamping**: Mouse relative deltas (`dx`, `dy`) are bounded to `[-4096, 4096]`. Text inputs are truncated to 1,000 characters.
- **Command Whitelisting**: Media controls and power options (`sleep`, `lock`, `shutdown`, `restart`) are whitelisted against hardcoded commands.
- **Automatic Disconnect Release**: In the event of network disruption or abrupt socket disconnect, the agent fires key-up signals for mouse buttons (left, right, middle) and modifier keys (`Ctrl`, `Alt`, `Shift`, `Win`) to prevent host keyboard/mouse lockup.

---

## 3. Firewall Scoping Guidance

When creating Windows Firewall rules for PC Remote:
- **Scope to Private Networks**: Always restrict firewall rules to **Private Wi-Fi / Ethernet profiles**. Do NOT enable incoming access on Public Wi-Fi profiles.
- **Specific Executable & Port**: Bound incoming rules strictly to `PC-Remote-Agent.exe` on TCP port `58642` and UDP port `5353` (mDNS).

---

## 4. Reporting Vulnerabilities

If you discover a potential security vulnerability in PC Remote:
1. Contact the security team privately via email or GitHub Security Advisories.
2. Provide details of the issue, affected versions, and reproduction steps.
3. Please do not publicly disclose vulnerabilities until a fix has been released.
