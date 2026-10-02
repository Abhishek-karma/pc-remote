# PC Remote — Windows Architecture

Status: implemented through Phase 4 (service, IPC, session/privilege
separation, UIAccess, secure desktop / lock-logon input). Phases 5–6 (screen
streaming, Android remote-desktop UI) are specified here and not yet built.

## Process model

```
┌────────────────────────── Windows (boot, before logon) ─────────────────┐
│  PCRemoteService.exe   (LocalSystem, session 0, SCM auto-start)         │
│  ├─ WSS control channel :58642 (auth, pairing, mouse/keyboard/power/SAS)│
│  ├─ PairingStore + cert + pcId (DPAPI LocalMachine, ProgramData ACL)    │
│  ├─ SessionManager (WTS notifications, desktop-state detection)         │
│  ├─ InputRouter (allowlisted commands -> correct security boundary)     │
│  ├─ SasController (sas.dll SendSAS for Ctrl+Alt+Del)                    │
│  ├─ IpcServer \\.\pipe\PCRemoteCtl (tray + helpers; privilege-checked)  │
│  └─ UpdateCoordinator (download -> SHA-256 -> Authenticode -> install)  │
│                                                                         │
│  launched per interactive session:                                      │
│  ├─ PCRemoteSession.exe        (user token, session N)                  │
│  │    └─ normal-desktop input injection (SendInput)                     │
│  └─ PCRemoteSession.exe --secure-input (SYSTEM token, session N)        │
│       └─ Winlogon desktop input (UAC / lock / logon)                    │
│                                                                         │
│  optional, user session:                                                │
│  └─ PCRemoteSession.UIA.exe    (user token, uiAccess=true manifest)     │
│       └─ input into elevated windows (UIPI-exempt, signed, Program Files)│
│                                                                         │
│  PCRemoteTray.exe (user, session N) — UI client over IPC only           │
└─────────────────────────────────────────────────────────────────────────┘

  Android app ◄── WSS :58642 (control, JSON v1) ──► PCRemoteService
  (Phase 5 will add a dedicated media transport — frames never ride the JSON
   control channel.)
```

## Privilege boundaries

| Component | Token | Can | Never |
|---|---|---|---|
| PCRemoteService | LocalSystem, session 0 | network, auth, session mgmt, SAS, launch helpers, update | render UI, see user desktop |
| PCRemoteSession | console user | inject into that session's normal desktop | network, persisted state |
| PCRemoteSession --secure-input | SYSTEM in console session | open WinSta0\Winlogon, inject into UAC/lock/logon | network; inject into normal desktop (refused) |
| PCRemoteSession.UIA | console user + UIAccess | inject into elevated windows | run unsigned (OS refuses) |
| PCRemoteTray | console user | display status, request operations via IPC | own pairing/tokens, listen on network |

## Input routing (`InputRouter`)

Desktop state (`SessionManager.DetectState`, input-desktop name + WTS events):

| State | Path |
|---|---|
| `Normal` | session helper (user token) |
| `SecureDesktop` (UAC) | secure-input helper (SYSTEM, Winlogon desktop) |
| `Locked` / `Logon` | secure-input helper |
| SAS (Ctrl+Alt+Del) | `SendSAS` from the service — never simulated keys |

Commands are allowlisted twice: once in `CommandAllowlist` (service, network
side) and once inside the session helper (defense in depth). Unknown types are
rejected with `unknown_type`; the service is not a shell.

## Lock-screen / UAC password entry (requirement 5)

* Keystrokes relayed to the secure-input helper are executed and discarded —
  never logged, buffered, echoed or persisted anywhere in the pipeline.
* No global keyboard hooks exist anywhere in the codebase.
* No custom credential provider. The helper types into the real logon UI on
  the Winlogon desktop, exactly like the physical keyboard would.
* Password text never appears in IPC logs, analytics, pairing history or
  the clipboard.

## Identity & pairing (requirement 10)

* The TLS certificate is generated once and never regenerated for IP changes;
  Android pins its SHA-256 fingerprint (TOFU), so pinning survives DHCP.
* A stable `pcId` GUID (ProgramData\PCRemote\pc-id) is reported in `auth_ok`
  and mDNS TXT; the Android client keys tokens/pins by PC identity, not IP.
* Tokens are owned by the service (DPAPI LocalMachine, ProgramData ACL'd to
  SYSTEM/Administrators). The legacy per-user tray store is migrated once via
  IPC and then re-encrypted under the service.

## IPC (requirement 11)

`\\.\pipe\PCRemoteCtl`, ACL: SYSTEM/Administrators full, Users read/write.
The service classifies each caller (PID via `GetNamedPipeClientProcessId`,
then its token): `Elevated` (SYSTEM/admin) may revoke devices, apply updates
and drive secure input; `Standard` (unelevated tray) may read status, refresh
pairing codes and check for updates. There is no local TCP control surface.

## Updater (requirement 12)

GitHub releases API (`api.github.com`) → download `PC-Remote-Setup.exe` →
verify published SHA-256 sidecar → verify Authenticode via `WinVerifyTrust` →
launch silently; the installer stops the service, replaces binaries and
restarts it. Unverified artifacts are deleted, never executed. CI signs with
signtool when `WINDOWS_CERT_PATH`/`WINDOWS_CERT_PASSWORD` are present and runs
`installer/Validate-Release.ps1`.

## Installer (requirement 8)

`installer/PC-Remote-Setup.iss` (Inno Setup 6) → `installer/build.ps1`:
Program Files install, SCM registration (`sc create`, auto start, failure
recovery), private/domain-profile + localsubnet firewall rules, optional tray
autostart, legacy self-install cleanup (HKCU Run entry + LocalAppData copy),
full upgrade/uninstall.

## Remaining phases (not yet implemented)

* **Phase 5 — screen streaming:** dedicated media transport (raw TCP/UDP with
  framed H.264 NALs, hardware Media Foundation encoder, adaptive bitrate; the
  `stream_request` protocol message is already reserved/accepted). Frames must
  never traverse the JSON control channel.
* **Phase 6 — Android remote-desktop UI:** single connected screen with the
  live remote display as the primary surface; touchpad/keyboard/CAD/fit
  controls as floating overlays; decode with MediaCodec (hardware) fed by the
  media channel.
* **Phase 8 — Windows VM integration matrix:** see `TEST-MATRIX.md`.
