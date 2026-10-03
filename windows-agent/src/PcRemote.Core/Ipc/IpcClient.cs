// Authenticated local IPC over named pipes.
//
// Boundary rules (requirement 11):
//   * The service exposes \\.\pipe\PCRemoteCtl. Tray and session helpers
//     connect as clients; there is no unrestricted local TCP surface.
//   * The pipe ACL grants access to SYSTEM, Administrators and the
//     interactive Users group. Privileged operations (secure-desktop input,
//     SAS, power, update apply, token revocation) are additionally gated on
//     the *caller's* token: the service impersonates the pipe client and
//     refuses elevated-only requests from unelevated processes.
//
// IMPORTANT: the client must NOT set PipeOptions.CurrentUserOnly. Every
// IPC peer is cross-user by design (tray/user-process -> SYSTEM service and
// SYSTEM service -> user-owned session helper) and CurrentUserOnly forbids
// connecting to a server created by a different user (it also compares
// elevation level). Authorization is the pipe ACL + privilege gate below.
//   * Every message is a JSON IpcMessage with a type from a fixed allowlist.
//     Payloads never include pairing codes or tokens except in
//     server-to-tray status replies the tray already displays.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcRemote.Core;

public class IpcMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("role")] public string? Role { get; set; }          // client -> server hello: "tray" | "session" | "secure"
    [JsonPropertyName("sessionId")] public int? SessionId { get; set; }   // session helpers announce their WTS session
    [JsonPropertyName("pairingCode")] public string? PairingCode { get; set; }
    [JsonPropertyName("connectedDevices")] public int? ConnectedDevices { get; set; }
    [JsonPropertyName("serviceState")] public string? ServiceState { get; set; }   // "running" | "starting" | "error"
    [JsonPropertyName("sessionState")] public string? SessionState { get; set; }   // normal | locked | secure_desktop | logon
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
// Input request payload (service -> session helper). The command string is
// re-validated by the helper against the same allowlist before execution.
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }
}

public static class IpcEndpoints
{
    public const string ControlPipe = "PCRemoteCtl";

    /// <summary>
    /// Pipe name for a session input helper. The secure (Winlogon) helper and
    /// the normal-desktop helper both run in the same console session, so the
    /// mode MUST be part of the name — otherwise both processes create
    /// instances of the same pipe and a command can be served by the wrong
    /// helper (a UAC keystroke into the user desktop, or vice versa).
    /// Both the service and the helper derive the name from here.
    /// </summary>
    public static string SessionPipe(int sessionId, bool secure) =>
        $"PCRemoteSessionCtl-{sessionId}-{(secure ? "secure" : "session")}";
}

/// <summary>Privilege levels the service can assign to an IPC caller.</summary>
public enum IpcPrivilege
{
/// <summary>Unelevated user process: status queries and pairing-code refresh only.</summary>
    Standard,
/// <summary>Elevated (admin token) or SYSTEM caller: full control surface.</summary>
    Elevated,
}

public static class IpcSecurity
{
/// <summary>Pipe ACL: SYSTEM + Administrators full control, interactive
/// Users read/write. Anonymous and guest access is explicitly denied.</summary>
    public static PipeSecurity CreatePipeSecurity()
    {
        var ps = new PipeSecurity();
        var sidSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var sidAdmins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sidUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        ps.AddAccessRule(new PipeAccessRule(sidSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(sidAdmins, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(sidUsers,
            PipeAccessRights.Read | PipeAccessRights.Write, AccessControlType.Allow));
        // Creating additional instances of an existing pipe requires
        // FILE_CREATE_PIPE_INSTANCE. Grant it to the creating account
        // explicitly — CreatorOwner does not reliably match the creating
        // token at access-check time (verified on Win11 25H2).
        ps.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

/// <summary>
    /// Classifies the caller of a connected pipe client. The identity is taken
    /// from the pipe itself by impersonating the client for the duration of the
    /// check, so it cannot be spoofed by PID reuse between a PID lookup and the
    /// token read. Returns Elevated for SYSTEM or admin-token processes;
    /// Standard otherwise. Never throws - an unresolvable caller is treated
    /// as Standard (least privilege).
    /// </summary>
    public static IpcPrivilege GetCallerPrivilege(NamedPipeServerStream server)
    {
        try
        {
            if (!server.IsConnected) return IpcPrivilege.Standard;

            var privilege = IpcPrivilege.Standard;
            // RunAsClient impersonates the connected client on this thread only
            // and reverts automatically, so WindowsIdentity.GetCurrent() below is
            // the *client's' token, not ours.
            server.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.IsSystem) { privilege = IpcPrivilege.Elevated; return; }
                var principal = new WindowsPrincipal(identity);
                privilege = principal.IsInRole(WindowsBuiltInRole.Administrator)
                    ? IpcPrivilege.Elevated
                    : IpcPrivilege.Standard;
            });
            return privilege;
        }
        catch
        {
            // Impersonation unsupported/failed - least privilege.
            return IpcPrivilege.Standard;
        }
    }
}

