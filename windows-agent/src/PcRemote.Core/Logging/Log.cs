// Leveled logging for the service and the tray.
//
// Writes each line to the console AND to a rotating daily file, so a process
// with no console (the Windows service) can still be diagnosed. The input helper
// has its own tiny logger and does not use this.
//
// Retains 7 days. NEVER log pairing codes, tokens or typed text - only
// connection metadata and error messages.

using System.Text;

namespace PcRemote.Core;

public static class Log
{
    private const int RetentionDays = 7;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string _subsystem = "";

    /// <summary>Labels every line from this process: "service" or "tray".</summary>
    public static void Init(string logDir, string subsystem)
    {
        _subsystem = subsystem;
        lock (Gate)
        {
            if (_writer is not null) return;
            try
            {
                Directory.CreateDirectory(logDir);
                PruneOldLogs(logDir);
                var file = Path.Combine(logDir, $"{subsystem}-{DateTime.Now:yyyyMMdd}.log");
                // FileShare.ReadWrite: another process (or an editor) may already
                // hold today's file open, and losing logs to that is worse.
                _writer = new StreamWriter(
                    new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
            }
            catch (Exception ex)
            {
                // Never fatal: a process that cannot log must still work.
                Console.WriteLine($"[!] log file unavailable ({ex.Message}); console only");
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] [{_subsystem}] {message}";
        Console.WriteLine(line);
        lock (Gate)
        {
            try { _writer?.WriteLine(line); }
            catch (Exception) { /* a broken log must never break control */ }
        }
    }

    /// <summary>Opens the log folder in Explorer (tray menu shortcut).</summary>
    public static void OpenLogsFolder()
    {
        try
        {
            var dir = PairingStore.LogDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            // Best effort only: the tray must not die because Explorer failed.
            Console.WriteLine($"[!] could not open the log folder ({ex.Message})");
        }
    }

    private static void PruneOldLogs(string logDir)
    {
        try
        {
            foreach (var file in Directory.GetFiles(logDir, "*.log"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-RetentionDays))
                    File.Delete(file);
        }
        catch (Exception)
        {
            // Pruning is housekeeping; failing it must not stop logging.
        }
    }
}