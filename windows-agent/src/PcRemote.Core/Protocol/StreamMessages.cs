// Payload contracts for the streaming control messages that travel the session
// control pipe between the service (client) and the user-session agent (server):
//   stream_start    request  { fps, bitrate }
//   stream_start    reply    { path, width, height, fps }
//   stream_stop     request  (no payload)
//   stream_keyframe request  (no payload)
//   stream_fps      request  { fps }
// JSON names are wire format — never rename without a protocol version bump.

using System.Text.Json.Serialization;

namespace PcRemote.Core;

public sealed class StreamStartRequest
{
    [JsonPropertyName("fps")] public int Fps { get; set; } = 15;
    [JsonPropertyName("bitrate")] public int Bitrate { get; set; } = 4_000_000;
}

public sealed class StreamFpsRequest
{
    [JsonPropertyName("fps")] public int Fps { get; set; } = 15;
}

public sealed class StreamBindingInfo
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("fps")] public int Fps { get; set; }
}
