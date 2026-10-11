// PC Remote Windows Agent - Automatic In-App Updater
// Checks GitHub Releases API for a newer version, downloads it, verifies it
// against the release's SHA256SUMS.txt, and only then hands it to the
// self-installer. Running an unverified network download as an admin-level
// self-updater is the classic remote-execution hole, so verification is
// mandatory, not best-effort.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcRemoteAgent;

public record UpdateCheckResult(
    bool UpdateAvailable,
    string LatestVersion,
    string DownloadUrl,
    string ChecksumsUrl,
    string ReleaseNotes);

public static class AppUpdater
{
    private const string ApiUrl = "https://api.github.com/repos/Abhishek-karma/pc-remote/releases/latest";
    private const string ChecksumsAssetName = "SHA256SUMS.txt";

    private static readonly HttpClient Shared = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static AppUpdater()
    {
        Shared.DefaultRequestHeaders.Add("User-Agent", "PcRemoteAgent");
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            var jsonStr = await Shared.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            string body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

            string downloadUrl = "";
            string checksumsUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = url;
                    }
                    else if (string.Equals(name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        checksumsUrl = url;
                    }
                }
            }

            string currentVersion = Program.VersionDisplay.TrimStart('v', 'V');
            string latestVersion = tagName.TrimStart('v', 'V');

            bool isNewer = !string.IsNullOrEmpty(latestVersion) &&
                           !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrEmpty(downloadUrl) &&
                           !string.IsNullOrEmpty(checksumsUrl);

            // Without a checksum file the download cannot be verified, and an
            // unverified download is never applied — report "no update" rather
            // than offering an update the agent would then have to refuse.
            return new UpdateCheckResult(isNewer, tagName, downloadUrl, checksumsUrl, body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Check for update notice: {ex.Message}");
            return new UpdateCheckResult(false, "", "", "", "");
        }
    }

    public static Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, Action<int>? progressCallback = null) =>
        DownloadAndApplyUpdateAsync(downloadUrl, checksumsUrl: null, progressCallback);

    public static async Task<bool> DownloadAndApplyUpdateAsync(
        string downloadUrl,
        string? checksumsUrl,
        Action<int>? progressCallback = null)
    {
        try
        {
            if (string.IsNullOrEmpty(downloadUrl)) return false;

            string tempPath = Path.Combine(Path.GetTempPath(), "PcRemoteAgent-Update.exe");

            using var response = await Shared.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                if (totalBytes > 0 && progressCallback != null)
                {
                    int pct = (int)((totalRead * 100) / totalBytes);
                    progressCallback(pct);
                }
            }

            await fileStream.FlushAsync();

            var fileName = FileNameFromUrl(downloadUrl);
            if (!await VerifyDownloadAsync(tempPath, checksumsUrl, fileName))
            {
                // Fail closed: never execute something we could not verify.
                TryDelete(tempPath);
                return false;
            }

            // Spawn the verified executable (which runs InstallerHelper to
            // overwrite %LocalAppData%\PCRemote\PcRemoteAgent.exe and restart).
            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });

            // Exit current application instance
            Application.Exit();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Update download failed: {ex.Message}");
            TryDelete(Path.Combine(Path.GetTempPath(), "PcRemoteAgent-Update.exe"));
            return false;
        }
    }

    /// <summary>
    /// Fetches the release's SHA256SUMS.txt and compares the entry for
    /// [assetName] against the hash of [filePath]. Returns false when the
    /// checksum file, the entry, or the hash itself cannot be established.
    /// </summary>
    internal static async Task<bool> VerifyDownloadAsync(string filePath, string? checksumsUrl, string assetName)
    {
        if (string.IsNullOrWhiteSpace(checksumsUrl))
        {
            AgentLog.Warn("[!] Update refused: release published no checksum file");
            return false;
        }

        string? expected = null;
        try
        {
            var text = await Shared.GetStringAsync(checksumsUrl);
            expected = ParseChecksumFor(text, assetName);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"[!] Update refused: could not read {ChecksumsAssetName} ({ex.Message})");
            return false;
        }

        if (string.IsNullOrWhiteSpace(expected))
        {
            AgentLog.Warn($"[!] Update refused: {ChecksumsAssetName} has no entry for {assetName}");
            return false;
        }

        string actual;
        try
        {
            await using var stream = File.OpenRead(filePath);
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"[!] Update refused: could not hash the downloaded file ({ex.Message})");
            return false;
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            AgentLog.Warn($"[!] Update refused: checksum mismatch for {assetName} (expected {expected}, got {actual})");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads `sha256sum` output ("&lt;hex&gt;  &lt;name&gt;") and returns the hash
    /// recorded for [assetName], or null when it is absent.
    /// </summary>
    internal static string? ParseChecksumFor(string sha256sumText, string assetName)
    {
        foreach (var rawLine in sha256sumText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // sha256sum writes "<hash>  <file>" (binary mode adds a leading '*').
            var separator = line.IndexOf(' ');
            if (separator <= 0) continue;

            var hash = line[..separator].Trim().TrimStart('*');
            var name = line[(separator + 1)..].Trim().TrimStart('*');
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                return hash;
        }
        return null;
    }

    private static string FileNameFromUrl(string url)
    {
        try
        {
            return Uri.UnescapeDataString(new Uri(url).Segments[^1]);
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
