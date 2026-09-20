// PC Remote Windows Agent - Automatic In-App Updater
// Checks GitHub Releases API for new releases, downloads update executable, and triggers self-installer.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PcRemoteAgent;

public record UpdateCheckResult(bool UpdateAvailable, string LatestVersion, string DownloadUrl, string ReleaseNotes);

public static class AppUpdater
{
    private const string ApiUrl = "https://api.github.org/repos/Abhishek-karma/pc-remote/releases/latest";

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "PcRemoteAgent");
            client.Timeout = TimeSpan.FromSeconds(8);

            var jsonStr = await client.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? "";
            string body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

            string downloadUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        break;
                    }
                }
            }

            string currentVersion = Program.VersionDisplay.TrimStart('v', 'V');
            string latestVersion = tagName.TrimStart('v', 'V');

            bool isNewer = !string.IsNullOrEmpty(latestVersion) &&
                           !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrEmpty(downloadUrl);

            return new UpdateCheckResult(isNewer, tagName, downloadUrl, body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Check for update notice: {ex.Message}");
            return new UpdateCheckResult(false, "", "", "");
        }
    }

    public static async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, Action<int>? progressCallback = null)
    {
        try
        {
            if (string.IsNullOrEmpty(downloadUrl)) return false;

            string tempPath = Path.Combine(Path.GetTempPath(), "PcRemoteAgent-Update.exe");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "PcRemoteAgent");

            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
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

            fileStream.Close();

            // Spawn downloaded executable (which runs InstallerHelper to overwrite %LocalAppData%\PCRemote\PcRemoteAgent.exe and restart)
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
            return false;
        }
    }
}
