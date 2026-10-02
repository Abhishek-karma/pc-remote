// Privileged system power operations, executed by the service itself
// (LocalSystem) rather than a user process, so lock/shutdown work from any
// desktop state. Elevated semantics: shutdown/restart go through the OS
// shutdown APIs with proper privilege, lock uses WTSLockSystem-equivalent.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcRemote.Service;

public static class PowerController
{
    [DllImport("PowrProf.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSShutdownSystem(IntPtr serverHandle, uint shutdownFlag);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    private const uint WTS_SHUTDOWN = 1;
    private const uint WTS_REBOOT = 2;
    private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    public static void Execute(string action)
    {
        switch (action)
        {
            case "sleep":
                SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
                break;
            case "lock":
                // Locking must affect the interactive session, so the service
                // asks that session to lock via LockWorkStation in a helper —
                // the session helper performs it; fall back to WTS disconnect.
                ExecuteInConsoleSession("lock");
                break;
            case "shutdown":
            case "restart":
                // Use the standard shutdown.exe with SYSTEM privileges: honors
                // pending edits warnings semantics and works pre-logon.
                var flag = action == "restart" ? "/r /t 0" : "/s /t 0";
                Process.Start(new ProcessStartInfo("shutdown", flag)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                break;
        }
    }

    private static void ExecuteInConsoleSession(string action)
    {
        try
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "PCRemoteSession.exe");
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0 || !File.Exists(exePath))
            {
                Console.WriteLine("[!] Cannot lock: no active console session");
                return;
            }

            // A quick one-shot helper invocation with the user token.
            if (!WTSQueryUserToken(sessionId, out var token))
            {
                Console.WriteLine("[!] Cannot lock: no user token in console session");
                return;
            }
            using (token)
            {
                var si = new SessionWorkerClient.NativeMethods.STARTUPINFOW { cb = System.Runtime.InteropServices.Marshal.SizeOf<SessionWorkerClient.NativeMethods.STARTUPINFOW>() };
                SessionWorkerClient.NativeMethods.CreateProcessAsUser(
                    token.DangerousGetHandle(), null, $"\"{exePath}\" --lock",
                    IntPtr.Zero, IntPtr.Zero, false,
                    SessionWorkerClient.NativeMethods.CREATE_NO_WINDOW,
                    IntPtr.Zero, null, ref si, out _);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Lock request failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
}
