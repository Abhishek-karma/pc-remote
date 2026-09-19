// PC Remote Windows Agent - System tray host and WSS server driving Win32 input simulation.

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("PcRemoteAgent.Tests")]

namespace PcRemoteAgent;

public static class Program
{
    public const int Port = 58642;
    public const int MaxTotalConnections = 10;
    public const int MaxConnectionsPerIp = 3;

    private static readonly PairingStore Pairing = new();
    private static readonly ConcurrentDictionary<string, DateTime> Connected = new();

    public static string CurrentPairingCode => Pairing.CurrentCode;
    public static event Action<string>? PairingCodeChanged;
    public static event Action<int>? ConnectedCountChanged;

    private static TcpListener? _listener;
    private static Mutex? _singleInstance;

    public static string VersionDisplay =>
        (Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "0.0.0").Split('+')[0];

    [STAThread]
    public static async Task Main(string[] args)
    {
        AgentLog.Init();

        if (args.Contains("--console"))
        {
            AttachParentConsole();
            await RunConsoleAsync();
            return;
        }

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
                        if (!Pairing.IsCodeExpired()) continue;
                        PairingCodeChanged?.Invoke(Pairing.GeneratePairingCode());
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        FirewallHelper.EnsureFirewallRules();

        MdnsAdvertiser.Start(Port);

        var cert = CertificateManager.LoadOrCreate();

        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, Port);
            _listener.Server.DualMode = true;
            _listener.ExclusiveAddressUse = true;
            _listener.Start();
        }
        catch
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.ExclusiveAddressUse = true;
            _listener.Start();
        }

        while (!ct.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => HandleConnectionAsync(client, cert));
        }
    }

    public static void StopServer()
    {
        MdnsAdvertiser.Stop();
        try { _listener?.Stop(); } catch { }
    }

    private static bool TryAcquireConnectionSlot(string clientIp, out string connKey)
    {
        connKey = $"{clientIp}:{Guid.NewGuid():N}";
        lock (Connected)
        {
            var currentTotal = Connected.Count;
            var currentIpCount = Connected.Keys.Count(k => k.StartsWith(clientIp + ":", StringComparison.Ordinal));

            if (currentTotal >= MaxTotalConnections || currentIpCount >= MaxConnectionsPerIp)
            {
                return false;
            }

            Connected[connKey] = DateTime.UtcNow;
            return true;
        }
    }

    internal static async Task HandleConnectionAsync(TcpClient client, X509Certificate2 cert)
    {
        var clientIp = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "unknown";
        if (!TryAcquireConnectionSlot(clientIp, out var connKey))
        {
            Console.WriteLine($"[!] {clientIp} rejected: connection limit exceeded");
            return;
        }

        try
        {
            using var socket = await WebSocketConnection.AcceptAsync(client.GetStream(), cert, isSecure: true);
            if (socket is null)
            {
                Console.WriteLine($"[!] {clientIp} rejected (handshake failed)");
                return;
            }

            Console.WriteLine($"[+] Connection from {clientIp} (TLS)");
            PrintConnectedCount();

            var authenticated = false;
            var authDeadline = DateTime.UtcNow.AddSeconds(15);
            var messageCount = 0;
            var windowStart = DateTime.UtcNow;
            var muteUntil = DateTime.MinValue;

            while (true)
            {
                if (!authenticated && DateTime.UtcNow > authDeadline)
                {
                    Console.WriteLine($"[!] {clientIp} disconnected (authentication timeout)");
                    break;
                }

                var text = await socket.ReceiveTextAsync();
                if (text is null) break;

                var now = DateTime.UtcNow;
                if ((now - windowStart).TotalSeconds >= 1.0)
                {
                    messageCount = 0;
                    windowStart = now;
                }
                messageCount++;
                if (messageCount > 150)
                {
                    muteUntil = now.AddSeconds(1);
                    messageCount = 0;
                    windowStart = now.AddSeconds(1);
                }

                if (DateTime.UtcNow < muteUntil)
                {
                    continue;
                }

                RemoteMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<RemoteMessage>(text);
                }
                catch
                {
                    await SendErrorAsync(socket, null, "invalid_json");
                    continue;
                }

                if (msg is null) continue;

                if (msg.Version != 1)
                {
                    await SendErrorAsync(socket, msg.RequestId, "unsupported_version");
                    continue;
                }

                if (!authenticated)
                {
                    if (msg.Type == "auth")
                    {
                        if (Pairing.IsIpLockedOut(clientIp))
                        {
                            Console.WriteLine($"[!] {clientIp} authentication blocked (locked out)");
                            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                            {
                                Version = 1,
                                RequestId = msg.RequestId,
                                Type = "auth_failed",
                                ErrorCode = "rate_limited"
                            }));
                            break;
                        }

                        if (Pairing.TryAuthenticate(msg.Token, msg.PairingCode, clientIp))
                        {
                            authenticated = true;
                            var token = Pairing.IssueTokenIfNeeded(msg.Token);
                            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                            {
                                Version = 1,
                                RequestId = msg.RequestId,
                                Type = "auth_ok",
                                Token = token,
                                PcName = AgentInfo.Name
                            }));
                            Console.WriteLine($"[+] {clientIp} authenticated");
                        }
                        else
                        {
                            Console.WriteLine($"[!] {clientIp} authentication failed");
                            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                            {
                                Version = 1,
                                RequestId = msg.RequestId,
                                Type = "auth_failed",
                                ErrorCode = "invalid_credentials"
                            }));
                        }
                    }
                    else
                    {
                        await SendErrorAsync(socket, msg.RequestId, "unauthorized");
                    }
                    continue;
                }

                if (msg.Type == "system_power" && msg.Action is "shutdown" or "restart")
                {
                    await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                    {
                        Version = 1,
                        RequestId = msg.RequestId,
                        Type = "disconnecting",
                        Reason = msg.Action
                    }));
                }

                await HandleCommandAsync(socket, msg);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Connection error from {clientIp}: {ex.Message}");
        }
        finally
        {
            lock (Connected)
            {
                Connected.TryRemove(connKey, out _);
            }
            Win32Input.ReleaseAllButtons();
            Console.WriteLine($"[-] {clientIp} disconnected");
            PrintConnectedCount();
            client.Dispose();
        }
    }

    private static void PrintConnectedCount()
    {
        var devices = Connected.Count;
        Console.WriteLine($"     {devices} device(s) connected");
        ConnectedCountChanged?.Invoke(devices);
    }

    private static async Task SendAckAsync(WebSocketConnection socket, string? requestId)
    {
        if (string.IsNullOrEmpty(requestId)) return;
        await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
        {
            Version = 1,
            RequestId = requestId,
            Type = "command_result",
            Success = true
        }));
    }

    private static async Task SendErrorAsync(WebSocketConnection socket, string? requestId, string errorCode)
    {
        await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
        {
            Version = 1,
            RequestId = requestId,
            Type = "command_result",
            Success = false,
            ErrorCode = errorCode
        }));
    }

    private static async Task HandleCommandAsync(WebSocketConnection socket, RemoteMessage msg)
    {
        switch (msg.Type)
        {
            case "mouse_move":
                Win32Input.MoveMouseRelative(
                    Math.Clamp(msg.Dx ?? 0, -4096, 4096),
                    Math.Clamp(msg.Dy ?? 0, -4096, 4096));
                await SendAckAsync(socket, msg.RequestId);
                break;

            case "mouse_click":
                var btn = (msg.Button ?? "left").ToLowerInvariant();
                var act = (msg.Action ?? "click").ToLowerInvariant();
                if (btn is not ("left" or "right" or "middle") || act is not ("click" or "down" or "up"))
                {
                    await SendErrorAsync(socket, msg.RequestId, "bad_field");
                    return;
                }
                Win32Input.MouseClick(btn, act);
                await SendAckAsync(socket, msg.RequestId);
                break;

            case "mouse_scroll":
                Win32Input.Scroll(Math.Clamp(msg.Dy ?? 0, -1200, 1200));
                await SendAckAsync(socket, msg.RequestId);
                break;

            case "key_press":
                if (string.IsNullOrEmpty(msg.Key) || !Win32Input.SendKey(msg.Key, msg.Modifiers ?? []))
                {
                    await SendErrorAsync(socket, msg.RequestId, "bad_field");
                    return;
                }
                await SendAckAsync(socket, msg.RequestId);
                break;

            case "text_input":
                var text = msg.Text ?? "";
                if (text.Length > 1000) text = text[..1000];
                Win32Input.TypeText(text);
                await SendAckAsync(socket, msg.RequestId);
                break;

            case "media_control":
                var mediaAct = (msg.Action ?? "").ToLowerInvariant();
                if (mediaAct is not ("play_pause" or "next" or "prev" or "vol_up" or "vol_down" or "mute"))
                {
                    await SendErrorAsync(socket, msg.RequestId, "invalid_command");
                    return;
                }
                Win32Input.MediaControl(mediaAct);
                await SendAckAsync(socket, msg.RequestId);
                break;

            case "system_power":
                var powerAct = (msg.Action ?? "").ToLowerInvariant();
                if (powerAct is not ("sleep" or "lock" or "shutdown" or "restart"))
                {
                    await SendErrorAsync(socket, msg.RequestId, "invalid_command");
                    return;
                }
                SystemPower.Execute(powerAct);
                await SendAckAsync(socket, msg.RequestId);
                break;

            default:
                Console.WriteLine($"[?] Unknown message type: {msg.Type}");
                await SendErrorAsync(socket, msg.RequestId, "unknown_type");
                break;
        }
    }

    public static IEnumerable<string> GetLocalIPv4Addresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            string name = ni.Name ?? "";
            string desc = ni.Description ?? "";
            if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    yield return addr.Address.ToString();
                }
            }
        }
    }
}

