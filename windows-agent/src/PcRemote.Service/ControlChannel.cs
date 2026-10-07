// The network face of the product: one TLS listener, one authentication step,
// then a straight switch over the input commands.
//
// Everything a paired phone can do is in HandleInputAsync, and every field is
// bounds-checked there before the message reaches the input path. There is no RPC
// envelope and no per-command acknowledgement: mouse movement is fire-and-forget
// so a slow phone never stalls the PC.
//
// This process runs as LocalSystem, so the listener outlives logoff, the lock
// screen and the tray.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PcRemote.Core;

namespace PcRemote.Service;

public sealed class ControlChannel
{
    public const int Port = 58642;

    /// <summary>How long a socket may stay connected without completing the
    /// handshake before it is dropped.</summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the command loop will wait for any frame. The phone
    /// pings every 15 s, so three silent ping intervals means the phone is gone
    /// (Wi-Fi drop, app killed) even though no TCP reset arrived. This is what
    /// triggers release-all when a phone vanishes mid-drag.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Largest cursor or scroll delta accepted from the network.</summary>
    private const int MaxDelta = 20_000;

    /// <summary>Longest text message accepted from the network.</summary>
    private const int MaxTextChars = 4096;

    private readonly PairingStore _pairing;
    private readonly string _pcId;
    private readonly InputRouter _input;
    private readonly Func<string> _currentState;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate;

    private TcpListener? _listener;
    private int _connected;

    public ControlChannel(
        PairingStore pairing,
        string pcId,
        InputRouter input,
        Func<string> currentState,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        _pairing = pairing;
        _pcId = pcId;
        _input = input;
        _currentState = currentState;
        _certificate = certificate;
    }

    /// <summary>Phones currently connected. Read by the tray over IPC.</summary>
    public int ConnectedCount => Volatile.Read(ref _connected);

    /// <summary>Fresh pairing code. Surfaced to the tray - and only to an elevated
    /// caller - so a user can read it off the PC.</summary>
    public string CurrentPairingCode => _pairing.CurrentCode;

    public async Task RunAsync(CancellationToken ct)
    {
        _pairing.RotateCode();
        _ = Task.Run(() => RotateCodeUntilCancelled(ct), CancellationToken.None);

        FirewallHelper.EnsureRules();
        MdnsAdvertiser.Start(Port, _pcId);

        // Dual-stack so one listener serves IPv4 and IPv6 LAN traffic.
        _listener = new TcpListener(IPAddress.IPv6Any, Port);
        _listener.Server.DualMode = true;
        _listener.Start();
        Log.Info($"listening on port {Port}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ServeAsync(tcp, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Port already bound by a stray second copy, or the socket died:
            // without this the listener task faults silently and the service
            // looks healthy while deaf. (A listener closed during shutdown
            // lands here too, but the filter keeps that quiet.)
            Log.Error($"listener stopped: {ex.Message}");
        }
    }

    public void Stop()
    {
        MdnsAdvertiser.Stop();
        try { _listener?.Stop(); } catch (SocketException) { /* already closed */ }
    }

    /// <summary>Pairing codes are short-lived; a stale one left on screen should
    /// stop working rather than sit around.</summary>
    private async Task RotateCodeUntilCancelled(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                _pairing.RotateCode();
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        var clientIp = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        var authenticated = false;

        try
        {
            using (tcp)
            {
                using var socket = await SecureSocket.AcceptAsync(tcp, _certificate);
                if (socket is null) return;

                try
                {
                    authenticated = await HandshakeAsync(socket, clientIp, ct);
                    if (!authenticated) return;

                    Interlocked.Increment(ref _connected);
                    Log.Info($"device connected from {clientIp}");
                    try
                    {
                        await ReadCommandsAsync(socket, ct);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _connected);
                    }
                }
                finally
                {
                    // Whether the phone left cleanly or vanished mid-drag, the PC
                    // must not be left holding a mouse button or a modifier.
                    if (authenticated) await ReleaseAllQuietly();
                }
            }
        }
        catch (Exception ex)
        {
            // One bad connection must never take the listener down.
            Log.Warn($"connection from {clientIp} ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (authenticated) Log.Info($"device disconnected ({ConnectedCount} remaining)");
        }
    }

