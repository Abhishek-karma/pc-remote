// Multi-instance named-pipe server for the PC Remote service control IPC.
// One connection = one request/reply exchange (clients use IpcClient.RoundTrip).

using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PcRemote.Core;

public sealed class IpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcMessage, IpcPrivilege, IpcMessage> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public IpcServer(string pipeName, Func<IpcMessage, IpcPrivilege, IpcMessage> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public void Start()
    {
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    IpcSecurity.CreatePipeSecurity());
            }
            catch (Exception ex)
            {
                AgentLog.Error($"IPC pipe creation failed: {ex.Message}; retrying in 5 s");
                await Task.Delay(5000, ct);
                continue;
            }

            // Wait for the client HERE (bounding live instances), then hand
            // the connected instance to its own task so a wedged client — a
            // hung reply read with no deadline took the tray IPC down once —
            // never occupies the accept slot. Handlers are stateless, so
            // concurrent instances are safe; each connection is one
            // request/reply exchange.
            await server.WaitForConnectionAsync(ct);
            var current = server;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleConnectionAsync(current, ct);
                }
                catch (Exception ex)
                {
                    AgentLog.Error($"IPC connection error: {ex.Message}");
                }
                finally
                {
                    try { current.Dispose(); } catch { }
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(server, lenBuf, ct)) return;
        var len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > (1 << 20)) return;
        var body = new byte[len];
        if (!await ReadExactAsync(server, body, ct)) return;

        IpcMessage? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcMessage>(Encoding.UTF8.GetString(body));
        }
        catch
        {
            return; // malformed framing: drop silently
        }
        if (request is null) return;

        var privilege = IpcSecurity.GetCallerPrivilege(server);
        IpcMessage reply;
        try
        {
            reply = _handler(request, privilege);
        }
        catch (Exception ex)
        {
            // Do not echo raw exception text back to the caller: the pipe is
            // reachable by every local user, so details stay in the service log.
            AgentLog.Error($"IPC handler for '{request.Type}' failed: {ex.Message}");
            reply = new IpcMessage { Type = request.Type, Ok = false, Error = "internal_error" };
        }

        var replyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
        var replyLen = BitConverter.GetBytes(replyBytes.Length);
        await server.WriteAsync(replyLen, ct);
        await server.WriteAsync(replyBytes, ct);
        await server.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken ct)
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
