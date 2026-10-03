// PC Remote Session Helper.
//
// One process, three modes (requirement 2/4):
//   * default: runs in the interactive user session, hosts the input pipe and
//     injects input into the normal desktop. Launched by the service with the
//     console user's token.
//   * --secure-input: runs as SYSTEM inside the console session and injects
//     into the Winlogon desktop (UAC consent, credential prompt, lock screen,
//     logon UI). Switches the worker thread to the secure desktop when it
//     becomes the active input desktop.
//   * --lock: one-shot lock request from the service (LockWorkStation must run
//     inside the interactive session).
//
// This process NEVER talks to the network and NEVER persists anything. It
// re-validates every relayed command against the same allowlist the service
// uses (defense in depth). Keystrokes are executed and immediately discarded:
// nothing is logged, buffered or echoed (requirement 5).

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PcRemote.Core;
using PcRemote.Session;

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

        var sessionId = secure ? GetOwnSessionId() : (int)GetActiveConsoleSessionId();
        // Shared with the service (IpcEndpoints.SessionPipe) so the normal and
        // secure helpers can never be confused for one another.
        var pipeName = IpcEndpoints.SessionPipe(sessionId, secure);

        Console.WriteLine($"[session] helper starting (secure={secure}, session={sessionId}, pipe={pipeName})");

        // Secure mode: attach the pipe-handler threads to the Winlogon desktop
        // the moment it becomes the active input desktop. Normal mode injects
        // on the default desktop of this process's own session.
        var injector = new DesktopInjector(secure);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var server = new SessionPipeServer(pipeName, injector, secure);
        var serverTask = server.RunAsync(cts.Token);

        if (secure)
        {
            // The service sits in session 0 and cannot observe which desktop
            // receives input; this helper lives in the console session and
            // reports Winlogon transitions (UAC/lock/logon) back to it.
            // Reports carry only the desktop name — never keystrokes.
            var reporter = Task.Run(async () =>
            {
                string? lastReported = null;
                while (!cts.IsCancellationRequested)
                {
                    var name = QueryActiveInputDesktopName();
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
        Console.WriteLine("[session] helper stopping");
        return 0;
    }

    private static uint GetActiveConsoleSessionId() => Kernel32.WTSGetActiveConsoleSessionIdSafe();

    /// <summary>Name of the desktop currently receiving input in this session.</summary>
    internal static string? QueryActiveInputDesktopName()
    {
        const uint DESKTOP_READOBJECTS = 0x0001;
        const uint UOI_NAME = 2;
        var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(256);
            if (!GetUserObjectInformation(h, UOI_NAME, sb, (uint)sb.Capacity, out _)) return null;
            return sb.ToString();
        }
        finally
        {
            CloseDesktop(h);
        }
    }

    private static int GetOwnSessionId()
    {
        // The helper always runs inside the console session (the service
        // launches it there), so the active console id is its own session.
        var id = Kernel32.WTSGetActiveConsoleSessionIdSafe();
        return id == 0 ? 1 : (int)id;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool CloseDesktop(IntPtr desktop);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern bool GetUserObjectInformation(IntPtr hObj, uint index,
        System.Text.StringBuilder info, uint nMax, out uint length);
}

internal static class Kernel32
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    public static uint WTSGetActiveConsoleSessionIdSafe() => WTSGetActiveConsoleSessionId();
}

/// <summary>
/// Executes allowlisted input commands on the correct desktop. In secure mode
/// every execution attempts to attach the calling thread to the Winlogon
/// desktop first (UAC/lock/logon); if the active desktop is the normal one the
/// command is refused so a SYSTEM helper never injects into the user session
/// behind the user's back.
/// </summary>
internal sealed class DesktopInjector
{
    private readonly bool _secure;

    public DesktopInjector(bool secure) => _secure = secure;

    /// <summary>Must only ever return true for the allowlisted types below.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "mouse_move", "mouse_click", "mouse_scroll", "key_press", "text_input", "media_control",
    };

    public bool Execute(string type, RemoteMessage msg)
    {
        if (!Allowed.Contains(type)) return false;

        bool ran = false;
        IntPtr attachedDesktop = IntPtr.Zero;
        if (_secure)
        {
            // Attach this thread to the active input desktop if that is
            // Winlogon; refuse to act otherwise (a SYSTEM helper must never
            // inject into the user session behind the user's back).
            attachedDesktop = TryAttachToActiveDesktop();
            if (attachedDesktop == IntPtr.Zero) return false;
        }

        try
        {
            switch (type)
            {
                case "mouse_move":
                    ran = InputInjector.MoveMouseRelative(
                        Math.Clamp(msg.Dx ?? 0, -4096, 4096),
                        Math.Clamp(msg.Dy ?? 0, -4096, 4096));
                    break;
                case "mouse_click":
                    var btn = (msg.Button ?? "left").ToLowerInvariant();
                    var act = (msg.Action ?? "click").ToLowerInvariant();
                    if (btn is not ("left" or "right" or "middle") || act is not ("click" or "down" or "up"))
                        return false;
                    ran = InputInjector.MouseClick(btn, act);
                    break;
                case "mouse_scroll":
                    ran = InputInjector.Scroll(Math.Clamp(msg.Dy ?? 0, -1200, 1200));
                    break;
                case "key_press":
                    if (string.IsNullOrEmpty(msg.Key)) return false;
                    ran = InputInjector.SendKey(msg.Key, msg.Modifiers ?? []);
                    break;
                case "text_input":
                    var text = msg.Text ?? "";
                    if (text.Length > 1000) text = text[..1000];
                    ran = InputInjector.TypeText(text);
                    break;
                case "media_control":
                    var mediaAct = (msg.Action ?? "").ToLowerInvariant();
                    if (mediaAct is not ("play_pause" or "next" or "prev" or "vol_up" or "vol_down" or "mute"))
                        return false;
                    ran = InputInjector.MediaControl(mediaAct);
                    break;
            }
        }
        finally
        {
            // The thread-desktop association persists, but the handle must be
            // released; keystroke text was executed and is never retained.
            if (attachedDesktop != IntPtr.Zero) CloseDesktop(attachedDesktop);
            GC.KeepAlive(ran);
        }
        return ran;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, uint index,
        System.Text.StringBuilder info, uint nMax, out uint length);

    private static IntPtr TryAttachToActiveDesktop()
    {
        const uint DESKTOP_READOBJECTS = 0x0001;
        const uint UOI_NAME = 2;
        var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return IntPtr.Zero;

        var sb = new System.Text.StringBuilder(256);
        if (!GetUserObjectInformation(h, UOI_NAME, sb, (uint)sb.Capacity, out _) ||
            !string.Equals(sb.ToString(), "Winlogon", StringComparison.OrdinalIgnoreCase))
        {
            CloseDesktop(h);
            return IntPtr.Zero;
        }
        if (!SetThreadDesktop(h))
        {
            CloseDesktop(h);
            return IntPtr.Zero;
        }
        return h; // caller keeps it open for the thread's lifetime (released at exit)
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

    public SessionPipeServer(string pipeName, DesktopInjector injector, bool secure)
    {
        _pipeName = pipeName;
        _injector = injector;
        _secure = secure;
    }

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
                Console.WriteLine($"[session] pipe creation failed: {ex.Message}");
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
                Console.WriteLine("[session] client connected");
                var handled = current;
                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(handled); }
                    catch (Exception ex) { Console.WriteLine($"[session] handler error: {ex.Message}"); }
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
                Console.WriteLine($"[session] connection error: {ex.Message}");
                try { current.Dispose(); } catch { }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(server, lenBuf))
        {
            Console.WriteLine("[session] client connected but sent nothing (abandoned?)");
            return;
        }
        var len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > (1 << 20)) return;
        var body = new byte[len];
        if (!await ReadExactAsync(server, body)) return;
        Console.WriteLine($"[session] request received ({len} bytes)");

        IpcMessage? request;
        try { request = JsonSerializer.Deserialize<IpcMessage>(Encoding.UTF8.GetString(body)); }
        catch { return; }
        if (request is null) return;

        var ok = false;
        // Role guard: this process only ever serves the role it was launched
        // for. The pipe name already encodes it, but the request is verified
        // too so a future rename/caller bug cannot make the SYSTEM/Winlogon
        // helper execute normal-desktop input (or the reverse).
        var expectedRole = _secure ? "secure" : "session";
        if (!string.Equals(request.Role, expectedRole, StringComparison.Ordinal))
        {
            Console.WriteLine($"[session] refused {request.Type}: role '{request.Role}' != '{expectedRole}'");
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

        var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new IpcMessage { Type = request.Type, Ok = ok }));
        var replyLen = BitConverter.GetBytes(reply.Length);
        await server.WriteAsync(replyLen);
        await server.WriteAsync(reply);
        await server.FlushAsync();
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
