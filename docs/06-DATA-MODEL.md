# Data Model

There is no shared/hosted database in this system — each side persists only
what it needs locally. This doc defines those local schemas plus the
in-memory session state, so both codebases stay consistent as features are
added.

## 1. Android App — Local Storage

### 1.1 Current: `SharedPreferences` ("pc_remote_prefs")

| Key pattern | Type | Description |
|---|---|---|
| `token_<host>` | String | Trust token issued by the agent at `<host>` after successful pairing |

This is implemented today via `TokenStore` in `RemoteConnection.kt`.

**Planned migration:** move to `EncryptedSharedPreferences`
(androidx.security-crypto) before any public release, since these tokens
grant control of the PC — see `09-SECURITY-PRIVACY.md` §4.

### 1.2 Planned: `PairedDevice` record (once Settings/multi-PC support exists)

```kotlin
data class PairedDevice(
    val host: String,          // last-known IP, e.g. "192.168.1.42"
    val hostname: String?,     // friendly name reported by the agent, if any
    val displayName: String,   // user-editable label, e.g. "Living Room PC"
    val token: String,         // trust token (encrypted at rest)
    val lastConnectedAt: Long, // epoch millis, for sorting/"last used"
    val port: Int = 58642
)
```

Stored as a JSON list under a single `paired_devices` key (or migrated to a
small Room/SQLite table if the list-management UI in Settings needs
querying beyond a simple list — not expected to be necessary at this scale).

### 1.3 Planned: App Settings

```kotlin
data class AppSettings(
    val touchpadSensitivity: Float = 1.5f,
    val darkThemeOverride: Boolean = true, // see 04-UI-UX-SPECIFICATION §3
    val clipboardSyncEnabledPerHost: Map<String, Boolean> = emptyMap()
)
```

## 2. Windows Agent — Local Storage

### 2.1 Current: In-memory only

`PairingStore` (`Program.cs`) holds `_trustedTokens` (a `HashSet<string>`)
and `_currentPairingCode` purely in memory — both are lost on agent restart,
meaning every restart currently requires re-pairing all devices.

### 2.2 Planned: Persisted trusted-devices file

Location: `%AppData%\PcRemoteAgent\trusted-devices.json` (per-user, not
system-wide, since control should be scoped to the Windows user who ran the
agent).

```json
{
  "trustedDevices": [
    {
      "token": "3f9c1a2b4e5d4f0a9b8c7d6e5f4a3b2c",
      "deviceLabel": "Pixel 8 (added 2026-09-01)",
      "pairedAt": "2026-09-01T18:22:00Z",
      "lastSeenAt": "2026-09-10T21:03:11Z"
    }
  ]
}
```

Should be written with restrictive file permissions (current-user only) and
ideally encrypted with `System.Security.Cryptography.ProtectedData` (DPAPI,
user-scope) rather than stored as plaintext JSON, since a token here is
equivalent to a password for controlling the PC.

### 2.3 Session state (in-memory, per active connection)

Not persisted — exists only for the lifetime of a WebSocket connection:

```csharp
class SessionState
{
    bool Authenticated;
    string? DeviceToken;
    string ClientIp;
    DateTime ConnectedAt;
}
```

## 3. Wire-Level Message Schema

The `RemoteMessage` shape is the contract between the two local schemas
above and is documented in full in `07-API-SPECIFICATION.md`. It is
intentionally a single flat schema (all optional fields) rather than a
per-type schema, to keep both the Kotlin and C# (de)serialization code
simple — see that doc for the full field table.

## 4. Data Retention & Deletion

- Deleting the Android app removes all local `SharedPreferences` data,
  including saved tokens — no server-side cleanup needed since nothing is
  server-side.
- "Forget this PC" (planned, `02-FEATURE-SPECIFICATION.md` F9) removes the
  `PairedDevice` entry and its token from the app's local storage.
- Uninstalling/deleting the agent's data folder removes
  `trusted-devices.json`, immediately invalidating all previously-issued
  tokens for that PC.
- No data from either side is ever transmitted to a third party or cloud
  service in v1 — see `09-SECURITY-PRIVACY.md` §5.
