// Win32 input injection. This is the only place in the product that calls
// SendInput, and it runs inside the session helper — a process that lives in
// the interactive session's security context. That is not optional plumbing:
// a service in session 0 cannot reach the user's desktop at all.
//
// This class holds no credentials, opens no sockets and knows nothing about the
// network. It receives an already-authenticated, already-validated command and
// turns it into Win32 calls. Text it is asked to type is never logged.

using System.Runtime.InteropServices;

namespace PcRemote.Input;

public static class Injector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>One wheel notch, as Windows counts them.</summary>
    private const int WheelPerNotch = 120;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static bool Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length) return true;
        // UIPI (a more-privileged foreground window) is the usual cause and is not
        // fixable from here; log it so support has something to read.
        Log.Warn($"SendInput delivered {sent}/{inputs.Length} (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) =>
        new()
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
        };

    private static INPUT Key(ushort vk, bool up) =>
        new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };

    public static bool MoveMouse(int dx, int dy) => Send([Mouse(MOUSEEVENTF_MOVE, dx, dy)]);

    /// <summary>Presses or releases one mouse button. A full click sends both
    /// events in a single SendInput call so it can never be torn apart.</summary>
    public static bool MouseButton(string button, string action)
    {
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            "left" => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
            _ => (0u, 0u),
        };
        if (down == 0) return false;

        return action switch
        {
            "down" => Send([Mouse(down)]),
            "up" => Send([Mouse(up)]),
            _ => Send([Mouse(down), Mouse(up)]),
        };
    }

    /// <summary>Scrolls by whole notches. Positive scrolls up.</summary>
    public static bool Scroll(int notches)
    {
        if (notches == 0) return true;
        return Send([Mouse(MOUSEEVENTF_WHEEL, data: unchecked((uint)(notches * WheelPerNotch)))]);
    }

    /// <summary>Presses, releases or taps a named key. The app latches a modifier
    /// by sending "down" and releases it with "up".</summary>
    public static bool PressKey(string name, string action)
    {
        if (!Keys.TryGetValue(name, out var vk)) return false;

        return action switch
        {
            "down" => Send([Key(vk, up: false)]),
            "up" => Send([Key(vk, up: true)]),
            _ => Send([Key(vk, up: false), Key(vk, up: true)]),
        };
    }

    /// <summary>
    /// One keystroke to inject. A Character is typed as Unicode so it works
    /// whatever the current keyboard layout is; a Key is a named virtual key.
    /// </summary>
    internal readonly record struct Stroke(ushort? VirtualKey, char Character)
    {
        public static Stroke Char(char c) => new(null, c);
        public static Stroke Key(ushort vk) => new(vk, '\0');
    }

    /// <summary>
    /// Turns a text message into the keystrokes needed to produce it.
    ///
    /// Exposed and pure so it can be tested: calling SendInput for real would type
    /// into whatever window happened to be focused on the test machine.
    ///
    /// `\b` is a delete instruction, not a character. The phone sends it when the
    /// user removes a character, and typing a literal backspace character would
    /// put a stray control character into the user's document.
    /// </summary>
    internal static List<Stroke> StrokesFor(string text)
    {
        var strokes = new List<Stroke>(text.Length);
        foreach (var ch in text)
            strokes.Add(ch == '\b' ? Stroke.Key(0x08) : Stroke.Char(ch));
        return strokes;
    }

    /// <summary>Types literal Unicode text, including characters no physical
    /// keyboard has. One SendInput call covers the whole string.</summary>
    public static bool TypeText(string text)
    {
        var strokes = StrokesFor(text);
        if (strokes.Count == 0) return true;

        var inputs = new INPUT[strokes.Count * 2];
        for (var i = 0; i < strokes.Count; i++)
        {
            inputs[i * 2] = Down(strokes[i]);
            inputs[(i * 2) + 1] = Up(strokes[i]);
        }
        return Send(inputs);
    }

    private static INPUT Down(Stroke stroke) => stroke.VirtualKey is { } vk
        ? Key(vk, up: false)
        : Unicode(stroke.Character, up: false);

    private static INPUT Up(Stroke stroke) => stroke.VirtualKey is { } vk
        ? Key(vk, up: true)
        : Unicode(stroke.Character, up: true);

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
        },
    };
    /// <summary>Releases every button and modifier this helper could still be
    /// holding. Called when a phone disconnects mid-drag and on helper shutdown:
    /// the PC must never be left with a stuck mouse button or a latched Ctrl.</summary>
    public static void ReleaseAll()
    {
        try
        {
            Send([Mouse(MOUSEEVENTF_LEFTUP), Mouse(MOUSEEVENTF_RIGHTUP), Mouse(MOUSEEVENTF_MIDDLEUP)]);
            foreach (var vk in HeldReleasable) Send([Key(vk, up: true)]);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to release held input: {ex.Message}");
        }
    }

    /// <summary>Every modifier the app may latch, plus Win — exactly the set
    /// released on disconnect. Fixed order so releases are deterministic.</summary>
    private static readonly ushort[] HeldReleasable = [0x10, 0x11, 0x12, 0x5B, 0x5C];

    /// <summary>Every key the app may send. An allowlist, not a translation
    /// table: an unknown name is rejected here rather than guessed at.</summary>
    internal static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11,
        ["ALT"] = 0x12,
        ["SHIFT"] = 0x10,
        ["WIN"] = 0x5B,
        ["ESC"] = 0x1B,
        ["TAB"] = 0x09,
        ["ENTER"] = 0x0D,
        ["BACKSPACE"] = 0x08,
        ["DELETE"] = 0x2E,
        ["SPACE"] = 0x20,
        ["INSERT"] = 0x2D,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["PAGEUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["UP"] = 0x26,
        ["DOWN"] = 0x28,
        ["LEFT"] = 0x25,
        ["RIGHT"] = 0x27,
    };
}