// PC Remote - log mirroring
//
// Mirrors console output into %AppData%\PcRemoteAgent\logs\agent-<date>.log so
// an agent that runs at startup (no visible console) can still be diagnosed
// and its pairing code read — see docs/14-OBSERVABILITY-LOGGING.md §2.
// Keeps the last 7 days of logs.

using System.Text;

namespace PcRemoteAgent;

public static class AgentLog
{
    private const int RetentionDays = 7;

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PcRemoteAgent", "logs");

    /// <summary>Redirects Console output to console + log file. Never throws —
    /// on failure the agent keeps console-only logging.</summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            PruneOldLogs();
            var file = Path.Combine(LogDir, $"agent-{DateTime.Now:yyyyMMdd}.log");
            // FileShare.ReadWrite: another agent instance (or an editor) may
            // hold the same daily log open — a tray agent must never lose its
            // log to a sharing violation.
            var writer = new StreamWriter(
                new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
            Console.SetOut(new DualWriter(Console.Out, writer));
            Console.WriteLine($"[log] writing to {file}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Log file unavailable ({ex.Message}); console only");
        }
    }

    /// <summary>Opens the log folder in Explorer (tray menu shortcut).</summary>
    public static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{LogDir}\"");
        }
        catch { /* best effort */ }
    }

    private static void PruneOldLogs()
    {
        foreach (var f in Directory.GetFiles(LogDir, "agent-*.log"))
        {
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetentionDays))
                File.Delete(f);
        }
    }

    private sealed class DualWriter(TextWriter console, TextWriter file) : TextWriter
    {
        private readonly object _lock = new();

        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            lock (_lock) { console.Write(value); file.Write(value); }
        }

        public override void Write(string? value)
        {
            lock (_lock) { console.Write(value); file.Write(value); file.Flush(); }
        }

        public override void WriteLine(string? value)
        {
            lock (_lock) { console.WriteLine(value); file.WriteLine(value); file.Flush(); }
        }
    }
}