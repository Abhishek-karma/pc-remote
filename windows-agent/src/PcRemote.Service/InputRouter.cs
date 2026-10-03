// Routes remote input to the correct Windows security boundary instead of
// hoping one SendInput call fits all states.
//
//   Desktop state           | Path
//   ------------------------|--------------------------------------
//   Normal desktop          | session helper (user token)
//   UAC / Winlogon / locked | secure helper (SYSTEM token in the console
//                           | session, Winlogon desktop)
//
// The router never injects into its own session-0 context — that would target
// the wrong desktop and silently do nothing.
//
// Elevated foreground windows are NOT reachable: UIPI blocks lower-integrity
// input and there is no UIAccess helper (an unsigned uiAccess binary cannot be
// launched at all).

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

    public Task<bool> MediaControl(string action)
    {
        // Media keys only make sense for the interactive session.
        var session = _sessions.GetSessionWorker();
        if (session is { IsAlive: true }) return session.MediaControl(action);
        return Task.FromResult(true);
    }

    /// <summary>A client can drop mid-drag (WiFi loss, app killed), leaving a
    /// mouse button physically held down. Releasing is best-effort: tell
    /// whichever helper is alive to drop everything it is holding.</summary>
    public async Task ReleaseAllButtons()
    {
        var session = _sessions.GetSessionWorker();
        if (session is { IsAlive: true })
        {
            try { await session.ReleaseAll(); }
            catch (Exception ex) { Console.WriteLine($"[!] Release-on-disconnect failed: {ex.Message}"); }
        }

        var secure = _sessions.GetSecureInputHelper();
        if (secure is { IsAlive: true })
        {
            try { await secure.ReleaseAll(); }
            catch (Exception ex) { Console.WriteLine($"[!] Release-on-disconnect failed (secure): {ex.Message}"); }
        }
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
    Task<bool> MediaControl(string action);

    /// <summary>Release every held mouse button/modifier. Used when a client
    /// disconnects mid-drag so the PC is never left with a stuck button.</summary>
    Task<bool> ReleaseAll();
}
