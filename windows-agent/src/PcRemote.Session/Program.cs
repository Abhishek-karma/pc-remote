// PC Remote Session Helper. One process, two modes:
//   * default:        user token, injects into the session's normal desktop.
//   * --secure-input: SYSTEM token in the console session, injects into the
//                     Winlogon desktop (UAC, credential prompt, lock, logon).
//   * --lock:         one-shot LockWorkStation request from the service.
//
// This process NEVER talks to the network and NEVER persists anything. It
// re-validates every relayed command against its own allowlist, and keystrokes
// are executed then discarded — never logged or buffered.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PcRemote.Core;
using PcRemote.Session;
using PcRemote.Session.Streaming;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--lock"))
        {
            LockWorkStation();
            return 0;
        }

        var secure = args.Contains("--secure-input");
        AgentLog.Init(Path.Combine(PairingStore.ServiceDataDir, "logs"));
        AgentLog.SetSubsystem(secure ? "secure" : "session");

        // Both modes derive the pipe name from this process's REAL session id:
        // it must match the id the service launched us with, and WTS cannot be
        // trusted to report it back the same way once the console session has
        // no user attached (0xFFFFFFFF at the logon screen).
        var sessionId = GetOwnSessionId();
        // Shared with the service (IpcEndpoints.SessionPipe) so the normal and
        // secure helpers can never be confused for one another.
        var pipeName = IpcEndpoints.SessionPipe(sessionId, secure);

        AgentLog.Info($"helper starting (secure={secure}, session={sessionId}, pipe={pipeName})");

        var injector = new DesktopInjector(secure);
        // Streaming is a user-session capability; the secure (Winlogon) helper must
        // never capture or encode.
        var coordinator = secure ? null : new StreamingCoordinator();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var server = new SessionPipeServer(pipeName, injector, secure, coordinator);
        var serverTask = server.RunAsync(cts.Token);

        if (secure)
        {
            // We live in the console session, so we can report Winlogon
            // transitions (UAC/lock/logon) back to the service in session 0.
            // Reports carry only the desktop name — never keystrokes.
            var reporter = Task.Run(async () =>
            {
                string? lastReported = null;
                while (!cts.IsCancellationRequested)
                {
                    var name = SecureDesktop.GetActiveInputDesktopName();
                    if (name != lastReported)
                    {
                        lastReported = name;
                        try
                        {
                            using var client = new IpcClient();
                            client.Connect(1500);
                            client.RoundTrip(new IpcMessage
                            {
                                Type = "desktop_report",
                                Role = "secure",
                                SessionId = sessionId,
                                SessionState = name?.ToLowerInvariant() ?? "unknown",
                            }, 2000);
                        }
                        catch { /* service briefly busy; retry next tick */ }
                    }
                    await Task.Delay(500, cts.Token);
                }
            }, cts.Token);

            await reporter;
        }

        try { await serverTask; } catch (OperationCanceledException) { }
        InputInjector.ReleaseAllButtons();
        AgentLog.Info("helper stopping");
        return 0;
    }

    private static int GetOwnSessionId()
    {
        var id = Kernel32.ProcessIdToSessionIdSafe();
        return id == 0 ? 1 : (int)id;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}

internal static class Kernel32
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    public static uint WTSGetActiveConsoleSessionIdSafe() => WTSGetActiveConsoleSessionId();

    public static uint ProcessIdToSessionIdSafe() =>
        ProcessIdToSessionId((uint)System.Diagnostics.Process.GetCurrentProcess().Id, out var id) ? id : 0u;
}

/// <summary>
/// Executes allowlisted input commands on the correct desktop.
/// </summary>
internal sealed class DesktopInjector
{
    private readonly bool _secure;

    public DesktopInjector(bool secure) => _secure = secure;

