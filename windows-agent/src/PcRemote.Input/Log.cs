// The helper's log. Deliberately tiny and local: the helper has no dependency on
// the service's libraries, no credentials and no network, so it needs no more
// logging than a startup line, a failure and the occasional slow relay.
//
// It appends to the same rotating log directory as the service so there is one
// place to look. Text being typed is NEVER logged.

using System.Text;

namespace PcRemote.Input;

internal static class Log
{
    private const int RetentionDays = 7;

    private static readonly object Gate = new();
    private static string _label = "input";
    private static StreamWriter? _writer;
    private static bool _failed;

    /// <summary>Opens today's log file. Failure is non-fatal: a helper that cannot
    /// write a log must still inject input.</summary>
    public static void Init(string directory, string label)
    {
        _label = label;
        lock (Gate)
        {
            if (_writer is not null || _failed) return;
            try
            {
                Directory.CreateDirectory(directory);
                foreach (var f in Directory.GetFiles(directory, "*.log"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetentionDays))
                        File.Delete(f);

                var file = Path.Combine(directory, $"input-{DateTime.Now:yyyyMMdd}.log");
                // FileShare.ReadWrite: the service may hold today's file open.
                _writer = new StreamWriter(
                    new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
            }
            catch (Exception ex)
            {
                _failed = true;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [WARN] log unavailable: {ex.Message}");
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] [{_label}] {message}";
        Console.WriteLine(line);
        lock (Gate)
        {
            try { _writer?.WriteLine(line); }
            catch (Exception) { /* a broken log must never break input */ }
        }
    }
}