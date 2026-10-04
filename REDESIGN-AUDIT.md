# PC Remote — Redesign Audit & Deletion/Replacement Plan

**Date**: 2026-10-03
**Scope**: `windows-agent/` (C#/.NET 8), `android-app/` (Kotlin/Compose), CI (`.github/workflows`), docs.
**Method**: full read of every source file in both products, plus a clean build and the
36-test Windows suite. This document answers the two things the redesign brief asks
for first: (1) an architecture audit of the current repository, and (2) the
deletion/replacement plan. It then records what Phase 1 shipped.

---

## 0. TL;DR

The Windows agent is **not** the tangled multi-helper mess the redesign brief assumed.
The last three commits (`d62f5fa`, `ada6328`, `ed2c517`) already delivered most of the
Phase-1 architecture: a clean **three-process** model (`PCRemoteService.exe`,
`PCRemoteSession.exe`, `PCRemoteTray.exe`), the old UIAccess helper deleted, reliable
IPC, and per-session agent lifecycle with a watchdog. The two-layer
Service → Session-Agent split in the brief (§3–§4) is essentially already the shape of
this code.

The real gaps are **not** in the Windows process model. They are:

1. **No screen streaming at all** (§7 / Phase 4). There is no capture, no encoder, no
   media transport, no decoder, no desktop view. This is the single largest missing
   capability and it is confirmed in `ARCHITECTURE.md` *Not implemented*. Adding it is a
   protocol + pipeline change, not a flag flip.
2. **Android is a tabbed control hub** (Touchpad / Keyboard / Media / Power), not the
   desktop-first remote-desktop UI (§18–§19 / Phase 6).
3. **No Diagnostics view** (§21 / Phase 6).
4. **Logging is ad-hoc `Console.WriteLine` mirrors** with inconsistent markers and no
   levels/timestamps (§22). This is Phase 1 work and is implemented here.

**What Phase 1 actually shipped** (see the last section): structured leveled logging
across the whole Windows product, plus replacement of the stale `AUDIT.md` that
describes an architecture which no longer exists.

---

## 1. Current Windows architecture (verified)

Four C# projects in `windows-agent/PcRemoteAgent.slnx`, producing exactly three
deployable executables plus a shared library:

```
PCRemoteService.exe  (LocalSystem, session 0, SCM auto-start)
   ├─ ControlChannel   WSS :58642  — auth, pairing, input, power, SAS, session_status
   ├─ PairingStore     DPAPI (LocalMachine) trusted tokens + rotating pairing code
   ├─ CertificateManager  stable self-signed cert + stable pcId GUID (never regen)
   ├─ SessionManager   WTS events + 500 ms watchdog → spawn/kill per-session agent
   ├─ InputRouter      routes input to the correct security boundary
   ├─ SasController    SendSAS for Ctrl+Alt+Del (never simulated keys)
   ├─ PowerController  sleep/lock/shutdown/restart (SYSTEM)
   ├─ IpcServer        \\.\pipe\PCRemoteCtl — tray + helpers, privilege-checked
   └─ IpcCoordinator   elevated-only handlers (revoke, mInt pairing code, migrate)

PCRemoteSession.exe   (the session agent — one binary, three modes)
   ├─ default           console-user token → SendInput into normal desktop
   ├─ --secure-input    duplicated SYSTEM token in console session → Winlogon desktop
   │                    (UAC / lock / logon) — genuine Windows requirement
   └─ --lock            one-shot LockWorkStation
   Contains InputInjector (SendInput), DesktopInjector (allowlist), SessionPipeServer.

PCRemoteTray.exe       (console user) — NotifyIcon menu over IPC only.
                       No server, no pairing store, no input engine (§17 satisfied).

PcRemote.Core.dll      WebSocketConnection (RFC 6455 + TLS), RemoteMessage/protocol,
                       SecureDesktop, IpcClient/IpcServer, MdnsAdvertiser, NetworkInfo,
                       FirewallHelper, AgentLog (logging mirror).
```

**Privilege boundary** is exactly the brief's §5–§6 shape:

| Component | Token | Can | Never |
|---|---|---|---|
| Service | LocalSystem | network, auth, session mgmt, SAS, power, launch agents | render UI, see user desktop |
| Session agent (normal) | console user | inject into that session's normal desktop | network, persisted state |
| Session agent (secure) | SYSTEM in console sess. | open WinSta0\Winlogon, inject into UAC/lock/logon | network, normal-desktop injection |
| Tray | console user | status, revoke, logs | own tokens, listen on network |

### Why the secure-agent mode is legitimate (not the "custom UIAccess" the brief warns against, §6)

The brief says *"do not create a custom UIAccess architecture unless Windows actually
requires it."* Lock-screen / logon / UAC input **is** a genuine, demonstrated Windows
requirement: normal user-token SendInput cannot reach the Winlogon desktop. The old
`uiAccess=true` helper could not even launch (Windows refuses an unsigned UIAccess
binary — see `SessionWorkerClient.cs` and `CHANGELOG.md`) and was already removed. What
remains is **not** a UIAccess helper: it is the same `PCRemoteSession.exe` launched a
second time with a duplicated SYSTEM token in the console session, attaching to
`WinSta0\Winlogon` and re-validating every command against its own narrower allowlist.
That is the smallest privileged path that satisfies the §12 (UAC) and §11 (login/lock)
requirements, and it is exactly the "smallest possible privileged path" the brief
permits. **Keep it.**

### Ctrl+Alt+Del (§10) — correct
`SasController.SendSas` calls `SendSAS` from `sas.dll`, the documented SYSTEM-only API.
It is never faked with `SendInput`. Good.

### Dead-code findings
A full read found **no orphaned classes, no duplicate connection/session/input managers,
no legacy UIAccess code, no abandoned screen-streaming stubs, no commented-out
implementations.** The codebase is lean (~4,400 lines C#, 36 tests). The old `AUDIT.md`
is the only genuinely obsolete artifact — it describes a pre-0.2 single-`Program.cs`,
`makaretu`-based, `Random.Shared` architecture that was since replaced and hardened. It
is replaced here.

### Android current architecture
- `network/RemoteConnection.kt` — WSS + TOFU cert-pinning, per-host tokens, single
  reconnect state machine (DISCONNECTED→…→CONNECTED→RECONNECTING→CONNECTED, §20 ✓).
- `network/DiscoveryService.kt` — NSD browse with multicast lock; hides `NsdManager`
  behind testable seams.
- `ConnectionViewModel` / `MainActivity` — single source of connection truth.
- `ui/` — Pairing, Touchpad (+ `TouchpadGestureEngine` gesture state machine),
  Keyboard, Media, Power, Settings; plus `androidTest` E2E against a local agent.
- Gesture model matches §9 (`TouchpadGestureEngine`: 1-finger move, tap=left,
  two-finger=scroll, long-press=right).

**Android already satisfies** §2 (discovery + saved devices + manual IP fallback),
§20 (one reconnect state machine), §9 (gesture mapping), §14 (TOFU pinning + encrypted
prefs, tokens keyed by stable pcId so DHCP survives).

---

## 2. Deletion / replacement plan

Because the process model is already correct, this plan is deliberately small. It is
forward-ordered so each phase only touches what the previous phase proved.

### Keep (do not delete — required by the new architecture)
- The three-process model (`Service` / `Session agent` / `Tray`). **No rename to
  `PCRemoteAgent.exe`**: the brief's names are about *how many* processes, and our 3
  already satisfy it; a rename would churn the installer, CI, and signing with zero
  functional gain. Recorded here so it is a conscious decision, not accidental.
- The secure (Winlogon) agent mode — see §1 above.
- `SasController` (correct SAS), `PowerController` (SYSTEM ops), TOFU pinning, IPC
  privilege gating, the pairing store.
- The one-time legacy-token migration (`ImportLegacyTokens` / `migrate_tokens` /
  `LegacyAppDataDir`). It is a bounded, tested upgrade path for pre-0.2 users; §16/§28
  Phase 7 still requires "upgrade" to work, so this is functionality, not a
  compatibility layer that hides a broken core. Keep until upgrades to 0.2 are no longer
  in play.

### Replace
| Artifact | Why | Action |
|---|---|---|
| `AUDIT.md` | Describes pre-0.2 source (files/PRNG/mDNS lib no longer exist) — actively misleading | Replaced by this document |
| Logging (`Console.WriteLine` markers) | §22 requires structured levels + actionable context; current format has no level/timestamp | Implemented in Phase 1 (below) |
| Android `Media`/`Power` tabs as primary UX | §19 wants desktop-first, minimal controls | Phase 6 — replace the tab hub with the LIVE DESKTOP view; keep Media/Power behind "More" |

### Defer to later phases (not Phase 1)
- **Screen streaming** (§7): capture (Windows Graphics Capture / Desktop Duplication),
  encoder (NVENC/H264 with adaptive bitrate/FPS/resolution), a **separate media
  channel** (binary frames, not JSON) from the control WSS, and the Android
  `SurfaceView`/decoder + desktop view. → **Phase 4**.
- **Desktop-first Android UI + Diagnostics** (§19, §21). → **Phase 6**.
- **Real E2E / VM matrix** (§24, §25) — the acceptance criteria are explicitly
  physical-machine gates; they cannot be satisfied by unit tests or this audit. Logged
  as the release gate, not done here.

---

## 3. Phase roadmap against the brief

| Phase (brief) | Status | Evidence / what remains |
|---|---|---|
| 1 — Architecture cleanup | **Mostly already done**; Phase-1 logging + doc work landed here | 3-process model present; UIAccess removed; structured logging added |
| 2 — Discovery/Pairing/Auth/Connection/Reconnect | **Done** | WSS+TOFU, pairing store, single reconnect SM, NSD + saved + manual IP; 36 tests |
| 3 — Input (mouse/keyboard/scroll/shortcuts) | **Done** | `InputRouter` + `InputInjector` + gesture engine |
| 4 — Screen streaming | **Built, not physically verified** | Full compressed H.264 pipeline (A–E); decode+render needs a real device (§24/§25 gate) |
| 5 — Windows security boundaries (UAC/elevated/lock/logon/session switch) | **Substantially done** | secure agent, SAS, WTS detection; needs physical E2E (§24/§25) |
| 6 — Product UX | **Not started** | desktop-first Android, diagnostics, tray polish |
| 7 — Installer/recovery | **Done for 0.2** | Inno setup, upgrade/uninstall, firewall; needs reboot/upgrade E2E |

---

## 4. Phase 1 as executed (this change)

Scope kept to the brief's Phase 1 ("establish the four components, make lifecycle
reliable, remove dead architecture") plus its cross-cutting observability rule (§22):

1. **Structured, leveled logging** across Service, Session agent, Tray, and Core —
   `AgentLog.Info/Warn/Error/Debug` emitting `[HH:mm:ss][LEVEL][subsystem] message`,
   still mirrored to the rotating daily file. Lifecycle and error lines converted;
   high-frequency relay lines demoted to `Debug` so they stay out of normal logs.
2. **Replaced the stale `AUDIT.md`** with this document.

Verification for Phase 1 (not "compilation-only"): Windows solution builds with 0
warnings/0 errors and the full 36-test suite passes; logs still write to the same
ProgramData / LocalAppData log files with the same retention. (Running the *real*
service/agent interaction and screen-streaming are later-phase, physical gates.)

> Phase 1 does **not** touch the Android app. Android work is Phase 2 (already done),
> Phase 4 (streaming), and Phase 6 (UX).

---

## 5. Final acceptance mapping (what is still required to call this *done*)

The brief's §31 acceptance flows (install → discover → pair → see desktop → move → click
→ type; survive reboot; auto-reconnect; elevated apps; UAC; lock screen) map to:
- Reachable today: install/service/discovery/pairing/connect/input/reconnect.
- **Blocked on Phase 4**: "see desktop".
- **Blocked on real-machine E2E (Phase 7 gate)**: reboot, UAC, lock-screen, elevated-app
  verification — these must be physically tested and must not be claimed from CI.

Nothing in this audit claims any unsupported capability works. The redesign's §32 rule
("small codebase that reliably controls Windows beats a sophisticated architecture that
passes CI but fails on a real PC") is the governing bar, and the streaming + physical-E2E
work is where that is actually going to be earned.

---

## 6. Phase 4 — screen streaming (status as of 2026-10-03)

Increments A and B are implemented and green in `windows-agent` (`dotnet test`: **51/51
passing**; the H.264 encoder test really runs Media Foundation on the build machine and
asserts a genuine fragmented-MP4 stream).

### Increment A — media transport foundation (done)
- `WebSocketConnection` now exposes `SendBinaryAsync` (opcode 0x2) and reads the request
  path, so the same TLS port 58642 can classify a connection as `/stream` (binary media)
  vs `/` (JSON control) — **no new firewall port**.
- `MediaFrame` framing (magic/version/keyframe-flag/seq/pts/len) in-core.
- Protocol: `stream_start` / `stream_stop` / `keyframe_request` added to the allowlist;
  server replies `stream_state`. The `/stream` socket is authenticated (same token/pin)
  but has **no input command surface** — input stays on the control socket.

### Increment B — capture + encode (done)
- `PcRemote.Session.Streaming`: **GDI `ScreenCapture`** (BitBlt virtual desktop, no GPU),
  and an **H.264 encoder driven through the Media Foundation SinkWriter** (`Vortice
  .MediaFoundation` 3.8.3, pinned to .NET 8). Low-latency knobs forced: real-time mode,
  zero B-frames, CBR, keyframe-on-demand (CleanPoint).
- `VideoStreamer` ties capture→encode→a SYSTEM-readable write-sharing fMP4 file that the
  service tails. The agent stays network-blind (files/pipe only, never a socket).

### Architectural decision made mid-increment (deviates from the plan's wording)
The plan said "Annex-B NAL" + a dedicated media pipe. **Reality:** the raw H.264 encoder
MFT is asynchronously-driven and never answers a synchronous `ProcessOutput`
(`MF_E_TRANSFORM_NEED_MORE_INPUT`), which is exactly why CouchDesk drives it via the
SinkWriter. The SinkWriter emits **fragmented MP4** (ftyp+moov, then self-contained
moof/mdat) — real H.264, but containerized rather than raw Annex-B. Transport therefore
uses **write-sharing file tailing** (proven in CouchDesk) instead of a raw NAL pipe.
This is not a fake pipeline — it is still compressed H.264, and the Android side demuxes
fMP4 (MediaExtractor over a MediaDataSource) before MediaCodec. If raw Annex-B proves
necessary for latency/phone quirks, that remains a documented later optimization.

### Not yet done (Increment F only)
- **D (done 2026-10-04)**: the Android client. `StreamClient` opens the second WSS on
  `/stream` with the same TOFU pin (refuses unpinned hosts), authenticates with the
  saved token, then `stream_start`; `stream_state` drives the UI state machine
  (`Connecting/Active(width,height,fps)/Failed(reason)/Stopped`). Binary frames are
  parsed as `MediaFrame` and fed to `FragmentedMp4Demuxer`, whose output configures
  `H264StreamDecoder` (MediaCodec H.264 → Surface, low-latency on API 30+, dedicated
  decode thread; behind-decoder drops trigger keyframe requests). **The demuxer is
  verified against a REAL fixture generated by the product's own Windows encoder**
  (`app/src/test/resources/sample-stream.mp4`, regenerable), including byte-at-a-time
  feeding and MediaFrame re-framing parity — those tests caught three real parser
  bugs: little-endian box reads (MP4 is big-endian; MediaFrame's own header is LE by
  design), no descent into `traf`, and decode-time continuation when the muxer omits
  `tfdt`. `RemoteDesktopScreen` shows the live desktop as the FIRST nav destination,
  maps touch through the aspect-fitted video box into desktop pixels, and sends
  `mouse_move_abs` (Windows side: SendInput absolute, virtual-desktop normalized,
  pure mapping unit-tested). Tap=left click, long-press=right click, two-finger
  drag=scroll.
- **E (done 2026-10-04)**: adaptation + diagnostics landed. **Keyframe-on-join**: a
  client attaching to an already-running stream forces an IDR before the file is
  handed over (`StreamingCoordinator`, unit-tested with a fake stream via the new
  `IVideoStream` seam) — without it the joiner froze until the next GOP boundary.
  **Congestion adaptation**: the service-side forwarder knows when a client falls
  behind, so the FPS policy lives next to the signal —
  `FpsPolicy` (pure, clock-injected, unit-tested) steps 15→12→9→7→5 on forwarder
  drops and steps back up after a 10 s healthy hold; changes relay to the agent via
  the new `stream_fps` IPC and `VideoStreamer.SetFps`, with each adaptation logged.
  Bitrate stays fixed (CBR): changing it would require encoder re-creation — a
  documented later optimization if physical testing shows FPS alone is insufficient.
  **Diagnostics**: the agent logs observed-vs-target FPS + encode failures every 5 s;
  the IPC `status` reply now carries `streaming` + `streamClients` (media sockets are
  counted separately from devices — `PrintConnectedCount` was fixed to match);
  `WebSocketConnection.CloseAsync` now completes the close handshake (drains until the
  peer's close, bounded 2 s) — the loopback test caught that disposing right after
  sending the close frame still RST'd the peer. **Windows 66/66, Android 35/35.**
- **F**: physical Windows host + Android device E2E — the §24/§25 gate; latency,
  multi-monitor/DPI, GPU-less fallback, Wi-Fi loss, MediaCodec vendor quirks are all
  **physical**, not claimed from CI.
