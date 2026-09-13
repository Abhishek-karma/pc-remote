# PC Remote — Windows Agent

Windows server half of PC Remote: a **WebSocket-over-TLS** (WSS) endpoint
that lets the Android app pair and control this PC — mouse, keyboard, media,
power — and advertises itself via mDNS so the app can find it automatically.

## Requirements

- .NET 8 SDK (https://dotnet.microsoft.com/download)
- Windows 10/11

## Setup

No admin rights and no `netsh` steps are required — the agent binds a plain
TCP port and serves TLS with its own self-signed certificate (keyed to this
machine, stored under `%AppData%\PcRemoteAgent\`).

The only setup is the Windows Firewall, so inbound connections are allowed:

```powershell
netsh advfirewall firewall add rule name="PC Remote Agent" dir=in action=allow protocol=TCP localport=58642
netsh advfirewall firewall add rule name="PC Remote Agent mDNS" dir=in action=allow protocol=UDP localport=5353
```

(The second rule is for mDNS advertisement — only needed for auto-discovery.)

## Run it

```bash
cd windows-agent
dotnet run            # tray mode is the default for the published exe
```

The published agent runs as a **tray application** (no console window):

- the tray icon shows a menu with the **live pairing code** (plus a
  "Copy pairing code" action), the connected-device count, a **"Run at
  startup"** toggle (writes a HKCU Run-key entry — user level, no admin
  rights), a logs-folder shortcut, and Exit;
- a notification balloon shows the pairing code on start and every time it
  rotates;
- startup and connection events land in
  `%AppData%\PcRemoteAgent\logs\agent-<date>.log`.

For development with visible output run `dotnet run -- --console` (or
`PC-Remote-Agent-win-x64.exe --console`) — the output looks like:

```
=== PC Remote Agent (console mode) ===
Listening on port 58642 (WSS)
Pairing code (valid 5 minutes, auto-refreshes): 483920
Local IP addresses to enter manually if discovery fails:
  192.168.1.42:58642 (wss)
mDNS: advertising "MY-PC" _pc-remote._tcp. on port 58642
```

Enter the pairing code into the Android app to connect. The phone should
find this PC automatically in the "Discover nearby PC" list (mDNS); if not,
type the IP manually. Trust tokens are stored encrypted on this machine, so
already-paired phones stay paired across agent restarts, and the 5-minute
pairing code auto-rotates.

## What's implemented vs. what's stubbed

Implemented:
- WSS server (TcpListener + SslStream + minimal RFC 6455 framing), self-signed
  certificate generated on first run — no `HttpListener`, no admin rights
- Pairing via a 6-digit code that expires after 5 minutes and rotates
  automatically, then a persistent trust token (DPAPI-encrypted on disk)
- Mouse move (relative), click (left/right/middle, down/up/click), scroll
- Keyboard: special keys (arrows, enter, backspace, etc.) with modifiers, plus
  arbitrary Unicode text typing
- Media keys (play/pause, next, prev, volume, mute)
- Power actions (sleep, shutdown, restart, lock workstation), announcing
  `disconnecting` before an expected shutdown/restart
- mDNS advertisement (`_pc-remote._tcp.`, via `Makaretu.Dns.Multicast`)
- Live "N device(s) connected" counter in the console

Not yet implemented:
- File transfer, screen mirroring, clipboard sync
- A tray icon UI with a persistent "connected" indicator (currently a
  console app)

## Next steps to harden this for real use

1. Move from a console app to a system tray app so it can run quietly in the
   background and show pairing codes / connected-device status from a menu.
2. Wrap each `HandleCommand` dispatch individually so one bad command logs
   and continues rather than ending the connection.