// WSS control channel: listener + per-connection state machine. Runs inside the
// LocalSystem service, so the control surface survives logoff, lock, tray
// crashes and reboot. Carries control traffic only — never video frames.

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

    // Screen-stream defaults (adaptive tuning lands with the UX phase).
    public const int DefaultStreamFps = 15;
    public const int DefaultStreamBitrate = 4_000_000;

    /// <summary>Test seam: when set, stream_start binds to the returned file instead
    /// of driving the real session agent, so loopback tests can tail a temp fMP4.</summary>
    internal Func<StreamBinding?>? StreamBinderOverride { get; set; }

    private readonly PairingStore _pairing;
    private readonly string _pcId;
    private readonly InputRouter _inputRouter;
    private readonly SessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, ConnectionSlot> _connected = new();
    private TcpListener? _listener;
    private readonly X509Certificate2? _certOverride;

    public event Action<int>? ConnectedCountChanged;
    public event Action<string>? PairingCodeChanged;

    /// <summary>Request path that selects the binary media channel on this port.</summary>
    public const string StreamPath = "/stream";

    /// <summary>A live connection slot. Stream (media) sockets count toward the
    /// per-IP/total connection limits but not toward the visible device count.</summary>
    private sealed record ConnectionSlot(DateTime LastSeen, bool Stream);

    public int ConnectedCount
    {
        get { lock (_connected) return _connected.Values.Count(s => !s.Stream); }
    }

    /// <summary>Live media sockets (diagnostics: how many clients are watching).</summary>
    public int StreamingCount
    {
        get { lock (_connected) return _connected.Values.Count(s => s.Stream); }
    }

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
                        stale = _connected.Where(kv => kv.Value.LastSeen < threshold).Select(kv => kv.Key).ToList();
                        foreach (var key in stale) _connected.TryRemove(key, out _);
                    }
                    if (stale.Count > 0) AgentLog.Debug($"Cleaned up {stale.Count} stale connection slot(s)");
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
        AgentLog.Info($"WSS control channel listening on port {Port}");

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
            _connected[connKey] = new ConnectionSlot(DateTime.UtcNow, Stream: false);
            return true;
        }
    }

    /// <summary>Reclassifies a slot as a media socket once the upgrade path is
    /// known (the path is only readable after the TLS/WS handshake, which happens
    /// after the slot is acquired to bound pre-auth connections).</summary>
    private void MarkAsStream(string connKey)
    {
        lock (_connected)
        {
            if (_connected.TryGetValue(connKey, out var slot))
                _connected[connKey] = slot with { Stream = true };
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
            if (_connected.TryGetValue(connKey, out var slot))
                _connected[connKey] = slot with { LastSeen = DateTime.UtcNow };
        }
    }

    internal async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var clientIp = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "unknown";
        if (!TryAcquireConnectionSlot(clientIp, out var connKey))
        {
            AgentLog.Warn($"{clientIp} rejected: connection limit exceeded");
            client.Dispose();
            return;
        }

        var isStream = false;
        try
        {
            var cert = _certOverride ?? ControlChannelCert.Value;
            using var socket = await WebSocketConnection.AcceptAsync(client.GetStream(), cert, isSecure: true);
            if (socket is null)
            {
                AgentLog.Warn($"{clientIp} rejected (TLS handshake failed)");
                return;
            }

            // The request path decides the connection's role: "/stream" is the
            // binary media channel (video only), anything else is the JSON control
            // surface. Same TLS listener + cert, so no new port and the same pin.
            isStream = string.Equals(socket.RequestPath, StreamPath, StringComparison.Ordinal);
            if (isStream) MarkAsStream(connKey);

            AgentLog.Info($"Connection from {clientIp} (TLS, path={socket.RequestPath ?? "/"})");
            PrintConnectedCount();

            if (isStream)
                await HandleStreamConnectionAsync(socket, clientIp, connKey, ct);
            else
                await HandleControlConnectionAsync(socket, clientIp, connKey, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AgentLog.Error($"Connection error from {clientIp}: {ex.Message}");
        }
        finally
        {
            _connected.TryRemove(connKey, out _);
            if (!isStream)
            {
                // Never leave the PC with a mouse button held down.
                await _inputRouter.ReleaseAllButtons();
            }
            AgentLog.Info($"{clientIp} disconnected");
            PrintConnectedCount();
            client.Dispose();
        }
    }

    /// <summary>JSON control socket: the existing authenticated command path.</summary>
    private async Task HandleControlConnectionAsync(WebSocketConnection socket, string clientIp, string connKey, CancellationToken ct)
    {
        var authenticated = false;
        var authDeadline = DateTime.UtcNow.AddSeconds(15);
        var messageCount = 0;
        var windowStart = DateTime.UtcNow;
        var muteUntil = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            if (!authenticated && DateTime.UtcNow > authDeadline)
            {
                AgentLog.Warn($"{clientIp} disconnected (authentication timeout)");
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

    /// <summary>Media socket: authenticated binary channel carrying encoded video
    /// bytes from the agent's stream to one client. Carries no input command
    /// surface — the connection is the /stream path, so it cannot be confused with
    /// the control socket, and anything that is not stream lifecycle / keyframe is
    /// rejected.</summary>
    private async Task HandleStreamConnectionAsync(WebSocketConnection socket, string clientIp, string connKey, CancellationToken ct)
    {
        var authenticated = false;
        var authDeadline = DateTime.UtcNow.AddSeconds(15);
        CancellationTokenSource? forwarderCts = null;
        Task? forwarderTask = null;
        SessionWorkerClient? streamWorker = null;
        FpsPolicy? fpsPolicy = null;

        async Task StopStreamingAsync()
        {
            if (forwarderCts is not null)
            {
                forwarderCts.Cancel();
                try { if (forwarderTask is not null) await forwarderTask; } catch { }
                forwarderCts.Dispose();
                forwarderCts = null;
                forwarderTask = null;
            }
            if (streamWorker is not null)
            {
                try { streamWorker.StopStream(); } catch { }
                streamWorker = null;
            }
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!authenticated && DateTime.UtcNow > authDeadline)
                {
                    AgentLog.Warn($"{clientIp} stream socket disconnected (authentication timeout)");
                    break;
                }

                var text = await socket.ReceiveTextAsync();
                if (text is null) break;

                TouchConnectionSlot(connKey);

                RemoteMessage? msg;
                try { msg = JsonSerializer.Deserialize<RemoteMessage>(text); }
                catch { continue; }
                if (msg is null || msg.Version != 1) continue;

                if (!authenticated)
                {
                    authenticated = await HandleAuthAsync(socket, msg, clientIp, connKey);
                    continue;
                }

                switch (msg.Type)
                {
                    case "stream_start":
                    {
                        if (forwarderCts is not null)
                        {
                            await SendErrorAsync(socket, msg.RequestId, "already_streaming");
                            break;
                        }

                        var (binding, worker) = ResolveStreamBinding();
                        if (binding is null)
                        {
                            // Honest failure state: no interactive desktop or no
                            // agent to capture with — never pretend video is coming.
                            AgentLog.Warn($"{clientIp} stream unavailable (no user session or desktop)");
                            await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                            {
                                Version = 1, RequestId = msg.RequestId, Type = "stream_state",
                                StreamState = "error", ErrorCode = "stream_unavailable",
                            }));
                            break;
                        }

                        AgentLog.Info($"{clientIp} streaming {binding.Value.Width}x{binding.Value.Height}@{binding.Value.Fps}");
                        await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                        {
                            Version = 1, RequestId = msg.RequestId, Type = "stream_state",
                            StreamState = "active",
                            Width = binding.Value.Width, Height = binding.Value.Height, Fps = binding.Value.Fps,
                        }));

                        // Forwarder must be started only after the state reply so
                        // the client sees metadata before the first video bytes.
                        streamWorker = worker;
                        var forwardedWorker = worker;
                        fpsPolicy = new FpsPolicy(binding.Value.Fps);
                        forwarderCts = new CancellationTokenSource();
                        var forwarder = new StreamForwarder(
                            binding.Value.Path,
                            frame => socket.SendBinaryAsync(frame.ToBytes()),
                            onDropped: () => Task.Run(() =>
                            {
                                try
                                {
                                    forwardedWorker?.RequestStreamKeyframe();
                                    var newFps = fpsPolicy.OnCongestion(DateTime.UtcNow);
                                    AgentLog.Info($"stream congestion: FPS adapted to {newFps}");
                                    forwardedWorker?.SetStreamFps(newFps);
                                }
                                catch { }
                            }),
                            onHealthy: () => Task.Run(() =>
                            {
                                try
                                {
                                    var newFps = fpsPolicy.OnHealthyTick(DateTime.UtcNow);
                                    if (newFps is int fps)
                                    {
                                        AgentLog.Info($"stream recovered: FPS adapted to {fps}");
                                        forwardedWorker?.SetStreamFps(fps);
                                    }
                                }
                                catch { }
                            }));
                        forwarderTask = Task.Run(() => forwarder.RunAsync(forwarderCts.Token), CancellationToken.None);
                        break;
                    }

                    case "keyframe_request":
                        // Relay to the agent encoder for an IDR (join / loss recovery).
                        try { streamWorker?.RequestStreamKeyframe(); } catch { }
                        break;

                    case "stream_stop":
                        AgentLog.Info($"{clientIp} requested stream stop");
                        await socket.SendTextAsync(JsonSerializer.Serialize(new RemoteMessage
                        {
                            Version = 1, RequestId = msg.RequestId, Type = "stream_state",
                            StreamState = "stopped",
                        }));
                        await StopStreamingAsync();
                        // Complete the close handshake before the socket is disposed,
                        // so the client sees a clean end instead of a RST.
                        await socket.CloseAsync();
                        return;

                    default:
                        // The media socket has no command surface; input must come
                        // from the control socket to the same device.
                        await SendErrorAsync(socket, msg.RequestId, "unknown_type");
                        break;
                }
            }
        }
        finally
        {
            // Client vanished mid-stream: never leave the agent encoding for nobody.
            await StopStreamingAsync();
        }
    }

    /// <summary>Bind this media socket to a live stream: ask the user-session agent
    /// to start capture+encode and return the fMP4 file to tail. Null = unavailable.</summary>
    private (StreamBinding? Binding, SessionWorkerClient? Worker) ResolveStreamBinding()
    {
        if (StreamBinderOverride is not null) return (StreamBinderOverride(), null);

        var worker = _sessionManager.GetSessionWorker() as SessionWorkerClient;
        if (worker is not { IsAlive: true }) return (null, worker);
        return (worker.StartStream(DefaultStreamFps, DefaultStreamBitrate), worker);
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
            AgentLog.Warn($"{clientIp} authentication blocked (locked out)");
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
            AgentLog.Info($"{clientIp} authenticated");
            return true;
        }

        AgentLog.Warn($"{clientIp} authentication failed");
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
            AgentLog.Warn($"Rejected message type: {msg.Type}");
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

                case "mouse_move_abs":
                    if (!await _inputRouter.MouseMoveAbsolute(
                            Math.Clamp(msg.X ?? 0, 0, 1_000_000),
                            Math.Clamp(msg.Y ?? 0, 0, 1_000_000)))
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
                    // simulate Ctrl+Alt+Del. Only SYSTEM
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
        var devices = ConnectedCount; // stream (media) sockets are not devices
        AgentLog.Info($"{devices} device(s) connected");
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
