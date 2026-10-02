// SessionManager (requirements 2, 4): owns session detection and launches the
// per-session helpers with the correct token and desktop context.
//
//   * Tracks WTS session state (logon/logoff/lock/unlock/switch) through
//     WM_WTSSESSION_CHANGE on a hidden message window.
//   * Detects which desktop currently receives input (Default / Winlogon /
//     Screen-saver) so the InputRouter can pick the right path.
//   * Launches PCRemoteSession (user token, console session) for normal
//     desktop input, and PCRemoteSession --secure-input (duplicated SYSTEM
//     token, console session) for the Winlogon desktop. The service itself
//     lives in session 0 and can never inject there directly.
//
// Password-handling rules (requirement 5): this class never sees, buffers or
// logs keystrokes — it only spawns processes and forwards opaque IPC commands.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PcRemote.Core;

namespace PcRemote.Service;

public enum DesktopState
{
    Unknown,
    Normal,          // interactive desktop, input goes to the session helper
    SecureDesktop,   // UAC consent/credential UI (WinSta0\Winlogon, user still logged in)
    Locked,          // Win+L or switch-user lock screen
    Logon,           // no user logged in; Windows login screen
}

public sealed class SessionManager : IDisposable
{
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint WM_APP_REFRESH = 0x8001;
    private const int WTS_CONSOLE_CONNECT = 1;
    private const int WTS_CONSOLE_DISCONNECT = 2;
    private const int WTS_REMOTE_CONNECT = 3;
    private const int WTS_REMOTE_DISCONNECT = 4;
    private const int WTS_SESSION_LOGON = 5;
    private const int WTS_SESSION_LOGOFF = 6;
    private const int WTS_SESSION_LOCK = 7;
    private const int WTS_SESSION_UNLOCK = 8;

    private Thread? _messageThread;
    private IntPtr _hwnd;
    private volatile bool _stop;
    private uint _consoleSessionId;

    private SessionWorkerClient? _sessionWorker;
    private SessionWorkerClient? _secureHelper;

    /// <summary>Guards CurrentState and both helper handles (see RefreshState).</summary>
    private readonly object _stateLock = new();

    private DesktopState _currentState = DesktopState.Unknown;

    /// <summary>Guarded by <see cref="_stateLock"/>; read lock-free from the
    /// network/IPC threads for status reporting.</summary>
    public DesktopState CurrentState => _currentState;

