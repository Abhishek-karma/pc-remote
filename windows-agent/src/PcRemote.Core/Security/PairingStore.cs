// Pairing and persistent trust tokens.
//
// Model: the PC shows a short six-digit code. The phone sends it once and gets a
// long random token back, which it stores and sends on every later connection.
// After that the code is never needed again — revoking a phone means deleting
// its token, which the tray can do.
//
// The token list is DPAPI-encrypted at rest under LocalSystem scope, in a
// directory whose ACL excludes ordinary users (the installer sets it). Neither
// the code nor the token is ever logged.

using System.Security.Cryptography;
using System.Text.Json;

namespace PcRemote.Core;

public sealed class PairingStore
{
    /// <summary>Failed pairings allowed per IP before a temporary block. The code
    /// is only six digits, so guessing is cheap without this.</summary>
    private const int MaxFailuresPerIp = 5;

    private static readonly TimeSpan BlockFor = TimeSpan.FromMinutes(2);

    /// <summary>Service-owned mutable state lives under ProgramData, never beside
    /// the exe (Program Files is read-only for the service account).</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCRemote");

    public static string LogDir => Path.Combine(DataDir, "logs");

    private readonly string _tokensFile = Path.Combine(DataDir, "paired-devices.dat");
    private static string LegacyTokensFile => Path.Combine(DataDir, "trusted-devices.json");
    private readonly object _gate = new();
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, DateTime BlockedUntil)> _blocked = new(StringComparer.Ordinal);

    private string _currentCode = "";
    private DateTime _codeIssuedUtc = DateTime.MinValue;

    public PairingStore()
    {
        Load();
    }

    /// <summary>The code the user reads off the PC. Reissued once it is older
    /// than five minutes so a photo of it does not stay useful.</summary>
    public string CurrentCode
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _codeIssuedUtc > TimeSpan.FromMinutes(5)) NewCode();
                return _currentCode;
            }
        }
    }

    public void RotateCode()
    {
        lock (_gate) NewCode();
    }

    private void NewCode()
    {
        _currentCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _codeIssuedUtc = DateTime.UtcNow;
    }

    /// <summary>True when this IP has failed to pair too often recently.</summary>
    public bool IsBlocked(string clientIp)
    {
        lock (_gate)
        {
            return _blocked.TryGetValue(clientIp, out var entry) && entry.BlockedUntil > DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Validates a saved token or the displayed code. A failed attempt counts
    /// against the caller; check <see cref="IsBlocked"/> first.
    /// </summary>
    public bool TryAuthenticate(string? token, string? code, string clientIp)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(token) && _tokens.Contains(token)) return true;

            if (string.IsNullOrEmpty(code) || code != _currentCode)
            {
                RecordFailure(clientIp);
                return false;
            }
            return true;
        }
    }

    private void RecordFailure(string clientIp)
    {
        var (failures, blockedUntil) = _blocked.TryGetValue(clientIp, out var entry)
            ? entry
            : (0, DateTime.MinValue);
        failures++;
        if (failures >= MaxFailuresPerIp) blockedUntil = DateTime.UtcNow + BlockFor;
        _blocked[clientIp] = (failures, blockedUntil);
    }

    /// <summary>Returns the token to store: the one that was presented, or a new
    /// one when this is a first-time pairing.</summary>
    public string IssueTokenIfNeeded(string? presented)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(presented) && _tokens.Contains(presented)) return presented;

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _tokens.Add(token);
            Save();
            Log.Info($"paired a new device ({_tokens.Count} trusted)");
            return token;
        }
    }

    /// <summary>Forgets every phone. Each will have to pair again.</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            if (_tokens.Count == 0) return;
            _tokens.Clear();
            Save();
            Log.Info("revoked all paired devices");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_tokensFile))
            {
                var protectedBytes = File.ReadAllBytes(_tokensFile);
                var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                var saved = JsonSerializer.Deserialize<string[]>(plain);
                if (saved is not null)
                    lock (_gate) _tokens.UnionWith(saved);
            }
        }
        catch (Exception ex)
        {
            // Most likely a changed data-dir ACL or a rotated DPAPI key. Either
            // way, treating every phone as unpaired is the safe reading.
            Log.Error($"could not read paired devices ({ex.Message}); all phones must pair again");
        }

        MigrateLegacyTokenStore();
    }

    /// <summary>The pre-0.2.1 store kept tokens as plaintext JSON, readable by
    /// every local user. Import them once so an upgrade does not unpair the
    /// user's phone, then delete the plaintext file. Only runs when no DPAPI
    /// store exists yet — the encrypted store, when present, is authoritative.</summary>
    private void MigrateLegacyTokenStore()
    {
        try
        {
            if (!File.Exists(LegacyTokensFile)) return;
            var tokens = JsonSerializer.Deserialize<string[]>(File.ReadAllText(LegacyTokensFile));
            if (tokens is { Length: > 0 })
            {
                lock (_gate)
                {
                    if (_tokens.Count > 0) return;
                    _tokens.UnionWith(tokens);
                    Save();
                }
            }
            // Delete only after Save() actually wrote the encrypted file, so a
            // failed write can never lose trust material.
            if (File.Exists(_tokensFile)) File.Delete(LegacyTokensFile);
            Log.Info("migrated the legacy token store into the encrypted format");
        }
        catch (Exception ex)
        {
            Log.Warn($"legacy token store migration failed ({ex.Message}); the plaintext file stays");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var plain = JsonSerializer.SerializeToUtf8Bytes(_tokens);
            var bytes = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);

            // Write-then-rename: a crash mid-write must not destroy the trust
            // store and force every phone to re-pair. The ACL is applied to the
            // temp file before the rename because the renamed file keeps the
            // source's security descriptor.
            var temp = _tokensFile + ".tmp";
            File.WriteAllBytes(temp, bytes);
            FilePermissions.RestrictToSystemAndAdmins(temp);
            File.Move(temp, _tokensFile, overwrite: true);
            FilePermissions.RestrictToSystemAndAdmins(_tokensFile);
        }
        catch (Exception ex)
        {
            Log.Error($"could not save paired devices ({ex.Message}); they stay paired until restart");
        }
    }
}