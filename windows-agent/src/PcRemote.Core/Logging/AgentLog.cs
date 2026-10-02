// PC Remote - log mirroring
//
// Mirrors console output into a rotating daily log file so a background
// service (no console) can still be diagnosed. Each component initializes
// with its own directory:
//   * service: %ProgramData%\PCRemote\logs\service-<date>.log
//   * session helper: %ProgramData%\PCRemote\logs\session-<date>.log
//   * tray:        %LocalAppData%\PCRemote\logs\tray-<date>.log
// Keeps the last 7 days. NEVER log pairing codes, tokens or keystrokes —
// only connection metadata and error messages.

using System.Text;

namespace PcRemote.Core;

public static class AgentLog
{
    private const int RetentionDays = 7;

    private static string LogDir { get; set; } = "";

    /// <summary>Redirects Console output to console + log file. Never throws —
    /// on failure the component keeps console-only logging.</summary>
    public static void Init(string logDir)
    {
        LogDir = logDir;
        try
        {
            Directory.CreateDirectory(LogDir);
            PruneOldLogs();
            var file = Path.Combine(LogDir, $"{AppDomain.CurrentDomain.FriendlyName.Split('.')[0]}-{DateTime.Now:yyyyMMdd}.log");
            // FileShare.ReadWrite: another instance (or an editor) may hold the
            // same daily log open — a background process must never lose its
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

    public static string CurrentLogDir => LogDir;

    private static void PruneOldLogs()
    {
        foreach (var f in Directory.GetFiles(LogDir, "*.log"))
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
            // Must be Write(string): Write(string, object, object) would treat
            // log text as a format string and throw on any '{' it contains.
            lock (_lock) { console.Write(value); file.Write(value); file.Flush(); }
        }

        public override void WriteLine(string? value)
        {
            lock (_lock) { console.WriteLine(value); file.WriteLine(value); file.Flush(); }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_lock) { console.Write(buffer, index, count); file.Write(buffer, index, count); }
        }

        public override void Flush()
        {
            lock (_lock) { console.Flush(); file.Flush(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { lock (_lock) { file.Flush(); file.Dispose(); } }
            base.Dispose(disposing);
        }
    }
}
