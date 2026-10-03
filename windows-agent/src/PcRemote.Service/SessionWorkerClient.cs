// A running session-side input path. PCRemoteSession.exe is launched either
// with the console user's token (normal desktop) or with a duplicated SYSTEM
// token whose session id is the console session (secure mode — it can then open
// WinSta0\Winlogon and inject into UAC / lock / logon). Commands travel as JSON
// over the helper's own named pipe.

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using System.Runtime.InteropServices;
using PcRemote.Core;

namespace PcRemote.Service;

public sealed class SessionWorkerClient : ISessionInputPath
{
    private readonly int _sessionId;
    private readonly bool _secure;

    public Process Proc { get; private set; }

    public bool IsAlive
    {
        get
        {
            // Never throws: the router and the 500 ms healing pass poll this
            // from several threads, and Process.HasExited raises on a disposed
            // Process. An exception here would propagate out of
            // SessionManager.RefreshState and kill the WTS STA thread for
            // good — no lock/unlock detection for the rest of the boot.
            if (_killed) return false;
            try { return !Proc.HasExited; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private volatile bool _killed;

    private SessionWorkerClient(Process proc, int sessionId, bool secure)
    {
        Proc = proc;
        _sessionId = sessionId;
        _secure = secure;
    }

    public static SessionWorkerClient? LaunchAsUser(uint sessionId, bool secure)
    {
        // One helper binary for both roles: the mode is a command-line switch
        // (--secure-input), not a separate executable.
        //
        // There used to be a second, UIAccess-manifest copy of this helper for
        // elevated windows. Windows refuses to launch an unsigned uiAccess
        // binary at all, so it could never run in practice and only added a
        // second build to sign and ship.
        var path = Path.Combine(AppContext.BaseDirectory, "PCRemoteSession.exe");
        if (!File.Exists(path))
        {
            Console.WriteLine("[!] Session helper missing: " + path);
            return null;
        }
        return TryLaunch(path, secure, sessionId);
    }

    /// <summary>Launches one helper executable. Returns null on failure (never
    /// throws) so callers can fall back to another binary.</summary>
    private static SessionWorkerClient? TryLaunch(string exePath, bool secure, uint sessionId)
    {
        try
        {
            if (!File.Exists(exePath))
            {
                Console.WriteLine("[!] Session helper missing: " + exePath);
                return null;
            }

            var args = secure ? "--secure-input" : "";

            // Secure mode: the helper must run as SYSTEM inside the target
            // session (a user token cannot open the Winlogon desktop).
            // Normal mode: the console user's token from WTS.
            using var token = secure
                ? DuplicateSystemTokenForSession(sessionId)
                : QueryUserToken(sessionId);
            if (token is null)
            {
                Console.WriteLine($"[!] Could not obtain {(secure ? "SYSTEM" : "user")} token for session {sessionId}");
                return null;
            }

            if (!NativeMethods.CreateEnvironmentBlock(out var envBlock, token.DangerousGetHandle(), false))
                envBlock = IntPtr.Zero;
            try
            {
                var si = new NativeMethods.STARTUPINFOW { cb = Marshal.SizeOf<NativeMethods.STARTUPINFOW>() };
                var psi = new NativeMethods.PROCESS_INFORMATION();
                var cmd = $"\"{exePath}\" {args}".TrimEnd();
                if (!NativeMethods.CreateProcessAsUser(
                        token.DangerousGetHandle(), null, cmd,
                        IntPtr.Zero, IntPtr.Zero, false,
                        NativeMethods.CREATE_NO_WINDOW | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                        envBlock, null, ref si, out psi))
                {
                    Console.WriteLine($"[!] CreateProcessAsUser({Path.GetFileName(exePath)}) failed: {Marshal.GetLastWin32Error()}");
                    return null;
                }
                CloseHandle(psi.hThread);
                var proc = Process.GetProcessById((int)psi.dwProcessId);
                CloseHandle(psi.hProcess);
                return new SessionWorkerClient(proc, (int)sessionId, secure);
            }
            finally
            {
                if (envBlock != IntPtr.Zero)
                    NativeMethods.DestroyEnvironmentBlock(envBlock);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Launching session helper failed: {ex.Message}");
            return null;
        }
    }

    private static SafeAccessTokenHandle? QueryUserToken(uint sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var dup))
            return null;
        return dup;
    }

    /// <summary>Duplicates the service's own (SYSTEM) primary token and moves it
    /// into the requested session. This is what lets a service-side manager put
    /// a SYSTEM process into the interactive session for secure-desktop input.</summary>
    private static SafeAccessTokenHandle? DuplicateSystemTokenForSession(uint sessionId)
    {
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            if (!current.IsSystem) return null;

            const int TokenSessionId = 12;
            const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
            const uint TOKEN_DUPLICATE = 0x0002;
            const uint TOKEN_QUERY = 0x0008;
            const uint TOKEN_ADJUST_SESSIONID = 0x0100;
            if (!NativeMethods.DuplicateTokenEx(
                    current.Token,
                    TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_SESSIONID,
                    IntPtr.Zero,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityAnonymous,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out var primary))
            {
                return null;
            }

            // DuplicateTokenEx returns a SafeAccessTokenHandle, so a failure
            // below releases it on dispose - no manual CloseHandle needed.
            if (!NativeMethods.SetTokenInformation(
                    primary.DangerousGetHandle(),
                    TokenSessionId,
                    ref sessionId,
                    (uint)sizeof(uint)))
            {
                return null;
            }
            return primary;
        }
        catch
        {
            return null;
        }
    }

    public void Kill()
    {
        _killed = true;
        try
        {
            if (!Proc.HasExited)
                Proc.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        finally { try { Proc.Dispose(); } catch { } }
    }

    // ------------------------------------------------------------------
    // ISessionInputPath: forward the (already allowlisted) command to the
    // helper; the helper re-validates the type against its own allowlist
    // before executing — defense in depth against a confused-deputy relay.
    // ------------------------------------------------------------------

    public Task<bool> MouseMoveRelative(int dx, int dy) =>
        SendInputCommandAsync("mouse_move", new RemoteMessage { Type = "mouse_move", Dx = dx, Dy = dy });

    public Task<bool> MouseClick(string button, string action) =>
        SendInputCommandAsync("mouse_click", new RemoteMessage { Type = "mouse_click", Button = button, Action = action });

    public Task<bool> Scroll(int amount) =>
        SendInputCommandAsync("mouse_scroll", new RemoteMessage { Type = "mouse_scroll", Dy = amount });

    public Task<bool> SendKey(string key, List<string> modifiers) =>
        SendInputCommandAsync("key_press", new RemoteMessage { Type = "key_press", Key = key, Modifiers = modifiers });

    public Task<bool> TypeText(string text) =>
        SendInputCommandAsync("text_input", new RemoteMessage { Type = "text_input", Text = text });

    public Task<bool> MediaControl(string action) =>
        SendInputCommandAsync("media_control", new RemoteMessage { Type = "media_control", Action = action });

    public Task<bool> ReleaseAll() =>
        SendInputCommandAsync("release_all", new RemoteMessage { Type = "release_all" });

    private async Task<bool> SendInputCommandAsync(string command, RemoteMessage payload)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Name carries the mode so the secure and normal helpers can never
            // answer each other's requests (see IpcEndpoints.SessionPipe).
            using var client = new IpcClient(IpcEndpoints.SessionPipe(_sessionId, _secure));
            try
            {
                client.Connect(1500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Relay connect to session {_sessionId} timed out after {sw.ElapsedMilliseconds} ms ({ex.GetType().Name}) - helper pipe not listening?");
                return false;
            }
            var reply = client.RoundTrip(new IpcMessage
            {
                Type = command,
                Role = _secure ? "secure" : "session",
                SessionId = _sessionId,
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload),
            }, 2000);
            // Only surface slow relays — every mouse_move would flood the log.
            if (sw.ElapsedMilliseconds > 50)
                Console.WriteLine($"[relay] {command} ok={(reply?.Ok == true)} in {sw.ElapsedMilliseconds} ms");
            return reply?.Ok == true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Input relay to session {_sessionId} ({command}) failed after {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void CloseHandle(IntPtr h) { NativeMethods.CloseHandle(h); }

    internal static class NativeMethods
    {
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        public enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous }
        public enum TOKEN_TYPE { TokenPrimary = 1 }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOW
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(
            IntPtr token, string? applicationName, string commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint creationFlags, IntPtr environment, string? currentDirectory,
            ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(
            IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
            SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out SafeAccessTokenHandle newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(
            IntPtr token, int infoClass, ref uint info, uint infoLength);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
