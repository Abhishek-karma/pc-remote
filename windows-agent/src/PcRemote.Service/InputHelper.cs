// Launches PCRemoteInput.exe and relays commands to it over its named pipe.
//
// One helper binary serves both roles; the mode is a command-line switch
// (--secure), not a second executable. The two run under different tokens: the
// session helper as the logged-on user, the secure helper as SYSTEM inside the
// console session, because only SYSTEM can open WinSta0\Winlogon.
//
// This class never sees, buffers or logs a keystroke - it spawns a process and
// forwards opaque JSON.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PcRemote.Core;

namespace PcRemote.Service;

public sealed class InputHelper
{
    /// <summary>A relay is a synchronous round trip on the input path, so it must
    /// be short. Two seconds is far beyond a healthy SendInput and still bounded
    /// when a helper has wedged.</summary>
    private const int RelayTimeoutMs = 2000;

    private readonly Process _process;
    private readonly string _pipeName;
    private volatile bool _killed;

    private InputHelper(Process process, string pipeName)
    {
        _process = process;
        _pipeName = pipeName;
    }

    /// <summary>Never throws: several threads poll this while healing helpers,
    /// and an exception here would take down the state watcher.</summary>
    public bool IsAlive
    {
        get
        {
            if (_killed) return false;
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; } // already disposed
        }
    }

    /// <summary>Starts a helper, or returns null. Never throws: callers fall back
    /// to the other helper, or report the desktop as unavailable.</summary>
    public static InputHelper? Launch(uint sessionId, bool secure)
    {
        if (sessionId == 0) return null;

        var path = Path.Combine(AppContext.BaseDirectory, "PCRemoteInput.exe");
        if (!File.Exists(path))
        {
            Log.Error($"input helper missing: {path}");
            return null;
        }

        // The mode is part of the pipe name: both helpers run in the same console
        // session, and without it a lock-screen keystroke could be served by the
        // normal-desktop helper, or the reverse.
        var pipeName = $"PCRemoteInput-{sessionId}-{(secure ? "secure" : "session")}";

        try
        {
            using var token = secure
                ? DuplicateSystemTokenForSession(sessionId)
                : QueryUserToken(sessionId);
            if (token is null)
            {
                Log.Error($"no {(secure ? "SYSTEM" : "user")} token for session {sessionId}");
                return null;
            }

            if (!Native.CreateEnvironmentBlock(out var environment, token.DangerousGetHandle(), inherit: false))
                environment = IntPtr.Zero;

            try
            {
                var startup = new Native.STARTUPINFOW { cb = Marshal.SizeOf<Native.STARTUPINFOW>() };
                var commandLine = $"\"{path}\" {(secure ? "--secure" : "")} --log-dir=\"{PairingStore.LogDir}\"".TrimEnd();

                if (!Native.CreateProcessAsUser(
                        token.DangerousGetHandle(), null, commandLine,
                        IntPtr.Zero, IntPtr.Zero, inheritHandles: false,
                        Native.CREATE_NO_WINDOW | Native.CREATE_UNICODE_ENVIRONMENT,
                        environment, null, ref startup, out var created))
                {
                    Log.Error($"could not start input helper: {Marshal.GetLastWin32Error()}");
                    return null;
                }

                var process = Process.GetProcessById((int)created.dwProcessId);
                Native.CloseHandle(created.hThread);
                Native.CloseHandle(created.hProcess);
                return new InputHelper(process, pipeName);
            }
            finally
            {
                if (environment != IntPtr.Zero) Native.DestroyEnvironmentBlock(environment);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"starting input helper failed: {ex.Message}");
            return null;
        }
    }

    public void Kill()
    {
        _killed = true;
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch (Exception ex)
        {
            Log.Warn($"stopping input helper failed: {ex.Message}");
        }
    }

    public Task Move(int dx, int dy) => Relay("move", new { dx, dy });
    public Task Button(string button, string action) => Relay("button", new { button, action });
    public Task Scroll(int notches) => Relay("scroll", new { delta = notches });
    public Task Key(string key, string action) => Relay("key", new { key, action });
    public Task Text(string text) => Relay("text", new { text });
    public Task ReleaseAll() => Relay("release_all", new { });

    /// <summary>Asks the helper which desktop is currently receiving input.
    /// Answered by the secure helper with "Winlogon" or "Default"; the session
    /// manager polls this to notice UAC prompts, which emit no WTS event.</summary>
    public async Task<string?> QueryDesktop()
    {
        var reply = await RoundTrip("desktop", new { });
        if (reply is null) return null;
        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.TryGetProperty("desktop", out var name) ? name.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Sends one command and waits for the helper's "did it work"
    /// answer. A failed relay is reported as false rather than thrown: dropped
    /// input beats a dead listener. Move and scroll failures are not logged —
    /// they arrive in a continuous stream, so a dead helper mid-drag would
    /// flood the log; the session manager respawns the helper within one tick.</summary>
    private async Task<bool> Relay(string op, object payload)
    {
        var reply = await RoundTrip(op, payload);
        if (reply is null) return false;

        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<byte[]?> RoundTrip(string op, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { op, payload }));

        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(RelayTimeoutMs);
            await pipe.ConnectAsync(RelayTimeoutMs, cts.Token);

            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), cts.Token);
            await pipe.WriteAsync(bytes, cts.Token);
            await pipe.FlushAsync(cts.Token);

            var lengthBytes = await ReadExactlyAsync(pipe, 4, cts.Token);
            var length = BitConverter.ToInt32(lengthBytes);
            if (length is <= 0 or > 4096) return null;

            return await ReadExactlyAsync(pipe, length, cts.Token);
        }
        catch (Exception ex)
        {
            if (op is not ("move" or "scroll")) Log.Warn($"relay '{op}' failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(PipeStream pipe, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) throw new IOException("helper closed the pipe");
            read += n;
        }
        return buffer;
    }

    /// <summary>Duplicates the service's own SYSTEM token and moves it into the
    /// target session. This is what lets the service put a SYSTEM process inside
    /// the interactive session so it can reach the secure desktop.</summary>
    private static SafeAccessTokenHandle? DuplicateSystemTokenForSession(uint sessionId)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (!current.IsSystem) return null;

        try
        {
            if (!Native.DuplicateTokenEx(
                    current.Token,
                    Native.TOKEN_ASSIGN_PRIMARY | Native.TOKEN_DUPLICATE | Native.TOKEN_QUERY | Native.TOKEN_ADJUST_SESSIONID,
                    IntPtr.Zero,
                    Native.SecurityImpersonationLevel.SecurityAnonymous,
                    Native.TokenType.TokenPrimary,
                    out var primary))
            {
                return null;
            }

            return Native.SetTokenInformation(
                primary.DangerousGetHandle(), Native.TokenSessionId, ref sessionId, sizeof(uint))
                ? primary
                : null;
        }
        catch (Exception ex)
        {
            Log.Error($"could not duplicate a SYSTEM token: {ex.Message}");
            return null;
        }
    }

    private static SafeAccessTokenHandle? QueryUserToken(uint sessionId) =>
        Native.WTSQueryUserToken(sessionId, out var token) ? token : null;
}