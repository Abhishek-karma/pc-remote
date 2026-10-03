// Owns session detection and launches the per-session helpers with the right
// token and desktop. WTS session events arrive on a hidden message window;
// UAC transitions emit no event, so state is also re-evaluated on a timer.
// This class never sees, buffers or logs keystrokes — it only spawns
// processes and forwards opaque IPC commands.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PcRemote.Core;

namespace PcRemote.Service;

public enum DesktopState
{
    Unknown,
    Normal,          // interactive desktop
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

    /// <summary>Guards CurrentState and both helper handles.</summary>
    private readonly object _stateLock = new();

    private DesktopState _currentState = DesktopState.Unknown;

    /// <summary>Read lock-free from the network/IPC threads for status reporting.</summary>
    public DesktopState CurrentState => _currentState;

    public void Start()
    {
        _consoleSessionId = ResolveInputSessionId(WTSGetActiveConsoleSessionId());

        // SCM recovery can restart us while a user is already logged on, so the
        // WTS_SESSION_LOGON event will never arrive — probe for a live token.
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

    private void MessageLoop()
    {
        try
        {
            _hwnd = CreateWindowEx(0, "STATIC", "PCRemoteSessionMgr", 0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");

            WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_ALL_SESSIONS);
            RefreshState();

            // The timer only re-posts a message: every mutation of CurrentState
            // and the helper handles stays on this STA thread.
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
                EnsureSecureHelper(sessionId);
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
    /// The IPC thread reports desktop state while this STA thread handles WTS
    /// events; both mutate CurrentState and the helper handles, and launching a
    /// helper twice would orphan a process holding a pipe instance.</summary>
    private void RefreshState()
    {
        lock (_stateLock)
        {
            // Re-read the console session: it changes on a console switch, and
            // a stale id would spawn helpers into the wrong session (which then
            // look alive but are on a desktop nobody is looking at).
            var reported = WTSGetActiveConsoleSessionId();
            if (IsLaunchableSession(reported)) _consoleSessionId = reported;

            var newState = DetectState();
            if (newState != CurrentState)
            {
                Console.WriteLine($"[~] Desktop state: {CurrentState} -> {newState}");
                _currentState = newState;
                // Opportunistically (re)spawn helpers for the new reality.
                switch (newState)
                {
                    case DesktopState.Normal:
                        EnsureSessionWorker(_consoleSessionId);
                        break;
                    case DesktopState.Locked:
                    case DesktopState.Logon:
                    case DesktopState.SecureDesktop:
                        EnsureSecureHelper(_consoleSessionId);
                        break;
                }
            }
            else
            {
                // A helper that crashed (or was never launched) must be back
                // within one tick.
                switch (newState)
                {
                    case DesktopState.Normal:
                        EnsureSessionWorker(_consoleSessionId); // brings both helpers
                        break;
                    case DesktopState.Locked:
                    case DesktopState.Logon:
                    case DesktopState.SecureDesktop:
                        EnsureSecureHelper(_consoleSessionId);
                        break;
                }
            }
        }
    }

    /// <summary>A session id we can actually launch a helper into.
    /// WTS returns 0 when there is no console session and 0xFFFFFFFF when the
    /// console session has no user attached — the signed-out / logon-screen
    /// case the secure helper exists for.</summary>
    private static bool IsLaunchableSession(uint sessionId) =>
        sessionId != 0 && sessionId != InvalidSessionId;

    private const uint InvalidSessionId = 0xFFFFFFFF;

    /// <summary>Session that receives physical input, with a fallback for the
    /// logon screen: the active console session, else the first active session
    /// WTS knows about, else the physical console session (1).</summary>
    private uint ResolveInputSessionId(uint sessionId)
    {
        if (IsLaunchableSession(sessionId)) return sessionId;
        if (IsLaunchableSession(_consoleSessionId)) return _consoleSessionId;
        var active = FindActiveSessionId();
        return active != 0 ? active : DefaultConsoleSession;
    }

    private const uint DefaultConsoleSession = 1;
    private const int WTSActive = 0;

    private static uint FindActiveSessionId()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buffer, out var count) || buffer == IntPtr.Zero)
                return 0;
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (uint i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(buffer, checked((int)(i * size))));
                if (info.State == WTSActive && IsLaunchableSession(info.SessionId))
                    return info.SessionId;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
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
        sessionId = ResolveInputSessionId(sessionId);
        if (sessionId == 0) return;

        if (_sessionWorker is { IsAlive: true }) return;
        _sessionWorker?.Kill(); // reap the dead one before replacing it
        _sessionWorker = null;

        var client = SessionWorkerClient.LaunchAsUser(sessionId, secure: false);
        if (client is not null)
        {
            _sessionWorker = client;
            Console.WriteLine($"[+] Session helper running in session {sessionId}");
        }

        EnsureSecureHelper(sessionId);
    }

    /// <summary>The secure (Winlogon) helper must be *alive*, not merely
    /// non-null, for lock / UAC / logon input to work. The previous `??=` only
    /// replaced a null reference, so once the helper had died — or had never
    /// been started because no user was logged on yet — every relayed command
    /// kept failing with "no secure input helper" until the service restarted.</summary>
    private void EnsureSecureHelper(uint sessionId)
    {
        if (_secureHelper is { IsAlive: true }) return;
        _secureHelper?.Kill();
        _secureHelper = null;
        _secureHelper = LaunchSecureHelper(ResolveInputSessionId(sessionId));
    }

    private SessionWorkerClient? LaunchSecureHelper(uint sessionId)
    {
        if (sessionId == 0) return null;
        var client = SessionWorkerClient.LaunchAsUser(sessionId, secure: true);
        if (client is not null)
            Console.WriteLine($"[+] Secure input helper running in session {sessionId}");
        else
            Console.WriteLine($"[!] Secure input helper could not start in session {sessionId} (no SYSTEM token? not LocalSystem?)");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pWinStationName;
        public int State;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, uint reserved, uint version,
        out IntPtr sessionInfo, out uint count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

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
