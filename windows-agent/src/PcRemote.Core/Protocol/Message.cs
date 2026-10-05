// The PC Remote wire protocol.
//
// One flat JSON object per frame. `type` names a real user action — there is no
// RPC envelope, no request id, no generic command bus. Mouse movement is the
// highest-frequency message and is deliberately fire-and-forget: it never waits
// for an acknowledgement, so a saturated network never backs up the touchpad.
//
// Every field is nullable and every message is validated again in the session
// helper (the injection boundary) before it touches Win32.

using System.Text.Json.Serialization;

namespace PcRemote.Core;

/// <summary>A single protocol frame, in either direction.</summary>
public sealed class Message
{
    /// <summary>Protocol version. A peer that speaks a different major version is
    /// rejected rather than misinterpreted.</summary>
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    [JsonPropertyName("type")] public string Type { get; set; } = "";

    // --- handshake ---

    /// <summary>Handshake only. The phone sends its saved trust token, or the
    /// pairing code when pairing for the first time. On success the service
    /// replies with <see cref="Token"/> set to the token to store.</summary>
    [JsonPropertyName("token")] public string? Token { get; set; }

    [JsonPropertyName("code")] public string? Code { get; set; }

    /// <summary>Handshake reply: the machine name shown in the app.</summary>
    [JsonPropertyName("pcName")] public string? PcName { get; set; }

    /// <summary>Stable machine identity (a GUID). Never an IP address: the phone
    /// keys its saved token and certificate pin by this, so a DHCP lease change
    /// does not force the user to pair again.</summary>
    [JsonPropertyName("pcId")] public string? PcId { get; set; }

    /// <summary>Why the connection was refused, or why it ended. Codes are
    /// mapped to human text by the app; the phone never shows raw internals.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    /// <summary>Desktop the PC is currently showing: "normal", "locked",
    /// "secure" (UAC prompt) or "logon".</summary>
    [JsonPropertyName("state")] public string? State { get; set; }

    // --- input ---

    /// <summary>Relative cursor movement in pixels.</summary>
    [JsonPropertyName("dx")] public int? Dx { get; set; }
    [JsonPropertyName("dy")] public int? Dy { get; set; }

    /// <summary>"left" | "right" | "middle".</summary>
    [JsonPropertyName("button")] public string? Button { get; set; }

    /// <summary>For mouse buttons: "down" | "up". For keys: "down" | "up" |
    /// "press" (press = down then up, and is the default when omitted).</summary>
    [JsonPropertyName("action")] public string? Action { get; set; }

    /// <summary>Scroll wheel notches. Positive scrolls up.</summary>
    [JsonPropertyName("delta")] public int? Delta { get; set; }

    /// <summary>Named key, e.g. "ENTER", "ESC", "LEFT", "CTRL". See KeyMap.</summary>
    [JsonPropertyName("key")] public string? Key { get; set; }

    /// <summary>Literal text to type. Never logged.</summary>
    [JsonPropertyName("text")] public string? Text { get; set; }
}

/// <summary>Every message type the service accepts from a paired phone.
/// Anything else is answered with an error and never reaches the input path.</summary>
public static class Protocol
{
    public const int Version = 1;

    // client -> service
    public const string Hello = "hello";
    public const string Move = "move";
    public const string Button = "button";
    public const string Scroll = "scroll";
    public const string Key = "key";
    public const string Text = "text";
    public const string ReleaseAll = "release_all";
    public const string Disconnect = "disconnect";

    // service -> client
    public const string Welcome = "welcome";
    public const string Error = "error";

    private static readonly HashSet<string> Accepted = new(StringComparer.Ordinal)
    {
        Hello, Move, Button, Scroll, Key, Text, ReleaseAll, Disconnect,
    };

    public static bool IsAccepted(string type) => Accepted.Contains(type);
}
