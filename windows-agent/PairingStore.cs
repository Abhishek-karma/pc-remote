// Pairing/auth store.
//
// Pairing codes are single-use-ish (expire after 5 minutes and rotate
// automatically) and trust tokens are persisted to a DPAPI-encrypted JSON
// file (%AppData%\PcRemoteAgent\trusted-devices.json) so devices stay paired
// across agent restarts (docs/06-DATA-MODEL.md §2.2, docs/09-SECURITY-PRIVACY.md §3–4).

using System.Security.Cryptography;
using System.Text.Json;

namespace PcRemoteAgent;

public class PairingStore
{
    /// <summary>5 minutes by default; settable so tests can shorten it.</summary>
    internal static TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    private string _currentPairingCode = "";
    private DateTime _codeGeneratedAtUtc = DateTime.MinValue;
    private readonly HashSet<string> _trustedTokens = new();
    private readonly string _tokensFile;

    private readonly object _lock = new();

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcRemoteAgent");

    internal static string DefaultTokensFile => Path.Combine(AppDataDir, "trusted-devices.json");

    public PairingStore() : this(null) { }

    /// <param name="tokensFilePath">Override the persistence location (tests use a temp file).</param>
    public PairingStore(string? tokensFilePath)
    {
        _tokensFile = tokensFilePath ?? DefaultTokensFile;
        LoadTokens();
    }

    /// <summary>Generates (or refreshes) the pairing code and returns it.</summary>
    public string GeneratePairingCode()
    {
        lock (_lock)
        {
            _currentPairingCode = Random.Shared.Next(0, 1_000_000).ToString("D6");
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

    public bool TryAuthenticate(string? token, string? pairingCode)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(token) && _trustedTokens.Contains(token))
                return true;

            return !string.IsNullOrEmpty(pairingCode)
                && pairingCode == _currentPairingCode
                && DateTime.UtcNow - _codeGeneratedAtUtc <= CodeLifetime;
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

    public IReadOnlyCollection<string> TrustedTokens
    {
        get { lock (_lock) { return _trustedTokens.ToList(); } }
    }

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(_tokensFile)) return;
            var bytes = File.ReadAllBytes(_tokensFile);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            var doc = JsonSerializer.Deserialize<HashSet<string>>(plain);
            if (doc != null)
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
            Directory.CreateDirectory(Path.GetDirectoryName(_tokensFile)!);
            File.WriteAllBytes(_tokensFile, bytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Could not persist trusted devices ({ex.Message}); tokens are in-memory only for this run");
        }
    }
}