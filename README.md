# PC Remote

Control your Windows PC from an Android phone over your local network.

A touchpad and a keyboard, and nothing else.

## Features

- Touchpad with tap (left click), two-finger tap (right click), two-finger scroll and long-press drag
- Mouse movement, left/right/middle click, scroll wheel
- Modifier keys: Ctrl, Alt, Shift, Win, plus Esc, Tab, Enter, Backspace and the arrow keys
- Text input using your phone's own keyboard
- Automatic LAN discovery, so you never type an IP address
- Secure pairing with a persistent trust token
- Works on the normal desktop, the lock screen, the sign-in screen and UAC prompts
- Installs on Windows as a service and starts automatically

There is no screen streaming, no video, no file transfer, no clipboard sync, no
accounts and no cloud. Those are not "coming later" - they are not part of this
product.

## Installing

### Windows

1. Run `PC-Remote-Setup.exe`.
2. Accept the prompts. The installer registers the service, adds one firewall
   rule for local networks, and starts everything.

That is the whole setup. There is no port to configure and no server to start by
hand.

To uninstall, use Windows "Apps" as usual. The service and the firewall rules
are removed. Pairing data is kept on purpose, so reinstalling does not force
every phone to pair again; delete the `C:\ProgramData\PCRemote` folder if you
want it gone.

### Android

1. Install the APK.
2. Grant the network permission if Android asks.

## Pairing

The first time:

1. Open **PC Remote** on the PC (tray icon) to see a six-digit pairing code.
   When the tray is started by Windows (the installer's default), it runs with
   administrator rights and shows the code. If you started the tray by hand
   without elevation, right-click it and choose "Run as administrator".
2. On the phone, tap the PC in the list.
3. Type the code.

After that the code is never needed again. The phone keeps a token for the PC and
reconnects on its own - including after the phone's app was closed, the PC's IP
changed, or both machines restarted.

To unpair a phone, use "Revoke all paired phones" in the tray menu, or "Forget"
in the app's Settings.

## Troubleshooting

**The PC does not appear in the list.**
Both devices must be on the same Wi-Fi network, and the network must be set to
Private in Windows. Guest and public networks block discovery. You can still
connect by typing the PC's IP address at the bottom of the list.

**Pairing fails.**
Check the code has not expired - it changes every few minutes. If it fails several
times the PC temporarily refuses new attempts; wait two minutes and retry.

**"Connection lost".**
The phone reconnects on its own with a short backoff. This is usually the PC
sleeping, Wi-Fi blipping, or the service restarting.

**Input does nothing after locking the PC.**
Input on the lock screen, sign-in screen and UAC prompt requires the secure
helper, which only runs once someone has logged in at least once on this PC
since it booted. See "Lock screen" below.

**Nothing responds to the mouse.**
Something is holding the mouse button. Disconnecting releases everything the app
was holding; if that does not help, press the physical Ctrl and Alt keys on the
PC to clear any stuck modifier.

## Security

- Traffic is TLS on the local network only. The PC generates its own certificate
  once and keeps it; the phone pins its fingerprint when you pair.
- If a PC's certificate changes after pairing, the phone refuses to connect.
- Pairing uses a six-digit code that changes every minute.
- Failed pairing attempts are rate limited per IP address.
- Tokens and pins are stored in encrypted storage (DPAPI on Windows, the Android
  Keystore on the phone).
- Typed text, pairing codes and tokens are never written to the logs.

There is no account, no server and no traffic that leaves your network.

## Architecture

```
Android phone
     |  LAN, TLS
     v
PCRemoteService.exe   (Windows service)
     |                - mDNS discovery
     |                - TLS listener and pairing
     |                - session/desktop detection
     |
     |  named pipe
     v
PCRemoteInput.exe     (one helper, run twice)
     |                - normal desktop: as the logged-on user
     |                - lock/UAC/sign-in: as SYSTEM, on the Winlogon desktop
     v
SendInput
```

A Windows service runs in session 0 and cannot touch the desktop you are looking
at, so input is always injected by a small helper running in your session. That
is a Windows constraint, not extra architecture. The helper has no network code,
no credentials and no storage of any kind.

### Projects

| Project | Role |
| --- | --- |
| `PcRemote.Core` | Protocol, TLS socket, pairing store, mDNS, logging, tray IPC |
| `PcRemote.Service` | The Windows service: listener, auth, session routing |
| `PcRemote.Input` | The input helper. No project references at all |
| `PcRemote.Tray` | A small status menu |
| `PcRemote.Tests` | Protocol and key-allowlist tests |

### Protocol

One flat JSON object per message over TLS. There is no request id, no
acknowledgement and no RPC layer; mouse movement is fire-and-forget so a slow
network cannot back up the touchpad.

```json
{"v":1,"type":"move","dx":12,"dy":-4}
{"v":1,"type":"button","button":"left","action":"down"}
{"v":1,"type":"button","button":"left","action":"up"}
{"v":1,"type":"scroll","delta":-3}
{"v":1,"type":"key","key":"ENTER"}
{"v":1,"type":"key","key":"CTRL","action":"down"}
{"v":1,"type":"text","text":"hello"}
{"v":1,"type":"release_all"}
```

`release_all` is sent whenever the app disconnects, and the service also releases
everything itself when a connection drops. A PC is never left with a held mouse
button or a stuck modifier.

Every message is validated twice: once in the service, which is the trust
boundary for the network, and again in the helper before anything touches Win32.

### Lock screen

Lock screen, sign-in and UAC support is a genuine Windows requirement rather than
a feature, and it is deliberately isolated:

- The helper runs a second time as SYSTEM inside your session and attaches to the
  `Winlogon` desktop.
- It receives already-authenticated, already-validated commands and injects them.
  It never talks to the network and never stores a credential.
- The service notices UAC prompts by asking the SYSTEM helper which desktop is
  active, and routes input to the helper attached to `Winlogon` while one is up.
- When the desktop is not reachable, the service tells the phone instead of
  silently dropping input.

## Building from source

```bash
# Windows
cd windows-agent
dotnet build PcRemoteAgent.slnx -c Release
dotnet test src/PcRemote.Tests/PcRemote.Tests.csproj

# Installer (needs Inno Setup 6)
powershell -File installer/build.ps1

# Android
cd android-app
./gradlew assembleDebug
./gradlew testDebugUnitTest
```

## Releasing

Pushing a `v*` tag triggers the release workflow, which builds, signs and
publishes the Windows installer and Android APK to the GitHub Release. It
requires code-signing secrets to be configured on the repository.

See **[RELEASE.md](RELEASE.md)** for the full release flow and how to generate
the Windows and Android signing material.

## Licence

MIT.