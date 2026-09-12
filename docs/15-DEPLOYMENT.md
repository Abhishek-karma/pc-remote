# Deployment

## 1. Overview

Two separately-built, separately-distributed artifacts:

1. **Windows agent** — a .NET executable the user downloads and runs on
   their PC.
2. **Android app** — an APK/AAB installed on the phone (Play Store or
   sideloaded during development).

There is no server infrastructure to deploy for v1 (`05-TECHNICAL-ARCHITECTURE.md`
§1) — "deployment" here means packaging and distributing these two client
artifacts, not standing up any hosted service.

## 2. Windows Agent Build & Packaging

**Current state:** run via `dotnet run` from source (see
`windows-agent/README.md`) — fine for development, not suitable for
distributing to non-technical users. No admin rights or `netsh` steps are
needed to run the agent: it binds a plain TCP port and serves WSS with its
own self-signed certificate (`05-TECHNICAL-ARCHITECTURE.md` §3).

**Planned release packaging:**
- Publish as a self-contained, single-file executable:
  ```bash
  dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  ```
  This avoids requiring the end user to install the .NET runtime separately.
- Wrap the published executable in an installer (e.g., Inno Setup or MSIX)
  that:
  - Adds the Windows Firewall exceptions automatically: TCP `58642` for the
    WebSocket listener, plus UDP `5353` inbound for mDNS advertisement (see
    `windows-agent/README.md`'s manual `netsh advfirewall` steps — this
    should be automated by the installer, not left as a manual step for
    end users).
  - Optionally registers the agent to start on login (as a Scheduled Task
    or Startup entry) — should be a user-facing choice during install, not
    silently forced on.
  - Optionally requests the one-time `netsh http add urlacl` elevation
    during install — **no longer required** since the agent no longer uses
    `HttpListener`, but harmless if kept for legacy instructions.
- **Code signing:** the executable/installer should be code-signed before
  wide distribution — unsigned Windows executables trigger SmartScreen
  warnings that will scare away non-technical users. Requires acquiring a
  code-signing certificate.

## 3. Android App Build & Distribution

**Current state:** standard Android Studio / Gradle project structure
(`android-app/`), buildable and runnable via `./gradlew installDebug` or
Android Studio's Run button onto a device/emulator on the same network as
a running agent.

**Planned release packaging:**
- Build a signed release AAB (`./gradlew bundleRelease`) using a proper
  release keystore (never commit the keystore or its passwords to the
  repo — store via a secrets manager or local `.properties` file excluded
  from version control).
- Distribute via:
  - **Google Play internal testing track** first (fastest path to real
    device testing without a public listing), then
  - **Closed/open testing track**, then
  - **Production** once `16-RELEASE-CHECKLIST.md` is fully green.
- App requires a Play Store listing (screenshots, description, privacy
  policy URL) before any public track — privacy policy content should
  accurately reflect the no-cloud/no-telemetry stance from
  `09-SECURITY-PRIVACY.md` and `14-OBSERVABILITY-LOGGING.md`.

## 4. Versioning

- Both artifacts should share a coordinated version number while the
  protocol is unversioned (`07-API-SPECIFICATION.md` §6) — e.g., "v0.3.0"
  released for both agent and app together, since a mismatched pair could
  behave unpredictably until a `protocolVersion` field exists.
- Once a `protocolVersion` field is added to the `auth` handshake, agent and
  app versions can diverge more safely, and the agent/app should reject or
  warn on an incompatible protocol version rather than silently
  misbehaving.

## 5. CI/CD (Planned, Not Yet Set Up)

Suggested GitHub Actions structure once the project has enough usage to
justify the setup cost:

```
.github/workflows/
├── windows-agent-ci.yml   # dotnet build + test on windows-latest, on every PR
├── android-app-ci.yml     # gradle build + unit/Compose tests on ubuntu-latest, on every PR
└── release.yml            # triggered on a version tag: publish signed agent
                            # installer + signed AAB as GitHub Release assets
```

Until this exists, builds and releases are manual — see §2 and §3 above for
the manual commands.

## 6. Rollback Plan

- **Windows agent:** since it's a standalone executable with no
  auto-update mechanism in v1, "rollback" means the user manually
  re-downloads a previous release from GitHub Releases. No forced-update
  mechanism exists or is planned for v1 — see `01-PRODUCT-REQUIREMENTS.md`
  for the deliberately minimal, non-hosted scope.
- **Android app:** standard Play Store staged rollout percentage and
  halt/rollback controls, once the app is on a production track.

## 7. Environment Configuration

There are no environment-specific configs (dev/staging/prod) in the usual
backend sense, since there's no backend. The only "configuration" is the
agent's listening port (currently hardcoded to `58642` in `Program.cs`) —
consider making this user-configurable (agent settings) if port conflicts
become a reported issue.
