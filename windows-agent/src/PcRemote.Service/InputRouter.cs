// Routes each command to the helper that can actually inject it.
//
//   Desktop state      | Helper
//   -------------------|--------------------------------------
//   Normal desktop     | session helper (logged-on user token)
//   UAC / lock / logon | secure helper (SYSTEM, Winlogon)
//
// The service itself never injects: it lives in session 0, where SendInput would
// target a desktop no user can see.

using PcRemote.Core;

namespace PcRemote.Service;

public sealed class InputRouter
{
    private readonly SessionManager _sessions;

    public InputRouter(SessionManager sessions)
    {
        _sessions = sessions;
    }

    /// <summary>Raised when the desktop is in a state we cannot reach. The control
    /// channel turns this into a disconnect rather than silently dropping input.</summary>
    public sealed class UnavailableException : Exception
    {
        public UnavailableException(string message) : base(message) { }
    }

    public Task Move(int dx, int dy) => Target.Move(dx, dy);

    public Task Button(string button, string action) => Target.Button(button, action);

    public Task Scroll(int notches) => Target.Scroll(notches);

    public Task Key(string key, string action) => Target.Key(key, action);

    public Task Text(string text) => Target.Text(text);

    /// <summary>Releases everything the desktop could still be holding, on BOTH
    /// helpers. On disconnect we cannot know which one was mid-drag, and a stuck
    /// button is worse than an extra pipe round trip.</summary>
    public async Task ReleaseAll()
    {
        foreach (var helper in new[] { _sessions.SessionHelper, _sessions.SecureHelper })
        {
            if (helper is not { IsAlive: true }) continue;
            try { await helper.ReleaseAll(); }
            catch (Exception ex) { Log.Warn($"release-all failed: {ex.Message}"); }
        }
    }

    /// <summary>The helper for the desktop currently receiving input.</summary>
    private InputHelper Target => _sessions.State switch
    {
        DesktopState.Normal => _sessions.SessionHelper is { IsAlive: true } session
            ? session
            : throw new UnavailableException("no session helper"),

        DesktopState.Locked or DesktopState.Secure or DesktopState.Logon =>
            _sessions.SecureHelper is { IsAlive: true } secure
                ? secure
                : throw new UnavailableException("no secure helper"),

        _ => throw new UnavailableException($"desktop state {_sessions.State}"),
    };
}