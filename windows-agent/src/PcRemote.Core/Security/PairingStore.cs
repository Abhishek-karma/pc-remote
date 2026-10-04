// Persistent pairing code and DPAPI-encrypted token authentication store.
//
// Storage ownership: the Windows service owns this store.
// The tray and session helpers never touch the token file directly — they go
// through authenticated local IPC. Under the service the data protection
// scope is LocalMachine (stored under ProgramData with an ACL restricted to
// SYSTEM/Administrators); the legacy per-user scope is kept only for the
// one-time migration path that hands old tray-owned tokens to the service.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcRemote.Core;

public class PairingStore
{
    public static TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(5);
    internal static int MaxPairingFailures { get; set; } = 5;
    internal static TimeSpan PairingLockout { get; set; } = TimeSpan.FromMinutes(2);

    private string _currentPairingCode = "";
    private DateTime _codeGeneratedAtUtc = DateTime.MinValue;
    private readonly HashSet<string> _trustedTokens = [];
    private readonly string _tokensFile;
    private readonly DataProtectionScope _dpapiScope;

    private sealed class FailureRecord
    {
        public int Count;
        public DateTime LockedUntilUtc = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, FailureRecord> _pairingFailures = new();
    private readonly object _lock = new();

    public static string LegacyAppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcRemoteAgent");

    internal static string LegacyTokensFile => Path.Combine(LegacyAppDataDir, "trusted-devices.json");

    /// <summary>Service-owned mutable state lives under ProgramData, never beside
    /// the exe (Program Files is read-only for the service account).</summary>
    public static string ServiceDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCRemote");

    /// <summary>Rotating log directory shared by the service and its session
    /// helpers: %ProgramData%\PCRemote\logs. Kept here so the path cannot drift
    /// between components (and so docs/tests can reference one constant).</summary>
    public static string ServiceLogDir => Path.Combine(ServiceDataDir, "logs");

    public static string DefaultServiceTokensFile => Path.Combine(ServiceDataDir, "trusted-devices.json");

    public PairingStore(string? tokensFilePath = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _tokensFile = tokensFilePath ?? LegacyTokensFile;
        _dpapiScope = scope;
        LoadTokens();
    }

    public string GeneratePairingCode()
    {
        lock (_lock)
        {
            _currentPairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _codeGeneratedAtUtc = DateTime.UtcNow;
            return _currentPairingCode;
        }
    }

    public bool IsCodeExpired()
    {
        lock (_lock)
        {
            return DateTime.UtcNow - _codeGeneratedAtUtc > CodeLifetime;
        }
    }

    public string CurrentCode
    {
        get { lock (_lock) return _currentPairingCode; }
    }

    public bool IsIpLockedOut(string clientIp) =>
        _pairingFailures.TryGetValue(clientIp, out var rec) && rec.LockedUntilUtc > DateTime.UtcNow;

    public bool TryAuthenticate(string? token, string? pairingCode, string clientIp = "unknown")
    {
        lock (_lock)
        {
            if (IsIpLockedOut(clientIp)) return false;

            if (!string.IsNullOrEmpty(token) && TrustedTokensContains(token))
            {
                _pairingFailures.TryRemove(clientIp, out _);
                return true;
            }

            // Fixed-time comparison for the pairing code: a plain == leaks
            // information about how many leading digits were correct.
            if (!string.IsNullOrEmpty(pairingCode)
                && FixedTimeEquals(pairingCode, _currentPairingCode)
                && DateTime.UtcNow - _codeGeneratedAtUtc <= CodeLifetime)
            {
                _pairingFailures.TryRemove(clientIp, out _);
                _currentPairingCode = "";
                return true;
            }

            RecordFailedAttempt(clientIp);
            return false;
        }
    }

    /// <summary>Constant-time string comparison. Length is compared first because
    /// the pairing code is a fixed-width 6-digit value, and the caller has
    /// already established both operands are non-empty.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));
    }

    /// <summary>Constant-time membership test over the trusted-token set.
    /// HashSet.Contains short-circuits on the first mismatch, so tokens are
    /// compared byte-by-byte over the (small) set instead.</summary>
    private bool TrustedTokensContains(string token)
    {
        var found = false;
        foreach (var t in _trustedTokens)
        {
            if (FixedTimeEquals(t, token)) found = true;
        }
        return found;
    }

    private void RecordFailedAttempt(string clientIp)
    {
        PruneExpiredFailures();
        var rec = _pairingFailures.GetOrAdd(clientIp, _ => new FailureRecord());
        rec.Count++;
        if (rec.Count >= MaxPairingFailures)
        {
            rec.LockedUntilUtc = DateTime.UtcNow.Add(PairingLockout);
        }
    }

    public void PruneExpiredFailures()
    {
        var now = DateTime.UtcNow;
        if (_pairingFailures.Count > 100)
        {
            foreach (var (ip, rec) in _pairingFailures)
            {
                if (rec.LockedUntilUtc < now)
                {
                    _pairingFailures.TryRemove(ip, out _);
                }
            }
        }
    }

    public string IssueTokenIfNeeded(string? existingToken)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(existingToken) && _trustedTokens.Contains(existingToken))
                return existingToken;

            var newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _trustedTokens.Add(newToken);
            SaveTokens();
            return newToken;
        }
    }

    public bool RevokeToken(string token)
    {
        lock (_lock)
        {
            if (!_trustedTokens.Remove(token)) return false;
            SaveTokens();
            return true;
        }
    }

    public void ClearAllTokens()
    {
        lock (_lock)
        {
            _trustedTokens.Clear();
            SaveTokens();
        }
    }

    /// <summary>Imports tokens from the legacy tray-owned store (DPAPI CurrentUser
    /// scope — the caller must run in the original user's context to decrypt).
    /// Used once during migration from the pre-service agent.</summary>
    public IReadOnlyCollection<string> ImportLegacyTokens(IReadOnlyCollection<string> tokens)
    {
        lock (_lock)
        {
            var imported = 0;
            foreach (var t in tokens)
                if (!string.IsNullOrWhiteSpace(t) && _trustedTokens.Add(t))
                    imported++;
            if (imported > 0) SaveTokens();
            return _trustedTokens.ToArray();
        }
    }

    public IReadOnlyCollection<string> TrustedTokens
    {
        get { lock (_lock) return _trustedTokens.ToArray(); }
    }

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(_tokensFile)) return;
            var bytes = File.ReadAllBytes(_tokensFile);
            var plain = ProtectedData.Unprotect(bytes, null, _dpapiScope);
            var doc = JsonSerializer.Deserialize<HashSet<string>>(plain);
            if (doc is not null)
            {
                lock (_lock)
                {
                    _trustedTokens.UnionWith(doc);
                }
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Could not load trusted devices ({ex.Message}); starting fresh");
        }
    }

    private void SaveTokens()
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(_trustedTokens);
            var bytes = ProtectedData.Protect(plain, null, _dpapiScope);
            var dir = Path.GetDirectoryName(_tokensFile)!;
            Directory.CreateDirectory(dir);

            var tempFile = Path.Combine(dir, $"{Path.GetFileName(_tokensFile)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tempFile, bytes);
            File.Move(tempFile, _tokensFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Could not persist trusted devices ({ex.Message}); tokens are in-memory only for this run");
        }
    }
}
