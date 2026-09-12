// PC Remote - Windows Agent
// .NET 8 console app. Serves a WebSocket-over-TLS endpoint on 0.0.0.0:58642
// using its own self-signed certificate (CertificateManager.cs) — no
// HttpListener, so no `netsh urlacl`/admin rights are needed. Handles
// pairing (PairingStore.cs), then simulates mouse/keyboard/media/power via
// Win32 SendInput, and advertises itself via mDNS (MdnsAdvertiser.cs).
//
// Required NuGet packages:
//   Makaretu.Dns.Multicast (mDNS advertisement)
//   System.Security.Cryptography.ProtectedData (DPAPI for cert + tokens)
//
// Build & run:
//   dotnet run
//
// Project file (PcRemoteAgent.csproj) should target net8.0 (or net6.0+).

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("PcRemoteAgent.Tests")]

namespace PcRemoteAgent;

public static class Program
{
    private const int Port = 58642;
    private static readonly PairingStore Pairing = new();

    /// <summary>clientIp -> connected-at, used for the connected-device indicator (§6).</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Connected = new();

    public static async Task Main()
    {
        Console.WriteLine("=== PC Remote Agent ===");
        Console.WriteLine($"Listening on port {Port} (WSS)");

        // Fresh pairing code, then auto-rotate every 5 minutes (09-SECURITY-PRIVACY.md §3).
        var pairingCode = Pairing.GeneratePairingCode();
        Console.WriteLine($"Pairing code (valid 5 minutes, auto-refreshes): {pairingCode}");
        var rotation = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(PairingStore.CodeLifetime);
                if (!Pairing.IsCodeExpired()) continue; // already refreshed elsewhere
                lock (Pairing)
                {
                    if (!Pairing.IsCodeExpired()) continue;
                    var fresh = Pairing.GeneratePairingCode();
                    Console.WriteLine($"[code] Pairing code refreshed: {fresh}");
                }
            }
        });

        Console.WriteLine("Local IP addresses to enter manually if discovery fails:");
        foreach (var ip in GetLocalIPv4Addresses())
            Console.WriteLine($"  {ip}:{Port} (wss)");

        // Advertise via mDNS so the app can auto-discover this PC (falls back
        // gracefully to manual IP entry if mDNS is unavailable).
        MdnsAdvertiser.Start(Port);

        X509Certificate2 cert;
        try
        {
            cert = CertificateManager.LoadOrCreate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] TLS certificate unavailable ({ex.Message}); refusing to serve plaintext");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            MdnsAdvertiser.Stop();
            Environment.Exit(0);
        };

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleConnectionAsync(client, cert));
        }
    }

    internal static async Task HandleConnectionAsync(TcpClient client, X509Certificate2 cert)
    {
        var clientIp = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "unknown";
        try
        {
            var socket = await WebSocketConnection.AcceptAsync(
                client.GetStream(), cert, isSecure: true);
            if (socket is null)
            {
                Console.WriteLine($"[!] {clientIp} rejected (handshake failed)");
                client.Dispose();
                return;
            }

            Console.WriteLine($"[+] Connection from {clientIp} (TLS)");
            Connected[clientIp] = DateTime.UtcNow;
            PrintConnectedCount();

            var authenticated = false;
            try
            {
                while (true)
                {
                    var text = await socket.ReceiveTextAsync();
                    if (text is null) break;

                    var msg = JsonSerializer.Deserialize<RemoteMessage>(text);
                    if (msg is null) continue;

                    if (!authenticated)
                    {
                        if (msg.Type == "auth" && Pairing.TryAuthenticate(msg.Token, msg.PairingCode))
                        {
                            authenticated = true;
                            var token = Pairing.IssueTokenIfNeeded(msg.Token);
                            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage { Type = "auth_ok", Token = token }));
                            Console.WriteLine($"[+] {clientIp} authenticated");
                        }
                        else
                        {
                            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage { Type = "auth_failed" }));
                        }
                        continue;
                    }

                    // A shutdown/restart terminates this connection deliberately;
                    // tell the app before executing so it doesn't try to reconnect
                    // (10-ERROR-HANDLING.md §3).
                    if (msg.Type == "system_power" && msg.Action is "shutdown" or "restart")
                    {
                        await socket.SendTextAsync(JsonSerializer.Serialize(
                            new RemoteMessage { Type = "disconnecting", Reason = msg.Action }));
                    }

                    HandleCommand(msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Connection error from {clientIp}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Handshake error from {clientIp}: {ex.Message}");
        }
        finally
        {
            Console.WriteLine($"[-] {clientIp} disconnected");
            Connected.TryRemove(clientIp, out _);
            PrintConnectedCount();
            client.Dispose();
        }
    }

    private static void PrintConnectedCount()
    {
        Console.WriteLine($"     {Connected.Count} device(s) connected");
    }

    private static void HandleCommand(RemoteMessage msg)
    {
        switch (msg.Type)
        {
            case "mouse_move":
                Win32Input.MoveMouseRelative(msg.Dx ?? 0, msg.Dy ?? 0);
                break;
            case "mouse_click":
                Win32Input.MouseClick(msg.Button ?? "left", msg.Action ?? "click");
                break;
            case "mouse_scroll":
                Win32Input.Scroll(msg.Dy ?? 0);
                break;
            case "key_press":
                Win32Input.SendKey(msg.Key ?? "", msg.Modifiers ?? new List<string>());
                break;
            case "text_input":
                Win32Input.TypeText(msg.Text ?? "");
                break;
            case "media_control":
                Win32Input.MediaControl(msg.Action ?? "");
                break;
            case "system_power":
                SystemPower.Execute(msg.Action ?? "");
                break;
            default:
                Console.WriteLine($"[?] Unknown message type: {msg.Type}");
                break;
        }
    }

    public static IEnumerable<string> GetLocalIPv4Addresses()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    yield return addr.Address.ToString();
                }
            }
        }
    }
}

