// Service-side IPC coordinator: routes tray/session requests with explicit
// privilege checks. Privileged operations (revoke all, update apply, anything
// touching the secure path) are refused to unelevated callers — see
// IpcSecurity.GetCallerPrivilege.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PcRemote.Core;

namespace PcRemote.Service;

public sealed class IpcCoordinator
{
    private readonly PairingStore _pairing;
    private readonly string _pcId;
    private readonly InputRouter _inputRouter;
    private readonly SessionManager _sessionManager;
    private readonly CancellationTokenSource _cts;
    private readonly UpdateCoordinator _updater;

    public ControlChannel? Channel { get; set; }

    public IpcCoordinator(PairingStore pairing, string pcId, InputRouter inputRouter,
        SessionManager sessionManager, CancellationTokenSource cts)
    {
        _pairing = pairing;
        _pcId = pcId;
        _inputRouter = inputRouter;
        _sessionManager = sessionManager;
        _cts = cts;
        _updater = new UpdateCoordinator();
    }

    public IpcMessage HandleIpc(IpcMessage request, IpcPrivilege privilege)
    {
        switch (request.Type)
        {
            case "status":
                return new IpcMessage
                {
                    Type = "status",
                    Ok = true,
                    ServiceState = "running",
                    // The pairing code is trust material: anyone who reads it can
                    // pair a device. The pipe ACL admits all local Users, so it
                    // is only returned to an elevated (admin/SYSTEM) caller —
                    // the unelevated tray shows "run as administrator to pair".
                    PairingCode = privilege == IpcPrivilege.Elevated ? _pairing.CurrentCode : null,
                    ConnectedDevices = Channel?.ConnectedCount ?? 0,
                    SessionState = _sessionManager.CurrentState.ToString().ToLowerInvariant(),
                };

            case "generate_pairing_code":
            {
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                var code = _pairing.GeneratePairingCode();
                return new IpcMessage { Type = request.Type, Ok = true, PairingCode = code };
            }

            case "revoke_all_devices":
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                _pairing.ClearAllTokens();
                return new IpcMessage { Type = request.Type, Ok = true };

            case "migrate_tokens":
                // Writes trust material (the trusted-device set). The pipe ACL
                // admits every local user, so an unguarded handler here would
                // let any unprivileged local process inject a token and then
                // authenticate over WSS as a paired device. Elevated only.
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                // Legacy tray reads its CurrentUser-DPAPI file and hands the
                // token list over. One-way; the service re-encrypts under
                // LocalMachine scope. No tokens are returned or logged.
                if (request.Payload is not { ValueKind: JsonValueKind.Array } arr)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "bad_payload" };
                var tokens = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "")
                    .Where(s => s.Length > 0).ToList();
                _pairing.ImportLegacyTokens(tokens);
                return new IpcMessage { Type = request.Type, Ok = true, ConnectedDevices = tokens.Count };

            case "desktop_report":
                // Only the secure-input helper (SYSTEM) reports desktop state.
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                _sessionManager.ReportDesktopState(request.SessionState ?? "unknown");
                return new IpcMessage { Type = request.Type, Ok = true };

            case "update_check":
                // Network I/O in a pipe handler would block the accept loop and
                // is reachable by every local user, so it is elevated-gated too.
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                var check = _updater.CheckForUpdate().GetAwaiter().GetResult();
                return new IpcMessage
                {
                    Type = request.Type,
                    Ok = true,
                    ServiceState = check.UpdateAvailable ? "update_available" : "up_to_date",
                    Version = check.LatestVersion,
                };

            case "update_apply":
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                _ = _updater.CheckAndApplyAsync();
                return new IpcMessage { Type = request.Type, Ok = true, ServiceState = "updating" };

            default:
                return new IpcMessage { Type = request.Type, Ok = false, Error = "unknown_type" };
        }
    }
}

/// <summary>
/// Windows updater (requirement 12): download → SHA-256 verify →
/// Authenticode verify → stage → hand off to the signed installer. The
/// service never executes an unverified binary; the installer (Inno Setup)
/// stops the service, replaces files and restarts it — rollback is the
/// installer's [UninstallDelete]/backup semantics plus SCM recovery.
/// </summary>
public sealed class UpdateCoordinator
{
    private const string ApiUrl = "https://api.github.com/repos/Abhishek-karma/pc-remote/releases/latest";
    private static string StageDir => Path.Combine(PairingStore.ServiceDataDir, "update");

    public record UpdateCheckResult(bool UpdateAvailable, string LatestVersion, string DownloadUrl, string Sha256, string ReleaseNotes);

