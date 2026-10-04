// Service-side IPC coordinator: routes tray/session requests with privilege
// checks. Privileged operations are refused to unelevated callers — see
// IpcSecurity.GetCallerPrivilege.

using System.Text.Json;
using PcRemote.Core;

namespace PcRemote.Service;

public sealed class IpcCoordinator
{
    private readonly PairingStore _pairing;
    private readonly SessionManager _sessionManager;

    public ControlChannel? Channel { get; set; }

    public IpcCoordinator(PairingStore pairing, SessionManager sessionManager)
    {
        _pairing = pairing;
        _sessionManager = sessionManager;
    }

    public IpcMessage HandleIpc(IpcMessage request, IpcPrivilege privilege)
    {
        switch (request.Type)
        {
            case "status":
                return new IpcMessage
                {
                    Type = "status",
                    Ok = true,
                    ServiceState = "running",
                    // The pairing code is trust material: anyone who reads it can
                    // pair a device. The pipe ACL admits all local Users, so it
                    // is only returned to an elevated (admin/SYSTEM) caller —
                    // the unelevated tray shows "run as administrator to pair".
                    PairingCode = privilege == IpcPrivilege.Elevated ? _pairing.CurrentCode : null,
                    ConnectedDevices = Channel?.ConnectedCount ?? 0,
                    SessionState = _sessionManager.CurrentState.ToString().ToLowerInvariant(),
                    // Streaming diagnostics: how many media clients are attached.
                    Streaming = (Channel?.StreamingCount ?? 0) > 0,
                    StreamClients = Channel?.StreamingCount ?? 0,
                };

            case "generate_pairing_code":
            {
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                var code = _pairing.GeneratePairingCode();
                return new IpcMessage { Type = request.Type, Ok = true, PairingCode = code };
            }

            case "revoke_all_devices":
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                _pairing.ClearAllTokens();
                return new IpcMessage { Type = request.Type, Ok = true };

            case "migrate_tokens":
                // Writes trust material (the trusted-device set). The pipe ACL
                // admits every local user, so an unguarded handler here would
                // let any unprivileged local process inject a token and then
                // authenticate over WSS as a paired device. Elevated only.
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                // Legacy tray reads its CurrentUser-DPAPI file and hands the
                // token list over. One-way; the service re-encrypts under
                // LocalMachine scope. No tokens are returned or logged.
                if (request.Payload is not { ValueKind: JsonValueKind.Array } arr)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "bad_payload" };
                var tokens = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "")
                    .Where(s => s.Length > 0).ToList();
                _pairing.ImportLegacyTokens(tokens);
                return new IpcMessage { Type = request.Type, Ok = true, ConnectedDevices = tokens.Count };

            case "desktop_report":
                // Only the secure-input helper (SYSTEM) reports desktop state.
                if (privilege != IpcPrivilege.Elevated)
                    return new IpcMessage { Type = request.Type, Ok = false, Error = "elevation_required" };
                _sessionManager.ReportDesktopState(request.SessionState ?? "unknown");
                return new IpcMessage { Type = request.Type, Ok = true };

            default:
                return new IpcMessage { Type = request.Type, Ok = false, Error = "unknown_type" };
        }
    }
}
