# Windows VM Integration Test Matrix

Manual/VM matrix required before claiming any lock-screen, UAC or logon
capability (requirement 16, rule 17: "do not claim support without testing").
Run every row on a Windows 11 VM snapshot; reset the snapshot between rows
unless the row is explicitly cumulative. Record pass/fail + notes in the
columns; CI covers only the unit/integration subset.

Environment checklist per run:
- [ ] VM on a private network profile; Android device or test harness on same LAN
- [ ] Latest installer (`PC-Remote-Setup.exe`) from the release under test
- [ ] Known-good physical keyboard/mouse for local fallback verification
- [ ] UltraViewer (or another remote-control tool) installed for conflict rows

| # | Scenario | Steps | Expected | Result | Notes |
|---|----------|-------|----------|--------|-------|
| 1 | Windows boot | Boot VM, wait 60 s, check `sc query PCRemoteService` | Service RUNNING before any logon | ☐ | |
| 2 | Service start | `sc stop` + `sc start` | WSS binds 58642, tray reconnects | ☐ | |
| 3 | Service restart after crash | `taskkill /f /im PCRemoteService.exe` | SCM restarts within ~30 s (recovery config) | ☐ | |
| 4 | Normal desktop | Connect from app, move/click/type in Notepad | Full control | ☐ | |
| 5 | Elevated Task Manager | Launch Task Manager elevated; remote control | **Out of scope:** input into elevated windows is not supported (UIPI; no UIAccess helper) | ☐ | dropped from matrix — record as a known limitation |
| 6 | Registry Editor (elevated) | Same as #5 with regedit | Same | ☐ | |
| 7 | Device Manager (elevated) | Same as #5 | Same | ☐ | |
| 8 | UAC consent prompt | Trigger `regedit` elevation remotely | Mouse/keyboard work on secure desktop; state shows `secure_desktop` | ☐ | |
| 9 | UAC credential prompt | Trigger elevation with non-admin account | Credential fields accept remote keyboard | ☐ | |
| 10 | Win+L | Send lock from app | Locks; input routes to secure path | ☐ | |
| 11 | Lock-screen mouse | Lock, then move/click remotely | Works (secure-input helper) | ☐ | |
| 12 | Lock-screen keyboard | Lock, type into password field | Password accepted; **no password text in any log** | ☐ | audit `ProgramData\PCRemote\logs` |
| 13 | Windows login screen | Reboot to logon screen | Session shows `logon`; mouse/keyboard work | ☐ | |
| 14 | Ctrl+Alt+Del | Tap SAS button from app | Secure screen appears (SendSAS path) | ☐ | |
| 15 | Logout | Log off from session | Service stays up; app shows `logon`; relog works | ☐ | |
| 16 | Reboot | Reboot from app | Clean shutdown; service auto-starts after boot; app reconnects | ☐ | |
| 17 | Session switch (fast user switching) | Two accounts, switch sessions | Input follows active console session | ☐ | |
| 18 | RDP session | RDP into VM concurrently | Graceful: state reported, no input fights | ☐ | |
| 19 | Multiple monitors | 2+ displays | Mouse reaches all monitors | ☐ | |
| 20 | DPI scaling | 125%/150% scaled display | Cursor hits what user sees | ☐ | |
| 21 | Network interruption | Disable NIC 10 s, re-enable | App auto-reconnects with saved token | ☐ | |
| 22 | IP change (DHCP) | Change VM IP / renew lease | No re-pairing required (pcId + stable cert pin) | ☐ | |
| 23 | Android app restart | Kill app, relaunch, reconnect | Token still valid | ☐ | |
| 24 | Service crash mid-session | Kill service while connected | Client shows disconnect; reconnects after SCM restart | ☐ | |
| 25 | Tray crash | Kill PCRemoteTray.exe | Connectivity unaffected (service owns network) | ☐ | |
| 26 | UltraViewer installed | Install UltraViewer, idle | No input conflicts | ☐ | |
| 27 | UltraViewer running/elevated | UltraViewer session active | Document interaction; PC Remote remains functional | ☐ | |
| 28 | Concurrent remote-control app | AnyDesk/TeamViewer present | Same as #27 | ☐ | |
| 29 | Update | Publish new release; update from tray | SHA-256 + signature verified; service stops/installs/restarts; version bumped | ☐ | tamper the artifact to verify refusal |
| 30 | Update rollback path | Kill service during update apply | SCM recovery restarts old version; next update attempt works | ☐ | |
| 31 | Uninstall / reinstall | Uninstall, reboot, reinstall | Service/firewall rules removed; reinstall works; pairing re-established | ☐ | |
| 32 | Legacy agent migration | Old 0.1.x agent paired, then install new setup | Tray migration hands tokens to service; no re-pairing | ☐ | |

Automated (CI, no VM required): pairing/auth matrix, protocol wire format,
command allowlist (incl. rejection of exec/shell types), TLS+WSS handshake,
token entropy, certificate stability rules — `src/PcRemote.Tests`.
