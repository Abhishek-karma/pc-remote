# PC Remote — Windows Architecture

Status: service, IPC, session/privilege separation, secure desktop /
lock-logon input, and H.264 screen streaming (built and unit-tested;
**physical end-to-end verification on real hardware is still outstanding** —
see "Not verified" at the end).

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
                ◄── WSS :58642/stream (media, binary) ─►  (same TLS listener)
```

Two sockets, one port. The request path decides the connection's role:
`/` is the JSON control channel (input, power, SAS) and `/stream` is the media
channel. They are separate TCP connections so a stalled video client can never
add latency to input, and the stream socket has **no input command surface at
all** — it accepts only `stream_start` / `stream_stop` / `keyframe_request`.

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

## Screen streaming (Phase 4)

Capture and encode live in the **session agent**, because only a process in the
interactive session can see the desktop (Desktop Duplication requires it). The
agent stays **network-blind**: it writes compressed bytes to a local
write-sharing file and nothing else. Networking stays in the service.

```
Android MediaCodec(H.264) ◄── WSS /stream (binary MediaFrames) ──► service
                                                                       ▲
                                        tail fMP4 file (SYSTEM read)    │
                                                                   agent
                          GDI BitBlt ──► Media Foundation SinkWriter ──┘
```

* **Capture:** `ScreenCapture` (GDI `BitBlt` over the virtual desktop — no GPU
  required, so it also works headless/VM). Desktop pixels map to SendInput's
  normalized absolute space (pure, unit-tested, including multi-monitor origin
  offsets and clamping).
* **Encode:** `H264Encoder` drives the MF SinkWriter (`Vortice.MediaFoundation`,
  pinned to the .NET 8 line). Low-latency knobs are forced: real-time mode,
  zero B-frames (no decode reordering), CBR, keyframe-on-demand via the
  CleanPoint sample flag.
* **Container note:** the plan called for raw Annex-B NAL over a dedicated
  media pipe. In practice the Microsoft H.264 encoder MFT is
  asynchronously-driven and never answers a synchronous `ProcessOutput`
  (`MF_E_TRANSFORM_NEED_MORE_INPUT`), which is why the SinkWriter is used; it
  emits **fragmented MP4** (self-contained `moof`/`mdat` fragments). Transport
  is therefore write-sharing file tailing instead of a raw NAL pipe. It is
  still real compressed H.264 — the Android side demuxes the fMP4 before
  MediaCodec. Raw Annex-B remains a documented later optimization if physical
  testing shows the container costs latency.
* **Forwarding:** `StreamForwarder` tails the file and emits 64 KiB
  `MediaFrame`s (magic/version/keyframe/seq/pts/len, little-endian). Bytes are
  never dropped silently: if a stalled client falls more than 8 MiB behind, the
  forwarder fast-forwards to the live edge and requests a keyframe. Before that
  jump it replays the **init segment** (`ftyp`+`moov`, located by walking the
  top-level MP4 boxes) — without it MediaCodec can never be configured, because an
  IDR does not carry the SPS/PPS, so a client joining an in-progress stream would
  show a permanently black screen.
* **Sharing:** the stream is shared between clients, so the coordinator
  reference-counts them and only releases the encoder when the *last* one leaves.
  One device disconnecting cannot cut the picture for the others.
* **Adaptation:** `FpsPolicy` (pure, clock-injected) steps 15→12→9→7→5 when the
  forwarder drops and recovers after a 10 s healthy hold; changes relay to the
  agent over the existing control pipe. Bitrate stays fixed (CBR) because
  changing it would require re-creating the encoder.
* **Join/loss recovery:** the Android client requests a keyframe whenever the
  decoder falls behind or is reset, and both the demuxer and the decoder are reset
  on stop so a reconnect never reuses the previous stream's SPS/PPS.
* **DPI:** the session agent declares `PerMonitorV2` awareness. GDI capture and
  `SendInput` absolute coordinates both work in physical pixels, so on a mixed-DPI
  desktop they must not be virtualized against each other.

## Not verified

* **Physical end-to-end (the §24/§25 gate).** Nothing here claims streaming
  "works" from CI. Decode requires `MediaCodec`, which only exists on a real
  device, so decode+render is untested. Still to validate on real hardware:
  end-to-end latency, multi-monitor/DPI behaviour, the GPU-less fallback,
  Wi-Fi-loss reconnect, and MediaCodec vendor quirks. The pipeline should not
  be treated as proven until that gate is run.
* **Guaranteed 60 fps / hardware encode.** GDI + the in-box software encoder
  targets roughly 1080p30. A fixed 60 fps needs the later DXGI/Desktop
  Duplication + NVENC/hardware-MFT pass, which is documented, not built.

## Not implemented

* **Windows VM integration matrix:** see `TEST-MATRIX.md`.
