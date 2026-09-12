# Troubleshooting

## Windows Agent Issues

### Agent starts but the PC won't accept phone connections

The agent no longer needs admin rights or `netsh urlacl` (it serves WSS via
its own TLS listener), so the usual remaining cause is the Windows Firewall:

```powershell
netsh advfirewall firewall add rule name="PC Remote Agent" dir=in action=allow protocol=TCP localport=58642
netsh advfirewall firewall add rule name="PC Remote Agent mDNS" dir=in action=allow protocol=UDP localport=5353
```

(The second rule is for mDNS advertisement — only needed for auto-discovery.)

### Phone can't connect at all (connection times out)

Check, in order:
1. Both devices are actually on the same Wi-Fi network (not one on 5GHz
   guest and one on the main 2.4GHz network, for example — some routers
   isolate these).
2. Windows Firewall is allowing TCP 58642 (see the rule above).
3. The IP entered matches what the agent printed — IPs can change after a
   router restart/DHCP lease renewal; mDNS discovery
   (`02-FEATURE-SPECIFICATION.md` F1.5) covers this by letting you tap the
   PC's advertised name instead of typing the IP.
4. Router/AP "client isolation" (common on guest networks) is turned off —
   this silently blocks device-to-device traffic even though both devices
   show as "connected."
5. Certificate-pin mismatch: if the PC was paired before but its agent cert
   changed (e.g. `server-cert.dat` was deleted), the app refuses to trust
   the new cert — Settings → Paired PCs → Forget that PC and re-pair once
   (`09-SECURITY-PRIVACY.md` §2).

### Pairing code rejected even though it looks correct

- Codes **expire after 5 minutes** and rotate automatically
  (`09-SECURITY-PRIVACY.md` §3). If the code is older than that, use the
  newest "Pairing code refreshed: XXXXXX" line from the agent console.
- The agent could also have restarted since you looked — startup always
  prints a fresh code.
- Double-check for a typo — the code is exactly 6 digits, no letters.

### Cursor doesn't move / keys don't type even though the app shows "Connected"

- Verify the PC isn't locked — `SendInput`/`keybd_event` calls generally
  don't affect a locked screen's login prompt the same way they do an
  active desktop session; try unlocking the PC first.
- Check the agent's console for an unhandled exception around the time you
  tried the action (`10-ERROR-HANDLING.md` §4 notes this isn't always
  gracefully caught per-command yet).

## Android App Issues

### App stuck on "Connecting…" indefinitely

- Confirm the agent is actually running (check its console window) — the
  app doesn't currently distinguish "agent not running" from "still trying"
  with a fast-enough timeout in every case; give it 10–15 seconds, then
  back out and retry rather than waiting indefinitely.

### Previously-paired PC asks for a pairing code again

Should not happen for a normal agent restart anymore — trust tokens are
persisted on the agent side (DPAPI-protected file) and on the phone
(encrypted prefs), so a restart keeps devices paired. If you still see the
code prompt, the saved token was removed or revoked (e.g. via Settings →
Forget), or the agent's cert changed (see "Phone can't connect" step 5) —
re-enter the current code from the agent console.

### Touchpad feels too sensitive / not sensitive enough

Open Settings → "Touchpad sensitivity" and adjust the slider (0.5×–3.0×);
the change applies immediately to the touchpad screen.

### App crashes or force-closes

- Check `adb logcat` for a stack trace around the crash time.
- Since there's no crash-reporting integration yet
  (`14-OBSERVABILITY-LOGGING.md` §4), this is currently the only way to
  diagnose a crash without being able to reproduce it directly.

## Network/Environment Issues

### App doesn't find the PC in the discovery list

1. Both devices must be on the same subnet — mDNS multicast does not cross
   subnets or VLANs.
2. AP/client isolation (common on guest networks) blocks multicast; the
   discovery list will be empty even though manual IP entry works.
3. Windows Firewall must allow inbound **UDP 5353** (mDNS) in addition to
   TCP 58642:
   ```powershell
   netsh advfirewall firewall add rule name="PC Remote Agent mDNS" dir=in action=allow protocol=UDP localport=5353
   ```
4. Some managed networks block mDNS by policy (see "Corporate/managed Wi-Fi"
   below) — fall back to manual IP entry.

### Works on home Wi-Fi but not on a phone hotspot / mobile data

Expected — this is a LAN-only tool in v1 (`01-PRODUCT-REQUIREMENTS.md`
non-goals, `05-TECHNICAL-ARCHITECTURE.md` §2.1). The phone must be on the
**same local network** as the PC, not merely "connected to the internet."

### Corporate/managed Wi-Fi network doesn't work

Many corporate networks block device-to-device LAN traffic (and often
mDNS/multicast) as a security policy, similar to guest-network client
isolation. This is a network policy outside the app's control — try a home
network instead, or ask a network administrator about device-to-device
communication policy if this is a managed environment you control.

## If None of the Above Helps

Gather, before reporting an issue:
- Windows agent console output from the time of the problem.
- `adb logcat` output from the Android app around the same time.
- Windows version and Android version.
- Whether both devices are on the exact same SSID/network.

This mirrors the manual diagnostic process described in
`14-OBSERVABILITY-LOGGING.md` §5, since there's no automated crash/error
reporting to pull this information from remotely.
