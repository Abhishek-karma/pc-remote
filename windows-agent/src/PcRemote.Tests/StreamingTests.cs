// Increment A tests: MediaFrame binary framing, binary WebSocket push, and the
// /stream connection classification (auth + stream lifecycle, no command surface).

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PcRemote.Core;
using PcRemote.Service;
using Xunit;

namespace PcRemote.Tests;

public class MediaFrameTests
{
    [Fact]
    public void FrameRoundTrips()
    {
        var payload = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84 }; // fake Annex-B NAL
        var frame = new MediaFrame(IsKeyframe: true, Seq: 7, PtsMilliseconds: 1234, payload);

        var parsed = MediaFrame.Parse(frame.ToBytes());

        Assert.NotNull(parsed);
        var p = parsed.Value;
        Assert.True(p.IsKeyframe);
        Assert.Equal(7u, p.Seq);
        Assert.Equal(1234u, p.PtsMilliseconds);
        Assert.Equal(payload, p.Payload);
    }

    [Fact]
    public void NonKeyframeFlagDoesNotSetKeyframe()
    {
        var frame = new MediaFrame(IsKeyframe: false, Seq: 1, PtsMilliseconds: 0, new byte[] { 1, 2, 3 });
        var parsed = MediaFrame.Parse(frame.ToBytes());
        Assert.NotNull(parsed);
        Assert.False(parsed.Value.IsKeyframe);
    }

    [Theory]
    [InlineData(0)]               // empty
    [InlineData(6)]               // shorter than the 16-byte header
    [InlineData(15)]              // header minus one byte
    public void TruncatedHeaderIsRejected(int take)
    {
        var bytes = new MediaFrame(true, 1, 0, new byte[] { 1, 2, 3 }).ToBytes();
        Assert.Null(MediaFrame.Parse(bytes.Take(take).ToArray()));
    }

    [Fact]
    public void BadMagicIsRejected()
    {
        var bytes = new MediaFrame(true, 1, 0, new byte[] { 1 }).ToBytes();
        bytes[0] = 0x00; // corrupt the first magic byte
        Assert.Null(MediaFrame.Parse(bytes));
    }

    [Fact]
    public void WrongVersionIsRejected()
    {
        var bytes = new MediaFrame(true, 1, 0, new byte[] { 1 }).ToBytes();
        bytes[4] = 99;
        Assert.Null(MediaFrame.Parse(bytes));
    }

    [Fact]
    public void LengthMismatchIsRejected()
    {
        // Two extra bytes beyond the declared payload length.
        var bytes = new MediaFrame(true, 1, 0, new byte[] { 1, 2, 3 }).ToBytes();
        var padded = bytes.Concat(new byte[] { 0xAA, 0xBB }).ToArray();
        Assert.Null(MediaFrame.Parse(padded));
    }
}

public class WebSocketBinaryTests
{
    private static X509Certificate2 MakeTestCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=noop", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = signed.Export(X509ContentType.Pkcs12);
        return new X509Certificate2(pfx, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    [Fact]
    public async Task ServerBinaryPushArrivesAsBinaryFrame()
    {
        var cert = MakeTestCert();
        var payload = new byte[4096];
        RandomNumberGenerator.Fill(payload);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            using var ws = (await WebSocketConnection.AcceptAsync(client.GetStream(), cert, isSecure: true))!;
            await ws.SendBinaryAsync(payload);
            // Stay open until the client closes: disposing right after the push
            // would RST before the peer finished reading the frame.
            _ = await ws.ReceiveTextAsync();
        });

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Binary)
                ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal(payload, ms.ToArray());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await server.WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();
    }
}

/// <summary>
/// The init segment (ftyp+moov, carrying the avcC SPS/PPS) must reach a client that
/// joins a stream already in progress. Without it MediaCodec cannot be configured at
/// all — an IDR does not carry parameter sets — so the symptom is a permanently
/// black screen rather than a visible error.
/// </summary>
public class StreamInitSegmentTests
{
    private static byte[] Box(string type, int payloadLength, byte fill = 0)
    {
        var b = new byte[8 + payloadLength];
        var size = (uint)b.Length;
        b[0] = (byte)(size >> 24); b[1] = (byte)(size >> 16); b[2] = (byte)(size >> 8); b[3] = (byte)size;
        Encoding.ASCII.GetBytes(type, 0, 4, b, 4);
        for (var i = 8; i < b.Length; i++) b[i] = fill;
        return b;
    }

