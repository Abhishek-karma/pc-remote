// Winlogon (secure) desktop access for the lock screen, UAC prompts and the
// logon UI. This lives with the injector because attaching to the secure desktop
// only matters in order to run SendInput against it.
//
// The access mask is the whole ball game: SetThreadDesktop REJECTS a desktop
// handle that was not opened with DESKTOP_SWITCHDESKTOP (ERROR_ACCESS_DENIED),
// and the injector turns that failure into a silent "refused" for every relayed
// command — read access alone is NOT enough, which is exactly why lock-screen
// input never arrived.

using System.Runtime.InteropServices;
using System.Text;

namespace PcRemote.Input;

internal static class SecureDesktop
{
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    /// <summary>Access mask every OpenInputDesktop call in the secure-input path
    /// MUST request: read access to inspect the desktop name plus switch access
    /// to actually attach the thread to it.</summary>
    public const uint InputDesktopAccess = DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP;

    public const string WinlogonDesktopName = "Winlogon";

    private const uint UOI_NAME = 2;

    /// <summary>One desktop handle for the process lifetime. The thread/desktop
    /// association outlives the handle, and re-opening one per relayed
    /// keystroke would leak a desktop handle on every mouse_move.</summary>
    private static IntPtr _winlogonDesktop;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, uint index,
        StringBuilder info, uint nMax, out uint length);

    public static bool IsSecureDesktop(string? name) =>
        string.Equals(name, WinlogonDesktopName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Name of the desktop currently receiving input, or null when it
    /// cannot be queried.</summary>
    public static string? GetActiveInputDesktopName()
    {
        var h = OpenInputDesktop(0, false, InputDesktopAccess);
        if (h == IntPtr.Zero) return null;
        try
        {
            return ReadName(h);
        }
        finally
        {
            CloseDesktop(h);
        }
    }

    /// <summary>Attaches the CALLING THREAD to the input desktop when — and only
    /// when — that desktop is Winlogon. Returns the (secure) desktop name on
    /// success, or null when the active desktop is the normal one: a SYSTEM
    /// helper must never inject into the user session behind the user's back.
    /// Must be called on the same thread that will then run SendInput.</summary>
    public static string? AttachCurrentThreadToSecureInputDesktop()
    {
        var h = OpenInputDesktop(0, false, InputDesktopAccess);
        if (h == IntPtr.Zero)
        {
            Warn($"OpenInputDesktop failed: {Marshal.GetLastWin32Error()}");
            return null;
        }

        var name = ReadName(h);
        if (!IsSecureDesktop(name))
        {
            CloseDesktop(h);
            return null; // normal desktop — nothing to attach to
        }

        if (_winlogonDesktop == IntPtr.Zero)
        {
            _winlogonDesktop = h; // keep open for the process lifetime
        }
        else
        {
            CloseDesktop(h); // same desktop already cached
        }

        if (!SetThreadDesktop(_winlogonDesktop))
        {
            Warn($"SetThreadDesktop(Winlogon) failed: {Marshal.GetLastWin32Error()}");
            return null;
        }
        return WinlogonDesktopName;
    }

    private static string? ReadName(IntPtr desktop)
    {
        var sb = new StringBuilder(256);
        return GetUserObjectInformation(desktop, UOI_NAME, sb, (uint)sb.Capacity, out _)
            ? sb.ToString()
            : null;
    }

    private static void Warn(string message) => Log.Warn($"SecureDesktop: {message}");
}