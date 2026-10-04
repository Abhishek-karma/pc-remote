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

    // --- v1 streaming fields (additive; ignored by older clients) ---

    /// <summary>Media channel state (server → client on stream_start/stop): "active",
    /// "stopped", or "error".</summary>
    [JsonPropertyName("streamState")] public string? StreamState { get; set; }

    /// <summary>Negotiated capture dimensions (stream_state) in physical pixels.</summary>
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }

    /// <summary>Absolute coordinates (remote-desktop view): pixel position in the
    /// captured desktop's coordinate space, for mouse_move_abs.</summary>
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
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
        "mouse_move_abs",  // absolute desktop coordinates (remote-desktop view)
        "mouse_click",
        "mouse_scroll",
        "key_press",
        "text_input",
        "media_control",
        "system_power",
        "sas",             // secure attention sequence (Ctrl+Alt+Del) request
        "session_status",  // ask for current session/desktop state
        "release_all",    // drop held buttons/modifiers (also sent on disconnect)
        "disconnect",
        // Media channel lifecycle (sent on the /stream socket after auth):
        "stream_start",     // request the agent to begin capture+encode and push frames
        "stream_stop",      // stop the stream and release the agent encoder
        "keyframe_request", // ask the encoder for an IDR (join, loss, decoder reset)
    };

    /// <summary>Commands that may only run when a paired client asks — no extra
    /// caller privilege beyond the WSS authentication itself.</summary>
    public static bool IsAllowed(string type) => ClientCommands.Contains(type);
}