/// <summary>
/// JSON message shape shared with the Android app. Fields are nullable/optional
/// since each message type only uses a subset of them.
/// </summary>
public class RemoteMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("dx")] public int? Dx { get; set; }
    [JsonPropertyName("dy")] public int? Dy { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("modifiers")] public List<string>? Modifiers { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("pairingCode")] public string? PairingCode { get; set; }
}

/// <summary>
/// Win32 input simulation via SendInput. This is the core mechanism that lets
/// the agent move the cursor, click, scroll, and send keystrokes.
/// </summary>
public static class Win32Input
{
    // (unchanged from previous version — see git history)
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    public static void MoveMouseRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void MouseClick(string button, string action)
    {
        var (down, up) = button.ToLower() switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        void Fire(uint flag) =>
            SendInput(1, new[] { new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } } },
                Marshal.SizeOf<INPUT>());

        switch (action)
        {
            case "down": Fire(down); break;
            case "up": Fire(up); break;
            default: Fire(down); Fire(up); break; // "click"
        }
    }

    public static void Scroll(int amount)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)(amount * 120)), dwFlags = MOUSEEVENTF_WHEEL } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // Simple virtual-key map for common special keys used by the remote's
    // dedicated key buttons (arrows, enter, backspace, etc.).
    private static readonly Dictionary<string, ushort> VkMap = new()
    {
        ["ENTER"] = 0x0D,
        ["BACKSPACE"] = 0x08,
        ["TAB"] = 0x09,
        ["ESC"] = 0x1B,
        ["SPACE"] = 0x20,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
        ["DELETE"] = 0x2E,
        ["CTRL"] = 0x11,
        ["ALT"] = 0x12,
        ["SHIFT"] = 0x10,
        ["WIN"] = 0x5B,
    };

    public static void SendKey(string key, List<string> modifiers)
    {
        var modifierVks = modifiers.Select(m => VkMap.GetValueOrDefault(m.ToUpper(), (ushort)0)).Where(v => v != 0).ToList();
        if (!VkMap.TryGetValue(key.ToUpper(), out var vk))
        {
            // Fall back to treating a single character key as its ASCII/VK code.
            if (key.Length == 1) vk = (ushort)char.ToUpper(key[0]);
            else return;
        }

        foreach (var m in modifierVks) keybd_event((byte)m, 0, 0, IntPtr.Zero);
        keybd_event((byte)vk, 0, 0, IntPtr.Zero);
        keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        foreach (var m in modifierVks) keybd_event((byte)m, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    // Types arbitrary Unicode text (used for the soft-keyboard "type text" flow).
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static void MediaControl(string action)
    {
        // Virtual key codes for media keys (handled natively by Windows).
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
        if (vk == 0) return;

        keybd_event(vk, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }
}

public static class SystemPower
{
    [DllImport("PowrProf.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void LockWorkStation();

    public static void Execute(string action)
    {
        switch (action)
        {
            case "sleep":
                SetSuspendState(false, false, false);
                break;
            case "shutdown":
                System.Diagnostics.Process.Start("shutdown", "/s /t 0");
                break;
            case "restart":
                System.Diagnostics.Process.Start("shutdown", "/r /t 0");
                break;
            case "lock":
                LockWorkStation();
                break;
        }
    }
}