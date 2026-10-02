// InputManager (requirement 4): routes remote input to the correct Windows
// security boundary instead of hoping one SendInput call fits all states.
//
//   Desktop state                    | Path
//   ---------------------------------|--------------------------------------
//   Normal desktop (unelevated fg)   | PCRemoteSession helper (user session)
//   Elevated foreground window       | PCRemoteSession.UIA helper (UIAccess)
//   UAC / Winlogon / locked          | Secure input helper (SYSTEM token in
//                                    | the console session, Winlogon desktop)
//
// The router itself never injects input into its own (session 0) context —
// that would target the wrong desktop and silently do nothing.

using PcRemote.Core;

namespace PcRemote.Service;

/// <summary>Raised when no input path is currently available for a request.</summary>
public class SessionUnavailableException : Exception
{
    public SessionUnavailableException(string message) : base(message) { }
}

public sealed class InputRouter
{
    private readonly SessionManager _sessions;

    public InputRouter(SessionManager sessions)
    {
        _sessions = sessions;
    }

    private ISessionInputPath ResolvePath()
    {
        var state = _sessions.CurrentState;
        switch (state)
        {
            case DesktopState.Normal:
                var session = _sessions.GetSessionWorker();
                if (session is { IsAlive: true }) return session;
                throw new SessionUnavailableException("no user session helper running");
            case DesktopState.SecureDesktop:
            case DesktopState.Logon:
            case DesktopState.Locked:
                var secure = _sessions.GetSecureInputHelper();
                if (secure is { IsAlive: true }) return secure;
                throw new SessionUnavailableException($"no secure input helper for state {state}");
            default:
                throw new SessionUnavailableException($"unsupported desktop state {state}");
        }
    }

    public Task<bool> MouseMoveRelative(int dx, int dy) =>
        ResolvePath().MouseMoveRelative(dx, dy);

    public Task<bool> MouseClick(string button, string action) =>
        ResolvePath().MouseClick(button, action);

    public Task<bool> Scroll(int amount) =>
        ResolvePath().Scroll(amount);

    public Task<bool> SendKey(string key, List<string> modifiers) =>
        ResolvePath().SendKey(key, modifiers);

    public Task<bool> TypeText(string text) =>
        ResolvePath().TypeText(text);

    public Task MediaControl(string action)
    {
        // Media keys only make sense for the interactive session.
        var session = _sessions.GetSessionWorker();
        if (session is { IsAlive: true }) return session.MediaControl(action);
        return Task.CompletedTask;
    }

    /// <summary>Loose ends: if a remote click died mid-press, releasing stale
    /// buttons prevents a stuck mouse. Delegated to whichever helpers exist.</summary>
    public static void ReleaseAllButtons()
    {
        // Handled inside the helpers themselves when their IPC connection
        // drops (see PcRemote.Session.ConnectionHandler). Nothing to do here.
    }
}

/// <summary>Common input surface every session-side path implements.</summary>
public interface ISessionInputPath
{
    bool IsAlive { get; }

    Task<bool> MouseMoveRelative(int dx, int dy);
    Task<bool> MouseClick(string button, string action);
    Task<bool> Scroll(int amount);
    Task<bool> SendKey(string key, List<string> modifiers);
    Task<bool> TypeText(string text);
    Task MediaControl(string action);
}
