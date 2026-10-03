# PC Remote — Windows Architecture

Status: service, IPC, session/privilege separation and secure desktop /
lock-logon input. Screen streaming and the Android remote-desktop view are not
built (see "Not implemented" at the end).

## Process model

```
┌────────────────────────── Windows (boot, before logon) ─────────────────┐
│  PCRemoteService.exe   (LocalSystem, session 0, SCM auto-start)         │
│  ├─ WSS control channel :58642 (auth, pairing, mouse/keyboard/power/SAS)│
│  ├─ PairingStore + cert + pcId (DPAPI LocalMachine, ProgramData ACL)    │
│  ├─ SessionManager (WTS notifications, desktop-state detection)         │
│  ├─ InputRouter (allowlisted commands -> correct security boundary)     │
│  ├─ SasController (sas.dll SendSAS for Ctrl+Alt+Del)                    │
│  └─ IpcServer \\.\pipe\PCRemoteCtl (tray + helpers; privilege-checked)  │
│                                                                         │
│  launched per interactive session:                                      │
│  ├─ PCRemoteSession.exe              (user token, session N)            │
│  │    └─ normal-desktop input injection (SendInput)                     │
│  └─ PCRemoteSession.exe --secure-input (SYSTEM token, session N)        │
│       └─ Winlogon desktop input (UAC / lock / logon)                    │
│                                                                         │
│  PCRemoteTray.exe (user, session N) — status + pairing code over IPC    │
└─────────────────────────────────────────────────────────────────────────┘

  Android app ◄── WSS :58642 (control, JSON v1) ──► PCRemoteService
```

## Privilege boundaries

| Component | Token | Can | Never |
|---|---|---|---|
| PCRemoteService | LocalSystem, session 0 | network, auth, session mgmt, SAS, launch helpers | render UI, see user desktop |
| PCRemoteSession | console user | inject into that session's normal desktop | network, persisted state |
| PCRemoteSession --secure-input | SYSTEM in console session | open WinSta0\Winlogon, inject into UAC/lock/logon | network; inject into normal desktop (refused) |
| PCRemoteTray | console user | display status, revoke devices, open logs | own pairing/tokens, listen on network |

**Elevated windows are not supported.** Windows' UIPI blocks input from a
lower-integrity process, and the previous `uiAccess=true` helper could not run
at all: Windows refuses to launch an unsigned UIAccess binary, so it only ever
existed as a second build to sign and ship. It was removed rather than left as
dead surface.

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

The secure helper attaches to the Winlogon desktop with `SecureDesktop`
(`OpenInputDesktop` + `SetThreadDesktop`). That call only succeeds with
`DESKTOP_SWITCHDESKTOP` in the requested access mask — see `SecureDesktop.cs`
for why that detail decides whether lock-screen input works at all.

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
then its token): `Elevated` (SYSTEM/admin) may revoke devices, mint pairing
codes and drive secure input; `Standard` (unelevated tray) may read status
only — which is why the tray shows "(run as administrator)" instead of the
pairing code. There is no local TCP control surface.

## Updates

There is no in-app updater. Upgrades are a normal install: run the new
`PC-Remote-Setup.exe` over the top — it stops the service, replaces the
binaries, re-registers the service and starts it again. Release artifacts
ship with SHA-256 checksums; CI signs the staged exes with signtool when
`WINDOWS_CERT_PATH`/`WINDOWS_CERT_PASSWORD` are present and runs
`installer/Validate-Release.ps1`.

## Installer (requirement 8)

`installer/PC-Remote-Setup.iss` (Inno Setup 6) → `installer/build.ps1`:
Program Files install, SCM registration (`sc create`, auto start, failure
recovery), private/domain-profile + localsubnet firewall rules, optional tray
autostart, legacy self-install cleanup (HKCU Run entry + LocalAppData copy),
full upgrade/uninstall.

## Not implemented

* **Screen streaming / remote-desktop view.** There is no media transport and
  no reserved `stream_request` message: the phone sends input to a PC it
  cannot see. Adding streaming is a protocol change, not a flag flip.
* **Windows VM integration matrix:** see `TEST-MATRIX.md`.
