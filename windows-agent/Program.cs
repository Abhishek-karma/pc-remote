// PC Remote - Windows Agent
// Tray application (WinForms NotifyIcon; no console window in normal use).
// Serves a WebSocket-over-TLS endpoint on 0.0.0.0:58642 using its own
// self-signed certificate (CertificateManager.cs) — no HttpListener, so no
// `netsh urlacl`/admin rights are needed. Handles pairing (PairingStore.cs),
// then drives mouse/keyboard/media/power via Win32 SendInput, and advertises
// itself via mDNS (MdnsAdvertiser.cs). Run with --console for terminal
// output (development).
//
// Required NuGet packages:
//   Makaretu.Dns.Multicast (mDNS advertisement)
//   System.Security.Cryptography.ProtectedData (DPAPI for cert + tokens)

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("PcRemoteAgent.Tests")]

namespace PcRemoteAgent;

public static class Program
{
    public const int Port = 58642;
    private static readonly PairingStore Pairing = new();

    /// <summary>clientIp -> connected-at, used for the connected-device indicator (§6).</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Connected = new();

    /// <summary>Live pairing code for UI hosts (tray menu).</summary>
    public static string CurrentPairingCode => Pairing.CurrentCode;

    /// <summary>Raised on background threads whenever the pairing code rotates.</summary>
    public static event Action<string>? PairingCodeChanged;

    /// <summary>Raised on background threads when the connected-device count changes.</summary>
    public static event Action<int>? ConnectedCountChanged;

    private static TcpListener? _listener;
    private static Mutex? _singleInstance;

    /// <summary>Version for display (InformationalVersion without the commit suffix).</summary>
    public static string VersionDisplay =>
        (Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "0.0.0").Split('+')[0];

    [STAThread]
    public static async Task Main(string[] args)
    {
        // Console output also lands in a dated log file, so an agent with no
        // visible UI can still be diagnosed (14 §2).
        AgentLog.Init();

        if (args.Contains("--console"))
        {
            AttachParentConsole();
            await RunConsoleAsync();
            return;
        }

        // Single instance: a startup entry plus a manual launch (or a double
        // start during development) must not create a second tray agent —
        // the second one would die on the port or fork the pairing state.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\PC-Remote-Agent", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "PC Remote is already running. Check the system tray for its icon.",
                "PC Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _singleInstance.Dispose();
            return;
        }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(new TrayApplicationContext());
        }
        finally
        {
            _singleInstance.ReleaseMutex();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>Reattach to the launching terminal so --console output is visible.</summary>
    private static void AttachParentConsole() => AttachConsole(-1);

    private static async Task RunConsoleAsync()
    {
        Console.WriteLine("=== PC Remote Agent (console mode) ===");
        Console.WriteLine($"Listening on port {Port} (WSS)");
        // InformationalVersion carries CI suffixes (e.g. 1.0.0-ci.42).
        var infoVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine($"Version {infoVersion ?? typeof(Program).Assembly.GetName().Version?.ToString()}");
        Console.WriteLine("Local IP addresses to enter manually if discovery fails:");
        foreach (var ip in GetLocalIPv4Addresses())
            Console.WriteLine($"  {ip}:{Port} (wss)");

        PairingCodeChanged += code =>
            Console.WriteLine($"Pairing code (valid 5 minutes, auto-refreshes): {code}");
        ConnectedCountChanged += n => Console.WriteLine($"     {n} device(s) connected");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            RequestShutdown(shutdown);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestShutdown(shutdown);

        try
        {
            await StartServerAsync(shutdown.Token);
        }
        catch (OperationCanceledException) { /* shutdown requested */ }

        Console.WriteLine("[*] Shutting down");
        StopServer();
    }

    private static void RequestShutdown(CancellationTokenSource shutdown)
    {
        if (shutdown.IsCancellationRequested) return;
        Console.WriteLine("[*] Shutdown requested");
        try { _listener?.Stop(); } catch { /* already stopped */ }
        shutdown.Cancel();
    }

    /// <summary>
    /// Starts pairing rotation, mDNS, TLS certificate and the accept loop.
    /// Returns when <paramref name="ct"/> is cancelled; throws on fatal startup
    /// errors (e.g. port already in use). Hosts wire <see cref="PairingCodeChanged"/>
    /// and <see cref="ConnectedCountChanged"/> before calling this.
    /// </summary>
    public static async Task StartServerAsync(CancellationToken ct)
    {
        PairingCodeChanged?.Invoke(Pairing.GeneratePairingCode());

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PairingStore.CodeLifetime, ct);
                    lock (Pairing)
                    {
                        if (!Pairing.IsCodeExpired()) continue; // already refreshed elsewhere
                        PairingCodeChanged?.Invoke(Pairing.GeneratePairingCode());
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown requested */ }
        }, CancellationToken.None);

        // Advertise via mDNS so the app can auto-discover this PC (falls back
        // gracefully to manual IP entry if mDNS is unavailable).
        MdnsAdvertiser.Start(Port);

        var cert = CertificateManager.LoadOrCreate();

        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();

        while (!ct.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => HandleConnectionAsync(client, cert));
        }
    }

    /// <summary>Tears down mDNS and the listener. Safe to call twice.</summary>
    public static void StopServer()
    {
        MdnsAdvertiser.Stop();
        try { _listener?.Stop(); } catch { /* already stopped */ }
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
                            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                            {
                                Type = "auth_ok",
                                Token = token,
                                // The app stores this as the display name —
                                // covers manual-IP pairing too (mock §header).
                                PcName = AgentInfo.Name
                            }));
                            Console.WriteLine($"[+] {clientIp} authenticated");
                        }
                        else
                        {
                            // Never log the attempted token or pairing code (14 §3).
                            Console.WriteLine($"[!] {clientIp} authentication failed");
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
        ConnectedCountChanged?.Invoke(Connected.Count);
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
    [JsonPropertyName("pcName")] public string? PcName { get; set; }
}

/// <summary>Identity details the app fetches automatically.</summary>
public static class AgentInfo
{
    public static string Name => Environment.MachineName;
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