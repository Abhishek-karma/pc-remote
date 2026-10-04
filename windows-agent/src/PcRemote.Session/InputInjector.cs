// Win32 input injection primitives (SendInput / keybd_event) used by every
// mode of the session helper (normal desktop and --secure-input). This code is
// stateless with respect to credentials — it never stores, logs or echoes the
// text it is asked to type (requirement 5: no password logging anywhere).

using System.Runtime.InteropServices;
using PcRemote.Core;

namespace PcRemote.Session;

public static class InputInjector
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
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static bool Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return true;
        uint res = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (res == 0)
        {
            int err = Marshal.GetLastWin32Error();
            AgentLog.Error($"SendInput returned 0, error={err}");
            return false;
        }
        return true;
    }

    public static bool MoveMouseRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } }
        };
        return Send([input]);
    }

    /// <summary>Absolute move in the CAPTURED coordinate space (virtual-desktop
    /// pixels, exactly what ScreenCapture reports), so a tap on the Android
    /// desktop view lands where the user touched.</summary>
    public static bool MoveMouseAbsolute(int x, int y)
    {
        var (nx, ny) = NormalizeToVirtualDesktop(
            x, y,
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = unchecked((int)nx),
                    dy = unchecked((int)ny),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                }
            }
        };
        return Send([input]);
    }

    /// <summary>Pure mapping of desktop pixels to SendInput's normalized 0..65535
    /// virtual-desktop space. Public + pure so the math is testable without
    /// injecting real input.</summary>
    public static (uint X, uint Y) NormalizeToVirtualDesktop(int x, int y, int originX, int originY, int width, int height)
    {
        if (width <= 1 || height <= 1) return (0, 0);
        long nx = (long)(x - originX) * 65535 / (width - 1);
        long ny = (long)(y - originY) * 65535 / (height - 1);
        return ((uint)Math.Clamp(nx, 0, 65535), (uint)Math.Clamp(ny, 0, 65535));
    }

    public static bool MouseClick(string button, string action)
    {
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        bool Fire(uint flag) =>
            Send([new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } }]);

        return action switch
        {
            "down" => Fire(down),
            "up" => Fire(up),
            _ => Fire(down) & Fire(up), // bitwise: always send both
        };
    }

    public static bool Scroll(int amount)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)(amount * 120)), dwFlags = MOUSEEVENTF_WHEEL } }
        };
        return Send([input]);
    }

    private static readonly Dictionary<string, ushort> VkMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = 0x11,
        ["CONTROL"] = 0x11,
        ["ALT"] = 0x12,
        ["SHIFT"] = 0x10,
        ["WIN"] = 0x5B,
        ["WINDOWS"] = 0x5B,
        ["LWIN"] = 0x5B,
        ["RWIN"] = 0x5C,
        ["ENTER"] = 0x0D,
        ["RETURN"] = 0x0D,
        ["BACKSPACE"] = 0x08,
        ["BKSP"] = 0x08,
        ["TAB"] = 0x09,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20,
        ["INSERT"] = 0x2D,
        ["INS"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["DEL"] = 0x2E,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["PAGEUP"] = 0x21,
        ["PGUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["PGDN"] = 0x22,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
        ["PRINTSCREEN"] = 0x2C,
        ["PRTSC"] = 0x2C,
        ["SCROLLLOCK"] = 0x91,
        ["PAUSE"] = 0x13,
        ["CAPSLOCK"] = 0x14,
        ["NUMLOCK"] = 0x90,
        ["F1"] = 0x70,
        ["F2"] = 0x71,
        ["F3"] = 0x72,
        ["F4"] = 0x73,
        ["F5"] = 0x74,
        ["F6"] = 0x75,
        ["F7"] = 0x76,
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
        ["F13"] = 0x7C,
        ["F14"] = 0x7D,
        ["F15"] = 0x7E,
        ["F16"] = 0x7F,
        ["F17"] = 0x80,
        ["F18"] = 0x81,
        ["F19"] = 0x82,
        ["F20"] = 0x83,
        ["F21"] = 0x84,
        ["F22"] = 0x85,
        ["F23"] = 0x86,
        ["F24"] = 0x87,
    };

    public static bool SendKey(string key, List<string> modifiers)
    {
        if (string.IsNullOrWhiteSpace(key) || modifiers.Count > 8) return false;
        var modifierVks = new List<ushort>();
        foreach (var m in modifiers)
        {
            if (!VkMap.TryGetValue(m, out var mVk) || mVk == 0) return false;
            modifierVks.Add(mVk);
        }

        if (!VkMap.TryGetValue(key, out var vk))
        {
            if (key.Length == 1 && char.IsLetterOrDigit(key[0])) vk = (ushort)char.ToUpper(key[0]);
            else return false;
        }

        foreach (var m in modifierVks) keybd_event((byte)m, 0, 0, IntPtr.Zero);
        keybd_event((byte)vk, 0, 0, IntPtr.Zero);
        keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        foreach (var m in modifierVks) keybd_event((byte)m, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        return true;
    }

    public static bool TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        if (inputs.Count > 0)
            return Send([.. inputs]);
        return true;
    }

    public static bool MediaControl(string action)
    {
        const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        const byte VK_MEDIA_PREV_TRACK = 0xB1;
        const byte VK_VOLUME_UP = 0xAF;
        const byte VK_VOLUME_DOWN = 0xAE;
        const byte VK_VOLUME_MUTE = 0xAD;

        byte vk = action switch
        {
            "play_pause" => VK_MEDIA_PLAY_PAUSE,
            "next" => VK_MEDIA_NEXT_TRACK,
            "prev" => VK_MEDIA_PREV_TRACK,
            "vol_up" => VK_VOLUME_UP,
            "vol_down" => VK_VOLUME_DOWN,
            "mute" => VK_VOLUME_MUTE,
            _ => 0
        };
        if (vk == 0) return false;

        keybd_event(vk, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        return true;
    }

    public static void ReleaseAllButtons()
    {
        try
        {
            SendInput(1, [new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } }], Marshal.SizeOf<INPUT>());
            SendInput(1, [new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP } } }], Marshal.SizeOf<INPUT>());
            SendInput(1, [new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MIDDLEUP } } }], Marshal.SizeOf<INPUT>());

            ReadOnlySpan<byte> modifiers = [0x11, 0x12, 0x10, 0x5B, 0x5C];
            foreach (var vk in modifiers)
            {
                keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Error releasing buttons/modifiers: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
}