    public void Start()
    {
        _consoleSessionId = WTSGetActiveConsoleSessionId();

        // SCM recovery can restart the service while a user is already logged
        // on — the WTS_SESSION_LOGON event happened before we existed, so
        // probe the console session for a live user token instead of waiting
        // for an event that will never come.
        if (_consoleSessionId != 0 && WTSQueryUserToken(_consoleSessionId, out var existingToken))
        {
            _userLoggedOn = true;
            existingToken.Dispose();
        }

        _messageThread = new Thread(MessageLoop)
        {
            Name = "SessionManager-WTS",
            IsBackground = true,
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
        Console.WriteLine($"[+] SessionManager started (console session {_consoleSessionId})");
    }

    public void Stop()
    {
        _stop = true;
        _sessionWorker?.Kill();
        _secureHelper?.Kill();
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }

    public ISessionInputPath? GetSessionWorker() => _sessionWorker;

    public ISessionInputPath? GetSecureInputHelper() => _secureHelper;

    // ------------------------------------------------------------------
    // Hidden message window: receives WTS notifications (session lock/unlock,
    // logon/logoff) — the supported way for a service to observe session life
    // cycle. Also polls the input desktop name because UAC transitions do not
    // emit WTS events.
    // ------------------------------------------------------------------

    private void MessageLoop()
    {
        try
        {
            // Registering for session notifications requires a window; the
            // service creates a message-only window on a dedicated STA thread.
            _hwnd = CreateWindowEx(0, "STATIC", "PCRemoteSessionMgr", 0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");

            WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_ALL_SESSIONS);
            RefreshState();

            // UAC transitions emit no WTS event, so state is re-evaluated
            // periodically. The timer callback does NOT touch session state
            // directly: it only re-posts a message so every mutation of
            // CurrentState / the helper handles happens on this one STA thread
            // (see _stateLock).
            var timer = new System.Threading.Timer(
                _ => PostMessage(_hwnd, WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero),
                null, 500, 500);

            while (PumpMessage(out var msg))
            {
                if (msg.message == WM_WTSSESSION_CHANGE)
                {
                    OnSessionChange((int)msg.wParam, (uint)msg.lParam);
                }
                else if (msg.message == WM_APP_REFRESH)
                {
                    RefreshState();
                }
                else if (msg.message == 0x0010 /* WM_CLOSE */ && _stop)
                {
                    break;
                }
            }

            timer.Dispose();
            WTSUnRegisterSessionNotification(_hwnd);
            DestroyWindow(_hwnd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] SessionManager loop died: {ex.Message}");
        }
    }

    private static bool PumpMessage(out NativeMessage msg)
    {
        return GetMessage(out msg, IntPtr.Zero, 0, 0) > 0;
    }

    private void OnSessionChange(int reason, uint sessionId)
    {
        switch (reason)
        {
            case WTS_SESSION_LOGON:
            case WTS_SESSION_UNLOCK:
                _userLoggedOn = true;
                _locked = false;
                EnsureSessionWorker(sessionId);
                break;
            case WTS_SESSION_LOGOFF:
                _userLoggedOn = false;
                _locked = false;
                _sessionWorker?.Kill();
                _sessionWorker = null;
                _secureHelper?.Kill();
                _secureHelper = null;
                break;
            case WTS_SESSION_LOCK:
                _locked = true;
                _secureHelper ??= LaunchSecureHelper(sessionId);
                break;
            case WTS_CONSOLE_CONNECT:
                if (sessionId == _consoleSessionId)
                    EnsureSessionWorker(sessionId);
                break;
            case WTS_CONSOLE_DISCONNECT:
                _sessionWorker?.Kill();
                _sessionWorker = null;
                _secureHelper?.Kill();
                _secureHelper = null;
                break;
        }
        RefreshState();
    }

    /// <summary>Re-evaluates the desktop state and (re)spawns helpers as needed.
    /// Serialized by <see cref="_stateLock"/>: the IPC thread reports desktop
    /// state while the STA thread handles WTS events, and both paths mutate
    /// CurrentState and the helper handles. Launching a helper twice would
    /// leave an orphan process holding a pipe instance.</summary>
    private void RefreshState()
    {
        lock (_stateLock)
        {
            var newState = DetectState();
            if (newState != CurrentState)
            {
                Console.WriteLine($"[~] Desktop state: {CurrentState} -> {newState}");
                _currentState = newState;
                // Opportunistically (re)spawn helpers for the new reality.
                switch (newState)
                {
                    case DesktopState.Normal:
                        if (_consoleSessionId != 0) EnsureSessionWorker(_consoleSessionId);
                        break;
                    case DesktopState.Locked:
                    case DesktopState.Logon:
                    case DesktopState.SecureDesktop:
                        _secureHelper ??= LaunchSecureHelper(_consoleSessionId);
                        break;
                }
            }
            else if (newState == DesktopState.Normal)
            {
                EnsureSessionWorker(_consoleSessionId); // healing pass (both helpers)
            }
        }
    }

    // WTS-driven flags: the service sits in session 0 where OpenInputDesktop
    // cannot observe the interactive desktop, so lock/logon state comes from
    // session notifications and UAC state is *reported* by the secure-input
    // helper running inside the console session (ReportDesktopState).
    // Written on the STA message thread, read from the network/IPC threads.
    private volatile bool _userLoggedOn;
    private volatile bool _locked;

    private DesktopState DetectState()
    {
        if (!_userLoggedOn) return DesktopState.Logon;
        if (_locked) return DesktopState.Locked;
        return _secureDesktopActive ? DesktopState.SecureDesktop : DesktopState.Normal;
    }

    private volatile bool _secureDesktopActive;

    /// <summary>Called from the IPC coordinator when the secure-input helper
    /// reports the name of the active input desktop in the console session.</summary>
    public void ReportDesktopState(string desktopName)
    {
        _secureDesktopActive = string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase);
        RefreshState();
    }

    // ------------------------------------------------------------------
    // Helper process management
    // ------------------------------------------------------------------

    private void EnsureSessionWorker(uint sessionId)
    {
        if (sessionId == 0) sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0) return;

        if (_sessionWorker is { IsAlive: true }) return;

        var client = SessionWorkerClient.LaunchAsUser(sessionId, secure: false);
        if (client is not null)
        {
            _sessionWorker = client;
            Console.WriteLine($"[+] Session helper running in session {sessionId}");
        }

        _secureHelper ??= LaunchSecureHelper(sessionId);
    }

    private SessionWorkerClient? LaunchSecureHelper(uint sessionId)
    {
        if (sessionId == 0) sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0) return null;
        var client = SessionWorkerClient.LaunchAsUser(sessionId, secure: true);
        if (client is not null)
            Console.WriteLine($"[+] Secure input helper running in session {sessionId}");
        return client;
    }

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point pt;
    }

    private const uint NOTIFY_FOR_ALL_SESSIONS = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId,
        out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    public void Dispose() => Stop();
}