    public async Task<UpdateCheckResult> CheckForUpdate()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "PCRemoteService");
        client.Timeout = TimeSpan.FromSeconds(10);

        var json = await client.GetStringAsync(ApiUrl);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "";
        var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

        string exeUrl = "", exeSha = "";
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
                    exeUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (name.EndsWith("Setup.exe.sha256", StringComparison.OrdinalIgnoreCase))
                {
                    // Convention: a sidecar asset holding the hex digest of the Setup exe.
                    var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    try { exeSha = (await client.GetStringAsync(url)).Trim().Split(' ')[0]; }
                    catch { exeSha = ""; }
                }
            }
        }

        var current = VersionDisplay.TrimStart('v', 'V');
        var latest = tagName.TrimStart('v', 'V');
        // Compare numerically: a plain string inequality would report an update
        // for a DOWNGRADE and for local builds like 0.2.0.1 vs 0.2.0.
        var isNewer = exeUrl.Length > 0 && IsNewerVersion(latest, current);

        return new UpdateCheckResult(isNewer, tagName, exeUrl, exeSha, notes);
    }

    /// <summary>Full pipeline. Only ever launches an artifact whose SHA-256
    /// matches the published digest AND whose Authenticode signature verifies
    /// with a publisher chain trusted by the machine.</summary>
    public async Task<bool> CheckAndApplyAsync()
    {
        var check = await CheckForUpdate();
        if (!check.UpdateAvailable)
        {
            Console.WriteLine("[update] already up to date");
            return false;
        }
        if (string.IsNullOrEmpty(check.Sha256))
        {
            Console.WriteLine("[update] refusing to update: release has no published SHA-256");
            return false;
        }

        Directory.CreateDirectory(StageDir);
        var staged = Path.Combine(StageDir, "PC-Remote-Setup.exe");

        Console.WriteLine($"[update] downloading {check.DownloadUrl}");
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("User-Agent", "PCRemoteService");
            var bytes = await client.GetByteArrayAsync(check.DownloadUrl);
            await File.WriteAllBytesAsync(staged, bytes);
        }

        // 1. Digest check
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged))).ToLowerInvariant();
        if (!string.Equals(actual, check.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[update] SHA-256 mismatch (expected {check.Sha256}, got {actual}); aborting");
            File.Delete(staged);
            return false;
        }

        // 2. Authenticode check
        if (!AuthenticodeVerifier.IsTrusted(staged))
        {
            Console.WriteLine("[update] Authenticode signature missing or untrusted; aborting");
            File.Delete(staged);
            return false;
        }

        // 3. Launch the (verified) installer silently; it stops this service,
        //    replaces binaries and restarts it. Inno Setup semantics.
        Console.WriteLine("[update] launching verified installer (silent)");
        Process.Start(new ProcessStartInfo(staged, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true
        });
        return true;
    }

    private static string VersionDisplay =>
        (System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>True when <paramref name="candidate"/> is a strictly newer
    /// release than <paramref name="current"/>. Tolerates a leading "v" and
    /// pre-release/build suffixes. Unparseable versions are treated as
    /// "not newer" so a malformed tag can never trigger an install.</summary>
    internal static bool IsNewerVersion(string candidate, string current)
    {
        static (int[]? Parts, string Pre) Parse(string v)
        {
            var s = v.Trim();
            if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
            // Split off pre-release/build metadata: 1.2.3-beta+5
            var cut = s.IndexOfAny(new[] { '-', '+' });
            var pre = cut >= 0 ? s[cut..] : "";
            if (cut >= 0) s = s[..cut];
            var parts = s.Split('.').Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();
            return (parts.Length > 0 ? parts : null, pre);
        }

        var (candParts, _) = Parse(candidate);
        var (curParts, _) = Parse(current);
        if (candParts is null || curParts is null) return false;

        for (int i = 0; i < Math.Max(candParts.Length, curParts.Length); i++)
        {
            int c = i < candParts.Length ? candParts[i] : 0;
            int u = i < curParts.Length ? curParts[i] : 0;
            if (c > u) return true;
            if (c < u) return false;
        }

        // Numerically equal (e.g. "0.2.0" vs "0.2.0-beta"): not newer.
        return false;
    }
}

/// <summary>Authenticode verification via WinVerifyTrust. A staged update with
/// no valid, trusted signature is never executed (requirement 12/13).</summary>
public static class AuthenticodeVerifier
{
    private static Guid WintrustActionGenericVerifyV2 => new("00AAC56B-CD44-11d0-822A-00AAA005889B1");

    public static bool IsTrusted(string filePath)
    {
        try
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
            };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                dwStateAction = 0, // WTD_STATEACTION_VERIFY
                pFile = file,
            };

            var actionId = WintrustActionGenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
            return result == 0; // ERROR_SUCCESS / TRUST_E_NOSIGNATURE != 0 etc.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[update] signature verification error: {ex.Message}");
            return false;
        }
    }

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        // Union, dwUnionChoice==WTD_CHOICE_FILE: exactly one member overlays here.
        public WINTRUST_FILE_INFO pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