    /// <summary>ftyp + moov + two fragments, as the MF fragmented-MP4 sink emits.</summary>
    private static byte[] BuildFragmentedMp4()
    {
        using var ms = new MemoryStream();
        ms.Write(Box("ftyp", 16));
        ms.Write(Box("moov", 64));
        ms.Write(Box("moof", 32));
        ms.Write(Box("mdat", 128, 0xAB));
        ms.Write(Box("moof", 32));
        ms.Write(Box("mdat", 128, 0xCD));
        return ms.ToArray();
    }

    private static long InitEnd(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pcr-init-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return StreamForwarder.FindInitSegmentEnd(fs);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void FindsTheInitSegmentBoundary()
    {
        var bytes = BuildFragmentedMp4();
        var expected = 8 + 16 + (8 + 64); // end of ftyp + end of moov

        Assert.Equal(expected, InitEnd(bytes));
    }

    [Fact]
    public void ReturnsZeroWhenTheInitSegmentIsNotFullyWritten()
    {
        // A truncated moov (encoder still writing) must NOT be reported as a complete
        // init segment: sending half an moov would configure nothing.
        var bytes = BuildFragmentedMp4();
        var truncated = bytes.Take((8 + 16) + 20).ToArray();

        Assert.Equal(0, InitEnd(truncated));
    }

    [Fact]
    public void ReturnsZeroForAnEmptyFile()
    {
        Assert.Equal(0, InitEnd(Array.Empty<byte>()));
    }

    [Fact]
    public async Task JoiningClientStillReceivesTheInitSegment()
    {
        // The point of the fix: a client that is "behind" (which used to be
        // fast-forwarded straight to the live edge) must first be handed the init
        // segment, otherwise it can never decode a single frame.
        var sourcePath = Path.Combine(Path.GetTempPath(), $"pc-stream-late-{Guid.NewGuid():N}.mp4");
        var full = BuildFragmentedMp4();
        await File.WriteAllBytesAsync(sourcePath, full);

        var sent = new List<byte[]>();
        var dropped = 0;
        var forwarder = new StreamForwarder(
            sourcePath,
            frame => { sent.Add(frame.Payload); return Task.CompletedTask; },
            onDropped: () => { dropped++; return Task.CompletedTask; },
            pollMs: 10,
            // Force the "client is far behind" path on the very first read.
            maxBehindBytes: 16);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await forwarder.RunAsync(cts.Token);

            var delivered = sent.SelectMany(x => x).ToArray();
            var initLength = 8 + 16 + (8 + 64);

            // The init segment arrived intact and first...
            Assert.True(delivered.Length >= initLength, "init segment was never delivered");
            Assert.Equal(full.Take(initLength).ToArray(), delivered.Take(initLength).ToArray());
            // ...and the behind-path really did trigger (this is the path that used to
            // skip the init entirely and leave the screen black).
            Assert.True(dropped >= 1, "expected the behind-path to trigger");
        }
        finally { try { File.Delete(sourcePath); } catch { } }
    }
}

