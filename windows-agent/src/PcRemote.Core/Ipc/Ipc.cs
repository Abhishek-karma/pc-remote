// Local IPC between the service and the tray, over one named pipe.
//
// Three requests exist: status, a fresh pairing code, revoke every device. That
// is the entire local control surface, and it is all the tray needs.
//
// The pipe ACL admits local Users (so an unelevated tray can read status) but
// every privileged operation additionally checks the CALLER's token: the service
// impersonates the pipe client and refuses pairing codes and revocation to
// unelevated processes. The pairing code is trust material - anyone holding it
// can pair a phone.
//
// PipeOptions.CurrentUserOnly must NOT be used: every peer here is cross-user by
// design (user tray -> SYSTEM service). Authorization is the ACL plus the
// privilege check, not the pipe's user scoping.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcRemote.Core;

public sealed class IpcMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    // Status reply.
    [JsonPropertyName("pairingCode")] public string? PairingCode { get; set; }
    [JsonPropertyName("connectedDevices")] public int ConnectedDevices { get; set; }
    [JsonPropertyName("sessionState")] public string? SessionState { get; set; }
}

public static class IpcEndpoints
{
    public const string ControlPipe = "PCRemoteCtl";
}

/// <summary>True when the caller is SYSTEM or an administrator.</summary>
public static class IpcSecurity
{
    /// <summary>Pipe ACL: SYSTEM and Administrators full control, interactive
    /// Users read/write so the unelevated tray can ask for status. Guests and
    /// anonymous are excluded.</summary>
    public static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        void Allow(WellKnownSidType sid) => security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        Allow(WellKnownSidType.LocalSystemSid);
        Allow(WellKnownSidType.BuiltinAdministratorsSid);
        Allow(WellKnownSidType.InteractiveSid);
        return security;
    }

    /// <summary>Elevated callers get the privileged operations. Any failure to
    /// determine the caller is treated as NOT elevated.</summary>
    public static bool IsElevatedCaller(NamedPipeServerStream pipe)
    {
        try
        {
            WindowsIdentity? identity = null;
            pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent());
            if (identity is null) return false;
            if (identity.IsSystem) return true;
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not determine caller privilege: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Named-pipe server. One connection is one request/reply exchange.</summary>
public sealed class IpcServer : IAsyncDisposable
{
    private const int MaxFrameBytes = 64 * 1024;

    private readonly string _pipeName;
    private readonly Func<IpcMessage, IpcMessage> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public IpcServer(string pipeName, Func<IpcMessage, IpcMessage> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public void Start() => _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 4096, outBufferSize: 4096, IpcSecurity.CreatePipeSecurity());
            }
            catch (Exception ex)
            {
                Log.Error($"tray pipe creation failed: {ex.Message}; retrying in 5 s");
                try { server?.Dispose(); } catch { /* already broken */ }
                await Task.Delay(5000, ct);
                continue;
            }

            await server.WaitForConnectionAsync(ct);
            var current = server;
            // Handled off so a wedged client can never occupy the accept slot.
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(current, ct); }
                catch (Exception ex) { Log.Warn($"tray pipe error: {ex.Message}"); }
                finally { try { current.Dispose(); } catch { /* nothing to do */ } }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactlyAsync(server, lengthBytes, ct)) return;
        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > MaxFrameBytes) return;

        var body = new byte[length];
        if (!await ReadExactlyAsync(server, body, ct)) return;

        IpcMessage? request;
        try { request = JsonSerializer.Deserialize<IpcMessage>(Encoding.UTF8.GetString(body)); }
        catch (JsonException) { return; }
        if (request is null) return;

        var elevated = IpcSecurity.IsElevatedCaller(server);
        IpcMessage reply;
        try
        {
            reply = _handler(request);
            // Privilege is enforced here rather than inside each handler so no
            // future handler can forget the check.
            if (!elevated && request.Type is not "status")
            {
                reply = new IpcMessage { Type = request.Type, Error = "elevation_required" };
            }
        }
        catch (Exception ex)
        {
            // The pipe is reachable by every local user, so exception text stays
            // in the service log rather than going back to the caller.
            Log.Error($"tray request '{request.Type}' failed: {ex.Message}");
            reply = new IpcMessage { Type = request.Type, Error = "internal_error" };
        }

        var replyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
        await server.WriteAsync(BitConverter.GetBytes(replyBytes.Length), ct);
        await server.WriteAsync(replyBytes, ct);
        await server.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactlyAsync(PipeStream pipe, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_loop is not null) await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}

/// <summary>Client side of the same pipe, used by the tray.</summary>
public sealed class IpcClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;

    public IpcClient(string pipeName = IpcEndpoints.ControlPipe)
    {
        _pipeName = pipeName;
    }

    /// <summary>Sends one request and returns the reply. Each call uses a fresh
    /// connection; timeoutMs bounds the whole exchange.</summary>
    public IpcMessage? RoundTrip(IpcMessage request, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(timeoutMs);
        _pipe = pipe;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
        pipe.Write(BitConverter.GetBytes(bytes.Length));
        pipe.Write(bytes);
        pipe.Flush();

        var lengthBytes = ReadExactly(pipe, 4, cts.Token);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > 4096) return null;

        return JsonSerializer.Deserialize<IpcMessage>(Encoding.UTF8.GetString(ReadExactly(pipe, length, cts.Token)));
    }

    private static byte[] ReadExactly(PipeStream pipe, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = pipe.ReadAsync(buffer.AsMemory(read, count - read), ct).AsTask().GetAwaiter().GetResult();
            if (n == 0) throw new IOException("service closed the pipe");
            read += n;
        }
        return buffer;
    }

    public void Dispose() => _pipe?.Dispose();
}
