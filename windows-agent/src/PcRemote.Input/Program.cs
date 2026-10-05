// PC Remote input helper — the only process that injects input.
//
// One binary, two modes selected by a command-line switch:
//
//   (default)   runs as the logged-on user and injects into the normal desktop.
//   --secure    runs as SYSTEM inside the console session and injects into
//               WinSta0\Winlogon, which is what the lock screen, the logon UI
//               and UAC prompts are drawn on.
//
// It talks to the service over one named pipe and does exactly three things:
// receive an already-authenticated command, validate it against an allowlist,
// and inject it. It has no network code, no credentials, no protocol of its own
// and no persistence.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcRemote.Input;

/// <summary>A command relayed over the helper pipe. Deliberately not the network
/// protocol type: the helper validates its own inputs and must not be able to
/// reach anything except the injector.</summary>
internal sealed class Command
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("dx")] public int? Dx { get; set; }
    [JsonPropertyName("dy")] public int? Dy { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("delta")] public int? Delta { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

internal static class Program
{
    private const int MaxFrameBytes = 64 * 1024;

    /// <summary>Longest text accepted in one message, and the largest cursor or
    /// scroll delta. Both bound the work the service can force on this process.</summary>
    private const int MaxTextChars = 4096;
    private const int MaxDelta = 20_000;

    private static async Task<int> Main(string[] args)
    {
        var secure = args.Contains("--secure");
        var logDir = args.FirstOrDefault(a => a.StartsWith("--log-dir=", StringComparison.Ordinal))?["--log-dir=".Length..]
                     ?? Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCRemote", "logs");
        Log.Init(logDir, secure ? "secure" : "input");

        // Attach to the secure desktop for the process lifetime, in secure mode
        // only, and before anything on this thread can inject.
        if (secure) SecureDesktop.AttachCurrentThreadToSecureInputDesktop();

        var sessionId = GetOwnSessionId();
        var pipeName = $"PCRemoteInput-{sessionId}-{(secure ? "secure" : "session")}";
        Log.Info($"input helper ready (secure={secure}, session={sessionId})");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await ServeAsync(pipeName, cts.Token);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log.Error($"helper stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The service is gone, or we are shutting down. Never leave the
            // desktop with a button or a modifier held down.
            Injector.ReleaseAll();
            Log.Info("input helper stopped");
        }
        return 0;
    }
private static async Task ServeAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 4096, outBufferSize: 4096, PipeAcl());
            }
            catch (Exception ex)
            {
                Log.Error($"pipe creation failed: {ex.Message}; retrying in 5 s");
                try { server?.Dispose(); } catch { /* already broken */ }
                await Task.Delay(5000, ct);
                continue;
            }

            await server.WaitForConnectionAsync(ct);
            var current = server;
            // One connection = one command, then close. A fresh pipe per command
            // keeps this stateless: nothing to corrupt, nothing to recover, and
            // the service owns all the sequencing.
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(current, ct); }
                catch (Exception ex) { Log.Warn($"pipe error: {ex.Message}"); }
                finally { try { current.Dispose(); } catch { /* nothing to do */ } }
            }, CancellationToken.None);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactAsync(server, lengthBytes, ct)) return;
        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > MaxFrameBytes) return;

        var body = new byte[length];
        if (!await ReadExactAsync(server, body, ct)) return;

        Command? command;
        try { command = JsonSerializer.Deserialize<Command>(Encoding.UTF8.GetString(body)); }
        catch (JsonException) { return; }
        if (command is null) return;

        var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = Execute(command) }));
        var replyLength = BitConverter.GetBytes(reply.Length);
        await server.WriteAsync(replyLength, ct);
        await server.WriteAsync(reply, ct);
        await server.FlushAsync(ct);
    }

    /// <summary>Validates and performs one command. Every field is bounded and
    /// every name is checked against an allowlist here, at the injection
    /// boundary — the helper does not trust the service blindly.</summary>
    private static bool Execute(Command c) => c.Op switch
    {
        "move" => Within(c.Dx) && Within(c.Dy) && Injector.MoveMouse(c.Dx ?? 0, c.Dy ?? 0),
        "button" => c.Button is not null && Injector.MouseButton(c.Button, c.Action ?? ""),
        "scroll" => Within(c.Delta) && Injector.Scroll(c.Delta ?? 0),
        "key" => c.Key is not null && Injector.PressKey(c.Key, c.Action ?? ""),
        "text" => c.Text is { Length: <= MaxTextChars } && Injector.TypeText(c.Text),
        "release_all" => ReleaseAll(),
        _ => Unknown(c.Op),
    };

    private static bool ReleaseAll()
    {
        Injector.ReleaseAll();
        return true;
    }

    private static bool Unknown(string op)
    {
        Log.Warn($"rejected unknown op '{op}'");
        return false;
    }

    private static bool Within(int? value) => value is null || Math.Abs(value.Value) <= MaxDelta;

    private static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>Only SYSTEM and Administrators may drive the input helper.
    /// Everyone else on the machine is locked out of injecting keystrokes.</summary>
    private static PipeSecurity PipeAcl()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>This process's real session id. Not the one WTS reports for the
    /// console session: once nobody is logged on WTS returns 0xFFFFFFFF, which
    /// would collide with the helper the service started earlier.</summary>
    private static int GetOwnSessionId() =>
        Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId) == 0
            ? (int)sessionId
            : 0;

    private static class Kernel32
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ProcessIdToSessionId(uint processId, out uint sessionId);
    }
}