// JSON remote message data model shared between the Android client and the
// PC Remote service. Wire format is versioned: unknown fields are ignored by
// the Android client; the server rejects messages with an unsupported
// version. Additive fields only — never repurpose or rename existing ones.

using System.Text.Json.Serialization;

namespace PcRemote.Core;

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
    [JsonPropertyName("connKey")] public string? ConnKey { get; set; }

    // --- v1 additive fields (ignored by older clients) ---

    /// <summary>Stable PC identity GUID (auth_ok, mDNS TXT "pcid"). Never an IP address:
    /// Android keys its saved token/pin by this value so DHCP changes do not force re-pairing.</summary>
    [JsonPropertyName("pcId")] public string? PcId { get; set; }

    /// <summary>Desktop/session state snapshot (auth_ok and session_status): "normal",
    /// "locked", "secure_desktop" (UAC), or "logon". Lets the client render context-aware UI.</summary>
    [JsonPropertyName("sessionState")] public string? SessionState { get; set; }

    /// <summary>Reserved for the media channel handshake (Phase 5): requested protocol
    /// ("h264" | "jpeg"), port and capability flags. Frames never travel on this JSON channel.</summary>
    [JsonPropertyName("stream")] public StreamOffer? Stream { get; set; }
}

public class StreamOffer
{
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("maxFps")] public int MaxFps { get; set; }
}

/// <summary>
/// Static allowlist of message types the service accepts from a paired client,
/// with per-command field validation. Everything not listed here is answered
/// with "unknown_type" — the service is never an arbitrary command executor.
/// </summary>
public static class CommandAllowlist
{
    public static readonly IReadOnlySet<string> ClientCommands = new HashSet<string>
    {
        "auth",            // handled before this set is consulted; listed for documentation
        "mouse_move",
        "mouse_click",
        "mouse_scroll",
        "key_press",
        "text_input",
        "media_control",
        "system_power",
        "sas",             // secure attention sequence (Ctrl+Alt+Del) request
        "session_status",  // ask for current session/desktop state
        "stream_request",  // Phase 5 media channel negotiation (accepted, reserved)
        "disconnect",
    };

    /// <summary>Commands that may only run when a paired client asks — no extra
    /// caller privilege beyond the WSS authentication itself.</summary>
    public static bool IsAllowed(string type) => ClientCommands.Contains(type);
}
