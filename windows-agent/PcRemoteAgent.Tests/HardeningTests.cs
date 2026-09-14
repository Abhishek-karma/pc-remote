using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcRemoteAgent;
using Xunit;

namespace PcRemoteAgent.Tests;

public class HardeningTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"pc-remote-hardening-tests-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { /* best effort */ }
    }

    private PairingStore NewStore() => new(_tempFile);

    [Fact]
    public void PairingCodeIs6DigitsAndNumeric()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();
        Assert.Equal(6, code.Length);
        Assert.True(int.TryParse(code, out var val));
        Assert.InRange(val, 0, 999_999);
    }

    [Fact]
    public void BruteForcePairingAttemptsLockoutIp()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();
        const string attackerIp = "192.168.1.200";

        // Exceed MaxPairingFailures (5 attempts)
        for (var i = 0; i < PairingStore.MaxPairingFailures; i++)
        {
            Assert.False(store.TryAuthenticate(null, "000000", attackerIp));
        }

        // 6th attempt should be blocked due to lockout even if code were somehow correct
        Assert.True(store.IsIpLockedOut(attackerIp));
        Assert.False(store.TryAuthenticate(null, code, attackerIp));
    }

    [Fact]
    public void SuccessfulPairingInvalidatesPairingCode()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();

        Assert.True(store.TryAuthenticate(null, code, "192.168.1.50"));
        // Second attempt with same code must fail because code is single-use
        Assert.False(store.TryAuthenticate(null, code, "192.168.1.50"));
    }

    [Fact]
    public void TokenRevocationRemovesTrust()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();
        Assert.True(store.TryAuthenticate(null, code, "192.168.1.50"));
        var token = store.IssueTokenIfNeeded(null);

        Assert.True(store.TryAuthenticate(token, null, "192.168.1.50"));
        Assert.True(store.RevokeToken(token));
        Assert.False(store.TryAuthenticate(token, null, "192.168.1.50"));
    }

    [Fact]
    public async Task WebSocketRejectsUnmaskedClientFrames()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var cert = CertificateManager.LoadOrCreate();
            using var ws = await WebSocketConnection.AcceptAsync(client.GetStream(), cert, isSecure: false);
            if (ws is null) return null;
            return await ws.ReceiveTextAsync();
        });

        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, port);
        var stream = rawClient.GetStream();

        // Send valid HTTP upgrade request
        const string upgradeReq = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(upgradeReq));

        // Read upgrade response
        var respBuf = new byte[512];
        var read = await stream.ReadAsync(respBuf, 0, respBuf.Length);
        Assert.True(read > 0);

        // Construct UNMASKED text frame (0x81 = final text frame, 0x05 = unmasked length 5, payload = "hello")
        byte[] unmaskedFrame = [0x81, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'];
        await stream.WriteAsync(unmaskedFrame);

        var receivedText = await serverTask;
        Assert.Null(receivedText); // RFC 6455 §5.1: server MUST drop connection on unmasked client frame

        listener.Stop();
    }

    [Fact]
    public void PruneExpiredFailuresRemovesOldLockouts()
    {
        var store = NewStore();
        store.GeneratePairingCode();
        const string attackerIp = "192.168.1.201";

        for (var i = 0; i < PairingStore.MaxPairingFailures; i++)
        {
            store.TryAuthenticate(null, "000000", attackerIp);
        }
        Assert.True(store.IsIpLockedOut(attackerIp));

        store.PruneExpiredFailures();
        // Since lockout hasn't expired yet, it stays
        Assert.True(store.IsIpLockedOut(attackerIp));
    }

    [Fact]
    public async Task WebSocketRejectsFrameWithReservedBits()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var cert = CertificateManager.LoadOrCreate();
            using var ws = await WebSocketConnection.AcceptAsync(client.GetStream(), cert, isSecure: false);
            if (ws is null) return null;
            return await ws.ReceiveTextAsync();
        });

        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, port);
        var stream = rawClient.GetStream();

        const string upgradeReq = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(upgradeReq));

        var respBuf = new byte[512];
        var read = await stream.ReadAsync(respBuf, 0, respBuf.Length);
        Assert.True(read > 0);

        // 0xF1 = FIN=1, RSV1=1, RSV2=1, RSV3=1, Opcode=1 -> MUST BE REJECTED
        byte[] rsvFrame = [0xF1, 0x80, 0x00, 0x00, 0x00, 0x00];
        await stream.WriteAsync(rsvFrame);

        var receivedText = await serverTask;
        Assert.Null(receivedText);

        listener.Stop();
    }

    [Fact]
    public async Task WebSocketRejectsOversizedControlFrame()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var cert = CertificateManager.LoadOrCreate();
            using var ws = await WebSocketConnection.AcceptAsync(client.GetStream(), cert, isSecure: false);
            if (ws is null) return null;
            return await ws.ReceiveTextAsync();
        });

        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, port);
        var stream = rawClient.GetStream();

        const string upgradeReq = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(upgradeReq));

        var respBuf = new byte[512];
        var read = await stream.ReadAsync(respBuf, 0, respBuf.Length);
        Assert.True(read > 0);

        // 0x88 = Close control frame, payload len 126 (extended len) -> MUST BE REJECTED (control frames <= 125)
        byte[] oversizedControl = [0x88, 0xFE, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00];
        await stream.WriteAsync(oversizedControl);

        var receivedText = await serverTask;
        Assert.Null(receivedText);

        listener.Stop();
    }

    [Fact]
    public void RemoteMessageCarriesProtocolVersionAndRequestId()
    {
        var msg = new RemoteMessage
        {
            Version = 1,
            RequestId = "test-req-123",
            Type = "mouse_move",
            Dx = 10,
            Dy = -5
        };

        var json = JsonSerializer.Serialize(msg);
        Assert.Contains("\"version\":1", json);
        Assert.Contains("\"requestId\":\"test-req-123\"", json);

        var deserialized = JsonSerializer.Deserialize<RemoteMessage>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.Version);
        Assert.Equal("test-req-123", deserialized.RequestId);
        Assert.Equal("mouse_move", deserialized.Type);
    }
}