// JSON remote message data model shared between client and server.
public class RemoteMessage
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
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
    [JsonPropertyName("success")] public bool? Success { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
}

// Host machine identity details.
public static class AgentInfo
{
    public static string Name => Environment.MachineName;
}

// Win32 SendInput and keyboard event simulation engine.
public static class Win32Input
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    private static bool Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return true;
        uint res = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (res == 0)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[!] SendInput returned 0, error={err} (UIPI blocked elevated window target or invalid param)");
            return false;
        }
        return true;
    }

    public static void MoveMouseRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } }
        };
        Send([input]);
    }

    public static void MouseClick(string button, string action)
    {
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        void Fire(uint flag) =>
            Send([new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } }]);

        switch (action)
        {
            case "down": Fire(down); break;
            case "up": Fire(up); break;
            default: Fire(down); Fire(up); break;
        }
    }

    public static void Scroll(int amount)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)(amount * 120)), dwFlags = MOUSEEVENTF_WHEEL } }
        };
        Send([input]);
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

    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        if (inputs.Count > 0)
            Send([.. inputs]);
    }

    public static void MediaControl(string action)
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
        if (vk == 0) return;

        keybd_event(vk, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
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
            Console.WriteLine($"[!] Error releasing buttons/modifiers: {ex.Message}");
        }
    }
}

// System power state management wrapper.
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
