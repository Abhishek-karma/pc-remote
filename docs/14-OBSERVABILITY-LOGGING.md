# Observability & Logging

## 1. Guiding Principle

Consistent with the no-cloud, local-only stance in
`09-SECURITY-PRIVACY.md` §5: **logging in this project is for local
developer/user debugging only.** Nothing is transmitted off-device
automatically. There is no crash-reporting SDK, no analytics SDK, and no
remote log aggregation in v1.

## 2. Windows Agent Logging

**Current state:** plain `Console.WriteLine` calls throughout `Program.cs`
**mirrored to a dated log file** — `%AppData%\PcRemoteAgent\logs\agent-<date>.log`
(7-day retention, pruned at startup) via `AgentLog.cs`. The mirror exists so
an agent that runs at login (Startup-folder entry, no visible console) can
still be diagnosed and its pairing code read from the log file.

**Planned improvements:**
- When the agent becomes a tray app (no visible console,
  `05-TECHNICAL-ARCHITECTURE.md` §3), the file mirror stays; only the
  console half becomes optional.
- Introduce log levels (Info/Warn/Error) rather than uniform
  `Console.WriteLine` — e.g., a normal connect/disconnect is Info, a failed
  auth attempt is Warn, an unhandled exception in command handling is Error.
- Cap log file retention (e.g., keep the last 7 days or last 5MB) so logs
  don't grow unbounded on a machine that runs the agent for months.

**What should be logged:**
- Connection opened/closed (with client IP, not device-identifying info
  beyond that).
- Auth success/failure (without logging the pairing code or token value
  itself — log "auth succeeded for <ip>" not the secret).
- Unknown/malformed message types (for protocol-debugging during
  development).
- Unhandled exceptions from command dispatch (§ see `10-ERROR-HANDLING.md` §4).

**What must never be logged:**
- Trust tokens or pairing codes in plaintext.
- Full `text_input` contents by default — logging every keystroke a user
  types (which could include passwords) at Info level would itself be a
  privacy issue even in a purely local log file. If verbose debug logging
  of message payloads is ever added for development, it must be a
  clearly-labeled debug-only build flag, never in a normal running agent.

## 3. Android App Logging

**Current state:** no explicit logging added yet beyond default
Kotlin/Android runtime behavior (uncaught exceptions go to Logcat as usual).

**Planned:**
- Use standard `Log.d`/`Log.w`/`Log.e` (via a small wrapper, e.g. a
  `Logger` object) for connection state transitions, matching the agent's
  categories above (connect/disconnect, auth result, errors).
- Discovery lifecycle (browse started/stopped, number of PCs found,
  discovery failure) is debug-level — discovered host/IP values are fine to
  log; tokens and pairing codes still never logged.
- Strip or no-op debug-level logs in release builds (standard practice via
  a `BuildConfig.DEBUG` check or a logging library like Timber configured
  per build type) so verbose logs aren't left enabled in a shipped app.
- Same rule as the agent: never log token values or full typed text content.

## 4. No Crash Reporting / Analytics in v1

Explicitly **not** integrated: Firebase Crashlytics, Sentry, Google
Analytics, or any equivalent. This is a deliberate scope decision, not an
oversight — adding any of these later would be a notable change to the
project's privacy posture (`09-SECURITY-PRIVACY.md`) and should be called
out clearly to users if it's ever introduced (e.g., in Settings/about,
not silently bundled).

## 5. Diagnosing a User-Reported Issue (until crash reporting exists)

Since there's no remote crash visibility, troubleshooting a user's problem
currently depends on:
1. Reproducing locally using `17-TROUBLESHOOTING.md`'s known-issues list.
2. If needed, asking the user to share the agent's console/log output and
   `adb logcat` output around the time of the issue — manual, not automated.

This is an accepted limitation for the current project scale (personal/
small-scale use) and should be revisited if the project grows beyond
that — at which point, an opt-in (not default-on) crash reporting flow
would be the natural next step, gated by explicit user consent given the
privacy stance above.

## 6. Metrics (Not Implemented)

No usage metrics (feature-usage counts, session length, etc.) are collected
in v1. If product decisions later require usage data (e.g., to validate the
success metrics in `01-PRODUCT-REQUIREMENTS.md` §5 at scale beyond personal
use), that would require a deliberate, disclosed, opt-in telemetry feature
— not silent instrumentation added incrementally.