    /// <summary>Best-effort release: a helper that is already gone has released
    /// its own keys on shutdown, so failure here is not worth surfacing.</summary>
    private async Task ReleaseAllQuietly()
    {
        try { await _input.ReleaseAll(); }
        catch (Exception ex) { Log.Warn($"release on disconnect failed: {ex.Message}"); }
    }

    /// <summary>The first message decides everything: a saved token, or a pairing
    /// code that mints one. Returns false when the socket has been refused.</summary>
    private async Task<bool> HandshakeAsync(SecureSocket socket, string clientIp, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + AuthTimeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            // Bounded per frame: a client that sends half a message and stalls
            // must not hold the handshake loop (and its socket) forever.
            var text = await socket.ReceiveTextAsync(AuthTimeout);
            if (text is null) return false;

            Message? request;
            try { request = JsonSerializer.Deserialize<Message>(text); }
            catch (JsonException) { continue; }

            if (request is null || request.Version != Protocol.Version) continue;

            if (request.Type != Protocol.Hello)
            {
                await SendErrorAsync(socket, "not_paired", ct);
                return false;
            }

            if (_pairing.IsBlocked(clientIp))
            {
                Log.Warn($"refused {clientIp}: too many failed pairing attempts");
                await SendErrorAsync(socket, "too_many_attempts", ct);
                return false;
            }

            if (!_pairing.TryAuthenticate(request.Token, request.Code, clientIp))
            {
                await SendErrorAsync(socket, "pairing_failed", ct);
                return false;
            }

            await SendAsync(socket, new Message
            {
                Type = Protocol.Welcome,
                // The token that was presented, or a new one on a first pairing.
                Token = _pairing.IssueTokenIfNeeded(request.Token),
                PcName = Environment.MachineName,
                PcId = _pcId,
                State = _currentState(),
            }, ct);
            return true;
        }
        return false;
    }

    private async Task ReadCommandsAsync(SecureSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var text = await socket.ReceiveTextAsync(IdleTimeout);
            if (text is null) return; // phone closed the socket, or went silent

            Message? message;
            try { message = JsonSerializer.Deserialize<Message>(text); }
            catch (JsonException) { continue; }
            if (message is null || message.Version != Protocol.Version) continue;

            if (message.Type == Protocol.Disconnect) return;

            // Anything not on the allowlist is ignored outright: the service is
            // never a general command executor.
            if (!Protocol.IsAccepted(message.Type)) continue;

            try
            {
                await HandleInputAsync(message);
            }
            catch (InputRouter.UnavailableException ex)
            {
                // The PC is on a desktop we cannot reach right now. Telling the
                // phone is better than silently swallowing its input.
                Log.Warn($"input unavailable: {ex.Message}");
                await SendErrorAsync(socket, "desktop_unavailable", ct);
                return;
            }
        }
    }

    /// <summary>Validates one input message and forwards it to the desktop that is
    /// currently receiving input. Fields are checked here even though the helper
    /// checks them again - this is the trust boundary for the network.</summary>
    private async Task HandleInputAsync(Message m)
    {
        switch (m.Type)
        {
            case Protocol.Move when Bounded(m.Dx) && Bounded(m.Dy):
                await _input.Move(m.Dx ?? 0, m.Dy ?? 0);
                break;

            case Protocol.Button when m.Button is not null:
                await _input.Button(m.Button, m.Action ?? "");
                break;

            case Protocol.Scroll when Bounded(m.Delta):
                await _input.Scroll(m.Delta ?? 0);
                break;

            case Protocol.Key when m.Key is not null:
                await _input.Key(m.Key, m.Action ?? "");
                break;

            case Protocol.Text when m.Text is { Length: <= MaxTextChars }:
                await _input.Text(m.Text);
                break;

            case Protocol.ReleaseAll:
                await ReleaseAllQuietly();
                break;

            default:
                Log.Warn($"ignored malformed '{m.Type}'");
                break;
        }

        static bool Bounded(int? v) => v is null || Math.Abs(v.Value) <= MaxDelta;
    }

    private static Task SendAsync(SecureSocket socket, Message message, CancellationToken ct) =>
        socket.SendTextAsync(JsonSerializer.Serialize(message));

    private static Task SendErrorAsync(SecureSocket socket, string reason, CancellationToken ct) =>
        SendAsync(socket, new Message { Type = Protocol.Error, Code = reason }, ct);
}