public class StreamEndpointTests
{
    private static X509Certificate2 MakeTestCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=pcr-stream-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = signed.Export(X509ContentType.Pkcs12);
        return new X509Certificate2(pfx, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private static ControlChannel MakeChannel(PairingStore pairing, X509Certificate2 cert)
    {
        return new ControlChannel(pairing, "test-pc-id", new InputRouter(new SessionManager()), new SessionManager(), cert);
    }

    private static async Task<(TcpListener listener, Task server)> StartServerAsync(ControlChannel channel)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            await channel.HandleConnectionAsync(client, CancellationToken.None);
        });
        return (listener, server);
    }

    [Fact]
    public async Task StreamStartWithoutAgentReportsUnavailable()
    {
        var cert = MakeTestCert();
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-stream-{Guid.NewGuid():N}.json"));
        var code = pairing.GeneratePairingCode();
        var channel = MakeChannel(pairing, cert);

        var (listener, server) = await StartServerAsync(channel);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}{ControlChannel.StreamPath}"), CancellationToken.None)
                  .WaitAsync(TimeSpan.FromSeconds(15));

        // Authenticate on the /stream socket with the pairing code.
        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "auth", PairingCode = code }));
        var authReply = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("auth_ok", authReply);

        // No user-session agent is running in this test process: the honest
        // failure state is stream_state=error, never a silent "active".
        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "stream_start" }));
        var stateReply = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
        var state = JsonSerializer.Deserialize<RemoteMessage>(stateReply);
        Assert.Equal("stream_state", state?.Type);
        Assert.Equal("error", state?.StreamState);
        Assert.Equal("stream_unavailable", state?.ErrorCode);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await server.WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();
    }

    [Fact]
    public async Task StreamLoopbackDeliversFileBytesAsMediaFrames()
    {
        var cert = MakeTestCert();
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-stream-{Guid.NewGuid():N}.json"));
        var code = pairing.GeneratePairingCode();
        var channel = MakeChannel(pairing, cert);

        // A 200 KiB source file: bigger than one 64 KiB chunk, so the forwarder
        // must emit several ordered MediaFrames to deliver it.
        var sourcePath = Path.Combine(Path.GetTempPath(), $"pc-stream-loopback-{Guid.NewGuid():N}.bin");
        var sourceBytes = new byte[200 * 1024];
        Random.Shared.NextBytes(sourceBytes);
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);

        channel.StreamBinderOverride = () => new StreamBinding(sourcePath, Width: 640, Height: 480, Fps: 15);

        var (listener, server) = await StartServerAsync(channel);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}{ControlChannel.StreamPath}"), CancellationToken.None)
                  .WaitAsync(TimeSpan.FromSeconds(15));

        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "auth", PairingCode = code }));
        _ = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15)); // auth_ok

        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "stream_start" }));
        var stateReply = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
        var state = JsonSerializer.Deserialize<RemoteMessage>(stateReply);
        Assert.Equal("active", state?.StreamState);
        Assert.Equal(640, state?.Width);

        // Collect binary frames until the whole source has arrived.
        var received = new MemoryStream();
        uint lastSeq = 0;
        var firstFrame = true;
        while (received.Length < sourceBytes.Length)
        {
            var (data, type) = await ReceiveMessageAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WebSocketMessageType.Binary, type);
            var frame = MediaFrame.Parse(data);
            Assert.NotNull(frame);
            if (!firstFrame) Assert.True(frame.Value.Seq > lastSeq, "frame sequence must increase");
            lastSeq = frame.Value.Seq;
            firstFrame = false;
            received.Write(frame.Value.Payload);
        }

        Assert.Equal(sourceBytes, received.ToArray());

        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "stream_stop" }));
        var stopReply = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("stopped", stopReply);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await server.WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();
        try { File.Delete(sourcePath); } catch { }
    }

    [Fact]
    public async Task StreamSocketHasNoInputCommandSurface()
    {
        var cert = MakeTestCert();
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-stream-{Guid.NewGuid():N}.json"));
        var code = pairing.GeneratePairingCode();
        var channel = MakeChannel(pairing, cert);

        var (listener, server) = await StartServerAsync(channel);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}{ControlChannel.StreamPath}"), CancellationToken.None)
                  .WaitAsync(TimeSpan.FromSeconds(15));

        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "auth", PairingCode = code }));
        _ = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15)); // auth_ok

        // Input must NOT work on the media socket: the client controls the PC
        // over the / (control) connection, and the stream socket is video-only.
        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "mouse_move", Dx = 10, Dy = 10 }));
        var reply = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
        var msg = JsonSerializer.Deserialize<RemoteMessage>(reply);
        Assert.Equal("command_result", msg?.Type);
        Assert.False(msg?.Success);
        Assert.Equal("unknown_type", msg?.ErrorCode);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await server.WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<(byte[] Data, WebSocketMessageType Type)> ReceiveMessageAsync(ClientWebSocket ws)
    {
        var buffer = new byte[128 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (ms.ToArray(), result.MessageType);
    }
}
