// WSS control channel: listener + per-connection state machine.
// Runs inside the Windows service (LocalSystem), so the control surface
// survives logoff, lock, tray crashes and reboot (requirement 1).
// Authentication and pairing live here; the JSON channel carries control
// traffic only — video frames never travel through it (requirement 6).

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PcRemote.Core;

namespace PcRemote.Service;

public sealed class ControlChannel
{
    public const int Port = 58642;
    public const int MaxTotalConnections = 10;
    public const int MaxConnectionsPerIp = 3;

    private readonly PairingStore _pairing;
    private readonly string _pcId;
    private readonly InputRouter _inputRouter;
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, DateTime> _connected = new();
    private TcpListener? _listener;
    private readonly X509Certificate2? _certOverride;

    public event Action<int>? ConnectedCountChanged;
    public event Action<string>? PairingCodeChanged;

    public int ConnectedCount { get { lock (_connected) return _connected.Count; } }

    public ControlChannel(PairingStore pairing, string pcId, InputRouter inputRouter, SessionManager sessionManager,
        X509Certificate2? certificate = null)
    {
        _pairing = pairing;
        _pcId = pcId;
        _inputRouter = inputRouter;
        _sessionManager = sessionManager;
        _certOverride = certificate;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        PairingCodeChanged?.Invoke(_pairing.GeneratePairingCode());

        // Pairing codes expire and regenerate on a timer.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PairingStore.CodeLifetime, ct);
                    lock (_pairing)
                    {
                        if (!_pairing.IsCodeExpired()) continue;
                        var code = _pairing.GeneratePairingCode();
                        PairingCodeChanged?.Invoke(code);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        // Stale connection slot cleanup (clients that died without a close frame).
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    var threshold = DateTime.UtcNow.AddSeconds(-60);
                    List<string> stale;
                    lock (_connected)
                    {
                        stale = _connected.Where(kv => kv.Value < threshold).Select(kv => kv.Key).ToList();
                        foreach (var key in stale) _connected.TryRemove(key, out _);
                    }
                    if (stale.Count > 0) Console.WriteLine($"[~] Cleaned up {stale.Count} stale connection slot(s)");
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        FirewallHelper.EnsureFirewallRules();
        MdnsAdvertiser.Start(Port, _pcId);

        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, Port);
            _listener.Server.DualMode = true;
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start();
        }
        catch
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start();
        }
        Console.WriteLine($"[+] WSS control channel listening on port {Port}");

        while (!ct.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => HandleConnectionAsync(client, ct));
        }
    }

    public void Stop()
    {
        MdnsAdvertiser.Stop();
        try { _listener?.Stop(); } catch { }
    }

    private bool TryAcquireConnectionSlot(string clientIp, out string connKey)
    {
        connKey = $"{clientIp}:{Guid.NewGuid():N}";
        lock (_connected)
        {
            var currentTotal = _connected.Count;
            var currentIpCount = _connected.Keys.Count(k => k.StartsWith(clientIp + ":", StringComparison.Ordinal));
            if (currentTotal >= MaxTotalConnections || currentIpCount >= MaxConnectionsPerIp) return false;
            _connected[connKey] = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>Refreshes a slot's liveness timestamp. Called for every message
    /// received on a connection; without it the stale-slot sweeper would evict
    /// healthy long-lived sessions after 60 s and quietly disable the
    /// connection limits.</summary>
    private void TouchConnectionSlot(string connKey)
    {
        lock (_connected)
        {
            if (_connected.ContainsKey(connKey)) _connected[connKey] = DateTime.UtcNow;
        }
    }

    internal async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var clientIp = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "unknown";
        if (!TryAcquireConnectionSlot(clientIp, out var connKey))
        {
            Console.WriteLine($"[!] {clientIp} rejected: connection limit exceeded");
            client.Dispose();
            return;
        }

        try
        {
            var cert = _certOverride ?? ControlChannelCert.Value;
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

            while (!ct.IsCancellationRequested)
            {
                if (!authenticated && DateTime.UtcNow > authDeadline)
                {
                    Console.WriteLine($"[!] {clientIp} disconnected (authentication timeout)");
                    break;
                }

                var text = await socket.ReceiveTextAsync();
                if (text is null) break;

                // Any traffic proves the connection is alive; keep its slot.
                TouchConnectionSlot(connKey);

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
                if (DateTime.UtcNow < muteUntil) continue;

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
                    authenticated = await HandleAuthAsync(socket, msg, clientIp, connKey);
                    continue;
                }

                if (msg.Type == "system_power" && msg.Action is "shutdown" or "restart")
                {
                    await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                    {
                        Version = 1, RequestId = msg.RequestId, Type = "disconnecting", Reason = msg.Action
                    }));
                }

                await HandleCommandAsync(socket, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Connection error from {clientIp}: {ex.Message}");
        }
        finally
        {
            _connected.TryRemove(connKey, out _);
            InputRouter.ReleaseAllButtons();
            Console.WriteLine($"[-] {clientIp} disconnected");
            PrintConnectedCount();
            client.Dispose();
        }
    }

    private async Task<bool> HandleAuthAsync(WebSocketConnection socket, RemoteMessage msg, string clientIp, string connKey)
    {
        if (msg.Type != "auth")
        {
            await SendErrorAsync(socket, msg.RequestId, "unauthorized");
            return false;
        }

        if (_pairing.IsIpLockedOut(clientIp))
        {
            Console.WriteLine($"[!] {clientIp} authentication blocked (locked out)");
            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
            {
                Version = 1, RequestId = msg.RequestId, Type = "auth_failed", ErrorCode = "rate_limited"
            }));
            return false;
        }

        if (_pairing.TryAuthenticate(msg.Token, msg.PairingCode, clientIp))
        {
            var token = _pairing.IssueTokenIfNeeded(msg.Token);
            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
            {
                Version = 1,
                RequestId = msg.RequestId,
                Type = "auth_ok",
                Token = token,
                PcName = Environment.MachineName,
                PcId = _pcId,
                SessionState = _sessionManager.CurrentState.ToString().ToLowerInvariant(),
                ConnKey = connKey
            }));
            Console.WriteLine($"[+] {clientIp} authenticated");
            return true;
        }

        Console.WriteLine($"[!] {clientIp} authentication failed");
        await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
        {
            Version = 1, RequestId = msg.RequestId, Type = "auth_failed", ErrorCode = "invalid_credentials"
        }));
        return false;
    }

    /// <summary>Allowlisted command dispatch. Everything not in the allowlist
    /// gets "unknown_type"; the service never executes arbitrary commands.</summary>
    internal async Task HandleCommandAsync(WebSocketConnection socket, RemoteMessage msg)
    {
        if (!CommandAllowlist.IsAllowed(msg.Type))
        {
            Console.WriteLine($"[?] Rejected message type: {msg.Type}");
            await SendErrorAsync(socket, msg.RequestId, "unknown_type");
            return;
        }

        try
        {
            switch (msg.Type)
            {
                case "mouse_move":
                    if (!await _inputRouter.MouseMoveRelative(
                            Math.Clamp(msg.Dx ?? 0, -4096, 4096),
                            Math.Clamp(msg.Dy ?? 0, -4096, 4096)))
                    {
                        await SendErrorAsync(socket, msg.RequestId, "input_unavailable");
                        return;
                    }
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
                    if (!await _inputRouter.MouseClick(btn, act))
                    {
                        await SendErrorAsync(socket, msg.RequestId, "input_unavailable");
                        return;
                    }
                    await SendAckAsync(socket, msg.RequestId);
                    break;

                case "mouse_scroll":
                    if (!await _inputRouter.Scroll(Math.Clamp(msg.Dy ?? 0, -1200, 1200)))
                    {
                        await SendErrorAsync(socket, msg.RequestId, "input_unavailable");
                        return;
                    }
                    await SendAckAsync(socket, msg.RequestId);
                    break;

                case "key_press":
                    if (string.IsNullOrEmpty(msg.Key) || !await _inputRouter.SendKey(msg.Key, msg.Modifiers ?? []))
                    {
                        await SendErrorAsync(socket, msg.RequestId, "bad_field");
                        return;
                    }
                    await SendAckAsync(socket, msg.RequestId);
                    break;

                case "text_input":
                    var text = msg.Text ?? "";
                    if (text.Length > 1000) text = text[..1000];
                    if (!await _inputRouter.TypeText(text))
                    {
                        await SendErrorAsync(socket, msg.RequestId, "input_unavailable");
                        return;
                    }
                    await SendAckAsync(socket, msg.RequestId);
                    break;

                case "media_control":
                    var mediaAct = (msg.Action ?? "").ToLowerInvariant();
                    if (mediaAct is not ("play_pause" or "next" or "prev" or "vol_up" or "vol_down" or "mute"))
                    {
                        await SendErrorAsync(socket, msg.RequestId, "invalid_command");
                        return;
                    }
                    await _inputRouter.MediaControl(mediaAct);
                    await SendAckAsync(socket, msg.RequestId);
                    break;

                case "sas":
                    // Secure attention sequence — the one correct way to
                    // simulate Ctrl+Alt+Del (requirement 4). Only SYSTEM
                    // services may call SendSAS; this process qualifies.
                    SasController.SendSas();
                    await SendAckAsync(socket, msg.RequestId);
                    break;

                case "session_status":
                    await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                    {
                        Version = 1,
                        RequestId = msg.RequestId,
                        Type = "session_status",
                        PcName = Environment.MachineName,
                        SessionState = _sessionManager.CurrentState.ToString().ToLowerInvariant()
                    }));
                    break;

                case "system_power":
                    var powerAct = (msg.Action ?? "").ToLowerInvariant();
                    if (powerAct is not ("sleep" or "lock" or "shutdown" or "restart"))
                    {
                        await SendErrorAsync(socket, msg.RequestId, "invalid_command");
                        return;
                    }
                    PowerController.Execute(powerAct);
                    await SendAckAsync(socket, msg.RequestId);
                    break;

                case "stream_request":
                    // Media channel negotiation is Phase 5; acknowledge with
                    // "not_ready" so clients can degrade gracefully.
                    await SendErrorAsync(socket, msg.RequestId, "not_ready");
                    break;

                case "disconnect":
                    // Acknowledge, then let the client close: returning without
                    // terminating left the socket open until the peer timed out.
                    await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                    {
                        Version = 1, RequestId = msg.RequestId, Type = "disconnect_ack"
                    }));
                    return;
            }
        }
        catch (SessionUnavailableException)
        {
            await SendErrorAsync(socket, msg.RequestId, "session_unavailable");
        }
    }

    private void PrintConnectedCount()
    {
        var devices = _connected.Count;
        Console.WriteLine($"     {devices} device(s) connected");
        ConnectedCountChanged?.Invoke(devices);
    }

    private static async Task SendAckAsync(WebSocketConnection socket, string? requestId)
    {
        if (string.IsNullOrEmpty(requestId)) return;
        await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
        {
            Version = 1, RequestId = requestId, Type = "command_result", Success = true
        }));
    }

    private static async Task SendErrorAsync(WebSocketConnection socket, string? requestId, string errorCode)
    {
        await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
        {
            Version = 1, RequestId = requestId, Type = "command_result", Success = false, ErrorCode = errorCode
        }));
    }

    // Per-process certificate: loaded once, owned by the service.
    private static readonly Lazy<X509Certificate2> ControlChannelCert =
        new(() => CertificateManager.LoadOrCreate(), LazyThreadSafetyMode.ExecutionAndPublication);
}
