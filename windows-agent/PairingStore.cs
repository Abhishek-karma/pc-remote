// Persistent pairing code and DPAPI-encrypted token authentication store.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcRemoteAgent;

public class PairingStore
{
    internal static TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(5);
    internal static int MaxPairingFailures { get; set; } = 5;
    internal static TimeSpan PairingLockout { get; set; } = TimeSpan.FromMinutes(2);

    private string _currentPairingCode = "";
    private DateTime _codeGeneratedAtUtc = DateTime.MinValue;
    private readonly HashSet<string> _trustedTokens = [];
    private readonly string _tokensFile;

    private sealed class FailureRecord
    {
        public int Count;
        public DateTime LockedUntilUtc = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, FailureRecord> _pairingFailures = new();
    private readonly object _lock = new();

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcRemoteAgent");

    internal static string DefaultTokensFile => Path.Combine(AppDataDir, "trusted-devices.json");

    public PairingStore(string? tokensFilePath = null)
    {
        _tokensFile = tokensFilePath ?? DefaultTokensFile;
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

            if (!string.IsNullOrEmpty(token) && _trustedTokens.Contains(token))
            {
                _pairingFailures.TryRemove(clientIp, out _);
                return true;
            }

            if (!string.IsNullOrEmpty(pairingCode)
                && pairingCode == _currentPairingCode
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

            var newToken = Guid.NewGuid().ToString("N");
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
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
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
            Console.WriteLine($"[!] Could not load trusted devices ({ex.Message}); starting fresh");
        }
    }

    private void SaveTokens()
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(_trustedTokens);
            var bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var dir = Path.GetDirectoryName(_tokensFile)!;
            Directory.CreateDirectory(dir);

            var tempFile = Path.Combine(dir, $"{Path.GetFileName(_tokensFile)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tempFile, bytes);
            File.Move(tempFile, _tokensFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Could not persist trusted devices ({ex.Message}); tokens are in-memory only for this run");
        }
    }
}
