// Privileged power operations, run by the LocalSystem service so they work from
// any desktop state.

using System.Diagnostics;
using System.Runtime.InteropServices;
using PcRemote.Core;

namespace PcRemote.Service;

public static class PowerController
{
    [DllImport("PowrProf.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public static void Execute(string action)
    {
        switch (action)
        {
            case "sleep":
                SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: true);
                break;
            case "lock":
                LockConsoleSession();
                break;
            case "shutdown":
            case "restart":
                // shutdown.exe honours the "unsaved changes" prompt and works
                // before anyone has logged on.
                Process.Start(new ProcessStartInfo("shutdown", action == "restart" ? "/r /t 0" : "/s /t 0")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                break;
        }
    }

    /// <summary>LockWorkStation only works from inside the interactive session,
    /// so run a one-shot helper there with the console user's token.</summary>
    private static void LockConsoleSession()
    {
        try
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, "PCRemoteSession.exe");
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0 || !File.Exists(exePath))
            {
                AgentLog.Warn("Cannot lock: no active console session");
                return;
            }

            if (!WTSQueryUserToken(sessionId, out var token))
            {
                AgentLog.Warn("Cannot lock: no user token in console session");
                return;
            }
            using (token)
            {
                var si = new SessionWorkerClient.NativeMethods.STARTUPINFOW
                {
                    cb = System.Runtime.InteropServices.Marshal.SizeOf<SessionWorkerClient.NativeMethods.STARTUPINFOW>()
                };
                SessionWorkerClient.NativeMethods.CreateProcessAsUser(
                    token.DangerousGetHandle(), null, $"\"{exePath}\" --lock",
                    IntPtr.Zero, IntPtr.Zero, false,
                    SessionWorkerClient.NativeMethods.CREATE_NO_WINDOW,
                    IntPtr.Zero, null, ref si, out _);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Lock request failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
}
