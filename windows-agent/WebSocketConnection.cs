// PC Remote - minimal WebSocket server over a Stream (RFC 6455)
//
// The agent serves WSS itself instead of using HttpListener, because
// HttpListener needs an admin `netsh http add sslcert` binding for HTTPS —
// unacceptable for a personal LAN tool. This class handles the HTTP upgrade
// handshake and the WebSocket framing (masked reads from the client,
// unmasked writes to the client) over the caller-supplied SslStream.
//
// Scope: single-connection, no fragmentation / no extensions / no
// compression — every app message is one final text frame, which is all the
// protocol in docs/07-API-SPECIFICATION.md §1 requires.

using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

namespace PcRemoteAgent;

public class WebSocketConnection
{
    public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly Stream _stream;

    private WebSocketConnection(Stream stream, bool isSecure)
    {
        _stream = stream;
    }

    /// <summary>
    /// Performs the WebSocket (or WebSocket-over-TLS) handshake on a fresh
    /// connection. Returns null if the client did not send a valid upgrade
    /// request. [isSecure] selects ws:// vs wss:// test paths.
    /// </summary>
    public static async Task<WebSocketConnection?> AcceptAsync(
        Stream rawStream,
        System.Security.Cryptography.X509Certificates.X509Certificate2 serverCertificate,
        bool isSecure)
    {
        var stream = isSecure
            ? new SslStream(rawStream, leaveInnerStreamOpen: false)
            : rawStream;

        if (stream is SslStream ssl)
        {
            // The app pins this certificate via its own trust manager; we
            // accept any client trust here because the connection is
            // authenticated by the pairing handshake after the upgrade.
            await ssl.AuthenticateAsServerAsync(serverCertificate);
        }

        var headers = await ReadUpgradeRequestAsync(stream);
        if (headers is null || !headers.TryGetValue("sec-websocket-key", out var key))
            return null;

        await SendUpgradeResponseAsync(stream, key);
        return new WebSocketConnection(stream, isSecure);
    }

    private static async Task<Dictionary<string, string>?> ReadUpgradeRequestAsync(Stream stream)
    {
        // The request line + headers terminate in \r\n\r\n; cap at 8 KiB.
        var buffer = new byte[8192];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (n == 0) return null;
            offset += n;
            if (Array.IndexOf(buffer, (byte)'\n', 0, offset) >= 0 &&
                HasHeaderTerminator(buffer, offset)) break;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, offset);
        if (!text.StartsWith("GET ", StringComparison.Ordinal)) return null;
        if (!text.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase)) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    private static bool HasHeaderTerminator(byte[] buffer, int length) =>
        // Searching backwards is enough: the terminator is the last 4 bytes
        // when present (the request has no body at this layer).
        length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n'
            && buffer[length - 2] == '\r' && buffer[length - 1] == '\n';

    private static async Task SendUpgradeResponseAsync(Stream stream, string secWebSocketKey)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(secWebSocketKey + AcceptGuid)));
        var response = "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        await stream.FlushAsync();
    }

    /// <summary>Reads one WebSocket message (text). Returns null on close/failure.</summary>
    public async Task<string?> ReceiveTextAsync()
    {
        while (true)
        {
            var frame = await ReadFrameAsync();
            if (frame is null) return null; // connection error or close
            var (opcode, payload) = frame.Value;

            switch (opcode)
            {
                case 0x1: // text — every app message is a single final frame
                    return Encoding.UTF8.GetString(payload);
                case 0x8: // close
                    await SendFrameAsync(0x8, Array.Empty<byte>());
                    return null;
                case 0x9: // ping -> pong
                    await SendFrameAsync(0xA, payload);
                    break;
                case 0xA: // pong — ignore
                    break;
                default:
                    return null; // binary or unknown: unsupported by this app
            }
        }
    }

    private async Task<(byte opcode, byte[] payload)?> ReadFrameAsync()
    {
        var header = new byte[2];
        if (await ReadExactAsync(header, 2) < 2) return null;

        byte opcode = (byte)(header[0] & 0x0F);
        bool masked = (header[1] & 0x80) != 0;
        ulong length = (ulong)(header[1] & 0x7F);

        if (length == 126)
        {
            var ext = new byte[2];
            if (await ReadExactAsync(ext, 2) < 2) return null;
            length = (ulong)((ext[0] << 8) | ext[1]);
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (await ReadExactAsync(ext, 8) < 8) return null;
            length = BitConverter.ToUInt64(ext.Reverse().ToArray());
        }

        if (length > 4 * 1024 * 1024) return null; // sanity cap

        byte[] maskKey = [];
        if (masked)
        {
            maskKey = new byte[4];
            if (await ReadExactAsync(maskKey, 4) < 4) return null;
        }

        var payload = new byte[(int)length];
        if (await ReadExactAsync(payload, (int)length) < (int)length) return null;

        if (masked)
            for (var i = 0; i < payload.Length; i++)
                payload[i] ^= maskKey[i % 4];

        return (opcode, payload);
    }

    private async Task<int> ReadExactAsync(byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    /// <summary>Sends a text message (single unmasked final frame).</summary>
    public Task SendTextAsync(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        return SendFrameAsync(0x1, payload);
    }

    private async Task SendFrameAsync(byte opcode, byte[] payload)
    {
        // Server frames are unmasked; payloads here are always < 126 bytes
        // after the JSON envelope, but keep the length dealing general anyway.
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | opcode));
        if (payload.Length < 126)
        {
            ms.WriteByte((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            ms.WriteByte(126);
            ms.WriteByte((byte)(payload.Length >> 8));
            ms.WriteByte((byte)payload.Length);
        }
        else
        {
            ms.WriteByte(127);
            var lenBytes = BitConverter.GetBytes((ulong)payload.Length).Reverse().ToArray();
            ms.Write(lenBytes, 0, 8);
        }
        ms.Write(payload, 0, payload.Length);

        await _stream.WriteAsync(ms.ToArray());
        await _stream.FlushAsync();
    }

    public void Dispose() => _stream.Dispose();
}