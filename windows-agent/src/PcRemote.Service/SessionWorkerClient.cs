// A running session-side input path. The service launches PCRemoteSession.exe
// either with the console user's token (normal desktop input) or with a
// duplicated SYSTEM token whose session id is set to the console session
// (secure-input mode: the helper can then open WinSta0\Winlogon and inject
// into the UAC / lock / logon desktops — the classic remote-desktop approach).
// Input commands travel as JSON over the helper's own named pipe.

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

    public bool IsAlive => !Proc.HasExited;

    private SessionWorkerClient(Process proc, int sessionId, bool secure)
    {
        Proc = proc;
        _sessionId = sessionId;
        _secure = secure;
    }

    public static SessionWorkerClient? LaunchAsUser(uint sessionId, bool secure)
    {
        // Prefer the UIAccess helper (signed, Program Files, uiAccess=true) when
        // the installer enabled it — it can reach elevated windows. Windows
        // refuses to launch an *unsigned* UIAccess binary at all, and every
        // unsigned/CI build would otherwise end up with NO input helper, so an
        // actual launch failure retries with the plain helper.
        if (!secure && UiAccessEnabled())
        {
            var uia = UiAccessHelperPath();
            if (uia is not null)
            {
                var launched = TryLaunch(uia, secure, sessionId);
                if (launched is not null) return launched;
                Console.WriteLine("[!] UIAccess helper failed to launch (binary unsigned?); falling back to the plain helper");
            }
        }

        var plain = Path.Combine(AppContext.BaseDirectory, "PCRemoteSession.exe");
        if (!File.Exists(plain))
        {
            Console.WriteLine("[!] Session helper missing");
            return null;
        }
        return TryLaunch(plain, secure, sessionId);
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
        try
        {
            if (!Proc.HasExited)
                Proc.Kill(entireProcessTree: true);
            Proc.Dispose();
        }
        catch { /* already gone */ }
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

    public Task MediaControl(string action) =>
        SendInputCommandAsync("media_control", new RemoteMessage { Type = "media_control", Action = action });

    private async Task<bool> SendInputCommandAsync(string command, RemoteMessage payload)
    {
        try
        {
            // Name carries the mode so the secure and normal helpers can never
            // answer each other's requests (see IpcEndpoints.SessionPipe).
            using var client = new IpcClient(IpcEndpoints.SessionPipe(_sessionId, _secure));
            client.Connect(1500);
            var reply = client.RoundTrip(new IpcMessage
            {
                Type = command,
                Role = _secure ? "secure" : "session",
                SessionId = _sessionId,
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload),
            }, 2000);
            return reply?.Ok == true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Input relay to session {_sessionId} ({command}) failed: {ex.Message}");
            return false;
        }
    }

    private static string? UiAccessHelperPath()
    {
        // Secure-input mode must be the SYSTEM-context plain helper; UIAccess
        // buys nothing on the Winlogon desktop and the manifest forbids it.
        var uia = Path.Combine(AppContext.BaseDirectory, "PCRemoteSession.UIA.exe");
        return File.Exists(uia) ? uia : null;
    }

    private static bool UiAccessEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PCRemote");
            return key?.GetValue("UseUIAccess") is 1;
        }
        catch
        {
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