    /// <summary>Deliberately narrower than the service-side allowlist: power/SAS
    /// must never be relayed into a user session.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "mouse_move", "mouse_move_abs", "mouse_click", "mouse_scroll", "key_press", "text_input", "media_control",
        "release_all",
    };

    public bool Execute(string type, RemoteMessage msg)
    {
        if (!Allowed.Contains(type)) return false;

        if (_secure)
        {
            // Attach this thread to the Winlogon desktop if that is the active
            // input desktop; refuse to act otherwise (a SYSTEM helper must never
            // inject into the user session behind the user's back).
            if (SecureDesktop.AttachCurrentThreadToSecureInputDesktop() is null) return false;
        }

        switch (type)
        {
            case "mouse_move":
                return InputInjector.MoveMouseRelative(
                    Math.Clamp(msg.Dx ?? 0, -4096, 4096),
                    Math.Clamp(msg.Dy ?? 0, -4096, 4096));
            case "mouse_move_abs":
                // Desktop-view tap: absolute position in the captured coordinate space.
                return InputInjector.MoveMouseAbsolute(
                    Math.Clamp(msg.X ?? 0, 0, 1_000_000),
                    Math.Clamp(msg.Y ?? 0, 0, 1_000_000));
            case "mouse_click":
                var btn = (msg.Button ?? "left").ToLowerInvariant();
                var act = (msg.Action ?? "click").ToLowerInvariant();
                if (btn is not ("left" or "right" or "middle") || act is not ("click" or "down" or "up"))
                    return false;
                return InputInjector.MouseClick(btn, act);
            case "mouse_scroll":
                return InputInjector.Scroll(Math.Clamp(msg.Dy ?? 0, -1200, 1200));
            case "key_press":
                if (string.IsNullOrEmpty(msg.Key)) return false;
                return InputInjector.SendKey(msg.Key, msg.Modifiers ?? []);
            case "text_input":
                var text = msg.Text ?? "";
                if (text.Length > 1000) text = text[..1000];
                // Executed and discarded: nothing about the typed text is
                // buffered, logged or returned anywhere.
                return InputInjector.TypeText(text);
            case "media_control":
                var mediaAct = (msg.Action ?? "").ToLowerInvariant();
                if (mediaAct is not ("play_pause" or "next" or "prev" or "vol_up" or "vol_down" or "mute"))
                    return false;
                return InputInjector.MediaControl(mediaAct);
            case "release_all":
                // Sent by the service when a client drops mid-drag so the PC is
                // never left with a mouse button held down.
                InputInjector.ReleaseAllButtons();
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Pipe server the service relays input through. One request per
/// connection, same framing as the service's own IPC server.</summary>
internal sealed class SessionPipeServer
{
    private const string PipePrefix = "PCRemoteSessionCtl-";
    private readonly string _pipeName;
    private readonly DesktopInjector _injector;
    private readonly bool _secure;
    private readonly StreamingCoordinator? _streaming;

    public SessionPipeServer(string pipeName, DesktopInjector injector, bool secure,
        StreamingCoordinator? streaming = null)
    {
        _pipeName = pipeName;
        _injector = injector;
        _secure = secure;
        _streaming = streaming;
    }

    // Accept here (bounds live instances), then hand the connected instance to its
    // own task — one wedged caller can never occupy the accept slot. Disposal
    // ownership moves with the instance: the handler task disposes it.
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server;
            try
            {
                var ps = new PipeSecurity();
                // Only SYSTEM and Administrators may drive the helper.
                ps.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                // The helper's own user: creating additional instances of an
                // existing pipe requires FILE_CREATE_PIPE_INSTANCE from the
                // DACL. (CreatorOwner was tried and empirically does not match
                // the creating token at access-check time on Win11 25H2 — the
                // explicit user SID does.)
                ps.AddAccessRule(new PipeAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    PipeAccessRights.FullControl, AccessControlType.Allow));

                server = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 4096, outBufferSize: 4096, ps);
            }
            catch (Exception ex)
            {
                AgentLog.Error($"pipe creation failed: {ex.Message}");
                await Task.Delay(3000, ct);
                continue;
            }

            var current = server;
            try
            {
                // Wait here (bounds live instances), then hand the connected
                // instance to its own task — one wedged caller can never
                // occupy the accept slot. DISPOSAL OWNERSHIP moves with the
                // instance: the handler task disposes it; nothing on this
                // path may touch it afterwards.
                await current.WaitForConnectionAsync(ct);
                AgentLog.Info("client connected");
                var handled = current;
                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(handled); }
                    catch (Exception ex) { AgentLog.Error($"handler error: {ex.Message}"); }
                    finally { try { handled.Dispose(); } catch { } }
                });
            }
            catch (OperationCanceledException)
            {
                try { current.Dispose(); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                // Accept failed before any hand-off: this instance is ours to
                // clean up. Once handed off, the handler task owns disposal.
                AgentLog.Error($"connection error: {ex.Message}");
                try { current.Dispose(); } catch { }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(server, lenBuf))
        {
            AgentLog.Debug("client connected but sent nothing (abandoned?)");
            return;
        }
        var len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > (1 << 20)) return;
        var body = new byte[len];
        if (!await ReadExactAsync(server, body)) return;
        AgentLog.Debug($"request received ({len} bytes)");

        IpcMessage? request;
        try { request = JsonSerializer.Deserialize<IpcMessage>(Encoding.UTF8.GetString(body)); }
        catch { return; }
        if (request is null) return;

        var ok = false;
        JsonElement? replyPayload = null;
        // Role guard: this process only ever serves the role it was launched
        // for. The pipe name already encodes it, but the request is verified
        // too so a future rename/caller bug cannot make the SYSTEM/Winlogon
        // helper execute normal-desktop input (or the reverse).
        var expectedRole = _secure ? "secure" : "session";
        if (!string.Equals(request.Role, expectedRole, StringComparison.Ordinal))
        {
            AgentLog.Warn($"refused {request.Type}: role '{request.Role}' != '{expectedRole}'");
        }
        else if (request.Type is "stream_start" or "stream_stop" or "stream_keyframe" or "stream_fps")
        {
            (ok, replyPayload) = HandleStreamCommand(request);
        }
        else if (request.Payload is { } payload)
        {
            try
            {
                var msg = payload.Deserialize<RemoteMessage>();
                if (msg is not null && request.Type == msg.Type) // type confusion guard
                    ok = _injector.Execute(request.Type, msg);
            }
            catch { ok = false; }
        }

        var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new IpcMessage
        {
            Type = request.Type,
            Ok = ok,
            Payload = replyPayload,
        }));
        var replyLen = BitConverter.GetBytes(reply.Length);
        await server.WriteAsync(replyLen);
        await server.WriteAsync(reply);
        await server.FlushAsync();
    }

    /// <summary>Streaming lifecycle commands from the service. Only the normal
    /// (user-session) helper has a coordinator; the secure helper refuses.</summary>
    private (bool Ok, JsonElement? Payload) HandleStreamCommand(IpcMessage request)
    {
        if (_streaming is null)
        {
            AgentLog.Warn($"refused {request.Type}: no streaming coordinator in this helper");
            return (false, null);
        }

        switch (request.Type)
        {
            case "stream_start":
            {
                var req = new StreamStartRequest();
                try { if (request.Payload is { } p) req = p.Deserialize<StreamStartRequest>() ?? req; }
                catch { /* fall back to defaults */ }

                var info = _streaming.Start(req.Fps, req.Bitrate);
                if (info is null) return (false, null); // no interactive desktop to capture

                AgentLog.Info($"stream started: {info.Value.Width}x{info.Value.Height}@{info.Value.Fps}");
                return (true, JsonSerializer.SerializeToElement(new StreamBindingInfo
                {
                    Path = info.Value.Path,
                    Width = info.Value.Width,
                    Height = info.Value.Height,
                    Fps = info.Value.Fps,
                }));
            }
            case "stream_stop":
                _streaming.Stop();
                AgentLog.Info("stream stopped");
                return (true, null);
            case "stream_keyframe":
                _streaming.RequestKeyframe();
                return (true, null);
            case "stream_fps":
            {
                var req = new StreamFpsRequest();
                try { if (request.Payload is { } p) req = p.Deserialize<StreamFpsRequest>() ?? req; }
                catch { /* fall back to default */ }
                _streaming.SetFps(req.Fps);
                return (true, null);
            }
            default:
                return (false, null);
        }
    }

    private static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
