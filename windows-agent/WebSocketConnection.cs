// Lightweight WebSocket server implementation over a Stream (RFC 6455).

using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PcRemoteAgent;

public class WebSocketConnection : IDisposable
{
    public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    public const int MaxPayloadSize = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private WebSocketConnection(Stream stream)
    {
        _stream = stream;
    }

    public static async Task<WebSocketConnection?> AcceptAsync(
        Stream rawStream,
        X509Certificate2 serverCertificate,
        bool isSecure)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var stream = isSecure
                ? new SslStream(rawStream, leaveInnerStreamOpen: false)
                : rawStream;

            if (stream is SslStream ssl)
            {
                await ssl.AuthenticateAsServerAsync(serverCertificate).WaitAsync(cts.Token);
            }

            var headers = await ReadUpgradeRequestAsync(stream, cts.Token);
            if (headers is null ||
                !headers.TryGetValue("sec-websocket-key", out var key) ||
                string.IsNullOrWhiteSpace(key) ||
                !headers.TryGetValue("sec-websocket-version", out var ver) ||
                ver != "13")
            {
                return null;
            }

            await SendUpgradeResponseAsync(stream, key, cts.Token);
            return new WebSocketConnection(stream);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, string>?> ReadUpgradeRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
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
        length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n'
            && buffer[length - 2] == '\r' && buffer[length - 1] == '\n';

    private static async Task SendUpgradeResponseAsync(Stream stream, string secWebSocketKey, CancellationToken ct)
    {
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(secWebSocketKey + AcceptGuid)));
        var response = $"HTTP/1.1 101 Switching Protocols\r\n" +
                       $"Upgrade: websocket\r\n" +
                       $"Connection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        await stream.FlushAsync(ct);
    }

    public async Task<string?> ReceiveTextAsync()
    {
        while (true)
        {
            var frame = await ReadFrameAsync();
            if (frame is null) return null;
            var (opcode, payload) = frame.Value;

            switch (opcode)
            {
                case 0x1:
                    try
                    {
                        return StrictUtf8.GetString(payload);
                    }
                    catch (DecoderFallbackException)
                    {
                        Console.WriteLine("[!] Received invalid UTF-8 payload in text frame");
                        return null;
                    }
                case 0x8:
                    await SendFrameAsync(0x8, []);
                    return null;
                case 0x9:
                    await SendFrameAsync(0xA, payload);
                    break;
                case 0xA:
                    break;
                default:
                    return null;
            }
        }
    }

    private async Task<(byte opcode, byte[] payload)?> ReadFrameAsync()
    {
        var header = new byte[2];
        if (await ReadExactAsync(header, 2) < 2) return null;

        byte finAndRsv = header[0];
        bool fin = (finAndRsv & 0x80) != 0;
        int rsv = finAndRsv & 0x70;
        if (rsv != 0)
        {
            Console.WriteLine("[!] RFC 6455 violation: RSV bits must be 0");
            return null;
        }

        byte opcode = (byte)(finAndRsv & 0x0F);
        if (opcode is not (0x1 or 0x8 or 0x9 or 0xA))
        {
            Console.WriteLine($"[!] RFC 6455 violation: Unsupported opcode 0x{opcode:X}");
            return null;
        }

        bool isControlFrame = opcode >= 0x8;
        if (isControlFrame)
        {
            if (!fin)
            {
                Console.WriteLine("[!] RFC 6455 violation: Control frame must not be fragmented");
                return null;
            }
        }
        else if (!fin)
        {
            Console.WriteLine("[!] RFC 6455 violation: Fragmented data frames are not supported");
            return null;
        }

        bool masked = (header[1] & 0x80) != 0;
        if (!masked)
        {
            Console.WriteLine("[!] RFC 6455 violation: Client sent unmasked frame");
            return null;
        }

        ulong length = (ulong)(header[1] & 0x7F);
        if (isControlFrame && length > 125)
        {
            Console.WriteLine($"[!] RFC 6455 violation: Control frame payload length ({length}) > 125");
            return null;
        }

        if (length == 126)
        {
            var ext = new byte[2];
            if (await ReadExactAsync(ext, 2) < 2) return null;
            length = BinaryPrimitives.ReadUInt16BigEndian(ext);
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (await ReadExactAsync(ext, 8) < 8) return null;
            length = BinaryPrimitives.ReadUInt64BigEndian(ext);
            if (length > long.MaxValue) return null;
        }

        if (length > MaxPayloadSize)
        {
            Console.WriteLine($"[!] WebSocket frame payload length ({length}) exceeds maximum limit ({MaxPayloadSize})");
            return null;
        }

        var maskKey = new byte[4];
        if (await ReadExactAsync(maskKey, 4) < 4) return null;

        var payload = new byte[(int)length];
        if (await ReadExactAsync(payload, (int)length) < (int)length) return null;

        for (var i = 0; i < payload.Length; i++)
            payload[i] ^= maskKey[i % 4];

        if (opcode == 0x8)
        {
            if (payload.Length == 1) return null;
            if (payload.Length >= 2)
            {
                ushort code = BinaryPrimitives.ReadUInt16BigEndian(payload);
                if (code is < 1000 or 1004 or 1005 or 1006 or 1015 or > 4999) return null;
            }
        }

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

    public Task SendTextAsync(string text) =>
        SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text));

    private async Task SendFrameAsync(byte opcode, byte[] payload)
    {
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
            var lenBytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(lenBytes, (ulong)payload.Length);
            ms.Write(lenBytes);
        }
        ms.Write(payload);

        await _sendLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(ms.ToArray());
            await _stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _sendLock.Dispose();
        _stream.Dispose();
    }
}
