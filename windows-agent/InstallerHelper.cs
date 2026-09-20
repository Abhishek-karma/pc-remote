// PC Remote Windows Agent - Self-Installer & Auto-Start Helper
// Ensures the agent installs itself to %LocalAppData%\PCRemote\PcRemoteAgent.exe
// and registers with Windows Startup so it survives deletion of temporary/downloaded executables.

using System.Diagnostics;
using System.IO;

namespace PcRemoteAgent;

internal static class InstallerHelper
{
    public static string InstalledDirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCRemote");

    public static string InstalledExePath =>
        Path.Combine(InstalledDirPath, "PcRemoteAgent.exe");

    /// <summary>
    /// Checks if the running process is located in the LocalAppData installation folder.
    /// If not, copies the binary and dependencies, configures startup, and launches the installed copy.
    /// Returns true if a child process was launched and current process should exit.
    /// </summary>
    public static bool EnsureInstalled()
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return false;

        string targetExe = InstalledExePath;

        // Already running from installed location
        if (string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            // Ensure Windows startup entry points to installed location
            StartupToggle.Set(true);
            return false;
        }

        try
        {
            Directory.CreateDirectory(InstalledDirPath);

            // Copy executable
            File.Copy(currentExe, targetExe, overwrite: true);

            // Copy sibling files if any
            string? sourceDir = Path.GetDirectoryName(currentExe);
            if (!string.IsNullOrEmpty(sourceDir) && Directory.Exists(sourceDir))
            {
                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals(Path.GetFileName(currentExe), StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Copy(file, Path.Combine(InstalledDirPath, name), overwrite: true); } catch { }
                }
            }

            // Register Windows startup registry key
            StartupToggle.Set(true);

            // Relaunch from installed location
            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Self-install notice: {ex.Message}");
            return false;
        }
    }
}