/// <summary>Framed JSON client for the service control pipe (tray + session helpers).</summary>
public sealed class IpcClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly string _serverName;
    private readonly string _pipeName;

    public IpcClient(string pipeName = IpcEndpoints.ControlPipe, string serverName = ".")
    {
        _pipeName = pipeName;
        _serverName = serverName;
    }

    public bool IsConnected => _pipe is { IsConnected: true };

    public void Connect(int timeoutMs = 5000)
    {
        // Idempotent: RoundTrip callers that pre-connect (for a distinct
        // connect timeout) must not open a SECOND connection here — the first
        // stream would leak as a live, never-written zombie that occupies a
        // server pipe instance forever.
        if (_pipe is { IsConnected: true }) return;
        // NOTE: deliberately no PipeOptions.CurrentUserOnly here. Every IPC
        // peer is cross-user (tray/user-process -> SYSTEM service, and SYSTEM
        // service -> user-owned session helper) and CurrentUserOnly refuses to
        // connect to a server created by a different user/elevation level.
        // Authorization is the server-side pipe ACL plus IpcPrivilege.
        _pipe = new NamedPipeClientStream(_serverName, _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        _pipe.Connect(timeoutMs);
    }

/// <summary>Sends one message and waits for the reply. Each request uses a
/// fresh connection — IPC traffic is tiny and this keeps the server loop
/// one-request-per-client, matching NamedPipeServerStream semantics.
/// timeoutMs bounds the WHOLE exchange (connect + write + reply read): a
/// reply wait with no deadline would wedge the client — and, on a sequential
/// server, the server slot with it.</summary>
    public IpcMessage? RoundTrip(IpcMessage request, int timeoutMs = 5000)
    {
        Connect(timeoutMs);
        using var cts = new CancellationTokenSource(Math.Max(timeoutMs, 1000));
        var json = JsonSerializer.Serialize(request);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var len = BitConverter.GetBytes(bytes.Length);
        _pipe!.Write(len);
        _pipe.Write(bytes);
        _pipe.Flush();

        var lenBuf = new byte[4];
        ReadExact(_pipe, lenBuf, cts.Token);
        var replyLen = BitConverter.ToInt32(lenBuf);
        if (replyLen <= 0 || replyLen > 1 << 20) throw new IOException("IPC reply framing error");
        var replyBuf = new byte[replyLen];
        ReadExact(_pipe, replyBuf, cts.Token);
        return JsonSerializer.Deserialize<IpcMessage>(System.Text.Encoding.UTF8.GetString(replyBuf));
    }

    private static void ReadExact(PipeStream pipe, byte[] buffer, CancellationToken ct)
    {
        // ReadAsync (not the sync overload) so the deadline really cancels a
        // wedged reply wait — PipeStream honors the token on async reads only.
        var read = 0;
        while (read < buffer.Length)
        {
            var n = pipe.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct)
                        .AsTask().GetAwaiter().GetResult();
            if (n == 0) throw new IOException("IPC pipe closed");
            read += n;
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
    }
}
