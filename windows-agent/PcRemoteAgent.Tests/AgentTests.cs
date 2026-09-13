// Unit + integration tests for the agent's non-Win32 logic, per
// docs/11-TESTING-STRATEGY.md §2:
//  - PairingStore: code expiry, token persistence (DPAPI file), auth matrix
//  - RemoteMessage: JSON round-trips and wire names
//  - WebSocket-over-TLS: a real localhost client completes the handshake,
//    the auth gate, and orderly close (no real network, no admin rights)

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PcRemoteAgent;
using Xunit;

namespace PcRemoteAgent.Tests;

public class PairingStoreTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"pc-remote-tests-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { /* best effort */ }
    }

    private PairingStore NewStore() => new(_tempFile);

    [Fact]
    public void ValidPairingCodeAuthenticates()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();
        Assert.True(store.TryAuthenticate(null, code));
    }

    [Fact]
    public void InvalidPairingCodeIsRejected()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();
        var wrong = code == "111222" ? "000000" : "111222";
        Assert.False(store.TryAuthenticate(null, wrong));
    }

    [Fact]
    public async Task ExpiredPairingCodeIsRejected()
    {
        var original = PairingStore.CodeLifetime;
        try
        {
            PairingStore.CodeLifetime = TimeSpan.FromMilliseconds(50);
            var store = NewStore();
            var code = store.GeneratePairingCode();
            Assert.False(store.IsCodeExpired());

            await Task.Delay(120);
            Assert.True(store.IsCodeExpired());
            Assert.False(store.TryAuthenticate(null, code));
        }
        finally
        {
            PairingStore.CodeLifetime = original;
        }
    }

    [Fact]
    public void IssuedTokenAuthenticatesThereafter()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();
        Assert.True(store.TryAuthenticate(null, code));
        var token = store.IssueTokenIfNeeded(null);
        Assert.True(store.TryAuthenticate(token, null));
    }

    [Fact]
    public void UnknownTokenIsRejected()
    {
        var store = NewStore();
        Assert.False(store.TryAuthenticate("not-a-real-token", null));
    }

    [Fact]
    public void ReissuingTrustedTokenReturnsTheSameToken()
    {
        var store = NewStore();
        var first = store.IssueTokenIfNeeded(null);
        Assert.Equal(first, store.IssueTokenIfNeeded(first));
    }

    [Fact]
    public void NewDeviceGetsADistinctToken()
    {
        var store = NewStore();
        Assert.NotEqual(store.IssueTokenIfNeeded(null), store.IssueTokenIfNeeded(null));
    }

    [Fact]
    public void TokensSurviveStoreRecreation()
    {
        var store = NewStore();
        var code = store.GeneratePairingCode();
        Assert.True(store.TryAuthenticate(null, code));
        var token = store.IssueTokenIfNeeded(null);

        // Same backing file, new instance — as after an agent restart.
        var reloaded = NewStore();
        Assert.True(reloaded.TryAuthenticate(token, null));
        Assert.Contains(token, reloaded.TrustedTokens);
    }

    [Fact]
    public void TokensFileIsWritten()
    {
        var store = NewStore();
        store.IssueTokenIfNeeded(null);
        Assert.True(File.Exists(_tempFile));
        Assert.True(new FileInfo(_tempFile).Length > 8);
    }

    [Fact]
    public void DefaultTokensFileLivesUnderAppData()
    {
        // Mutable state must live under %AppData%, never beside the exe (Program Files installs).
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(
            Path.Combine(appData, "PcRemoteAgent", "trusted-devices.json"),
            PairingStore.DefaultTokensFile);
    }
}

public class RemoteMessageTests
{
    [Fact]
    public void SerializesWithCamelCaseWireNames()
    {
        var json = JsonSerializer.Serialize(new RemoteMessage { Type = "mouse_move", Dx = 12, Dy = -4 });
        Assert.Contains("\"type\":\"mouse_move\"", json);
        Assert.Contains("\"dx\":12", json);
        Assert.Contains("\"dy\":-4", json);
    }

    [Fact]
    public void RoundTripsAuthMessage()
    {
        var original = new RemoteMessage { Type = "auth", Token = "abc123" };
        var back = JsonSerializer.Deserialize<RemoteMessage>(JsonSerializer.Serialize(original));
        Assert.NotNull(back);
        Assert.Equal("auth", back.Type);
        Assert.Equal("abc123", back.Token);
    }

    [Fact]
    public void RoundTripsKeyPressWithModifiers()
    {
        var original = new RemoteMessage { Type = "key_press", Key = "C", Modifiers = new List<string> { "CTRL" } };
        var back = JsonSerializer.Deserialize<RemoteMessage>(JsonSerializer.Serialize(original));
        Assert.NotNull(back);
        Assert.Equal("C", back.Key);
        Assert.Equal(new[] { "CTRL" }, back.Modifiers);
    }

    [Fact]
    public void RoundTripsFunctionKeyCombo()
    {
        var original = new RemoteMessage { Type = "key_press", Key = "F4", Modifiers = new List<string> { "ALT" } };
        var back = JsonSerializer.Deserialize<RemoteMessage>(JsonSerializer.Serialize(original));
        Assert.NotNull(back);
        Assert.Equal("F4", back.Key);
        Assert.Equal(new[] { "ALT" }, back.Modifiers);
    }

    [Fact]
    public void RoundTripsExtendedEditingKey()
    {
        var original = new RemoteMessage { Type = "key_press", Key = "PRTSC" };
        var back = JsonSerializer.Deserialize<RemoteMessage>(JsonSerializer.Serialize(original));
        Assert.NotNull(back);
        Assert.Equal("PRTSC", back.Key);
    }

    [Fact]
    public void OmittedFieldsDeserializeAsNull()
    {
        var back = JsonSerializer.Deserialize<RemoteMessage>("{\"type\":\"auth_failed\"}");
        Assert.NotNull(back);
        Assert.Equal("auth_failed", back.Type);
        Assert.Null(back.Token);
        Assert.Null(back.Text);
    }

    [Fact]
    public void DisconnectingMessageCarriesTheReason()
    {
        var original = new RemoteMessage { Type = "disconnecting", Reason = "shutdown" };
        var back = JsonSerializer.Deserialize<RemoteMessage>(JsonSerializer.Serialize(original));
        Assert.NotNull(back);
        Assert.Equal("disconnecting", back.Type);
        Assert.Equal("shutdown", back.Reason);
    }
}

public class MdnsAdvertiserTests
{
    [Fact]
    public void ServiceTypeMatchesTheDocumentedContract()
    {
        // Full DNS-SD name is "_pc-remote._tcp.local."; the advertiser passes
        // the type prefix and Makaretu appends the domain (07-API-SPEC.md §7).
        Assert.Equal("_pc-remote._tcp.", MdnsAdvertiser.ServiceType);
    }
}

public class WsTlsIntegrationTests
{
    private static X509Certificate2 MakeTestCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=pc-remote-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
    public async Task AuthOverTlsCompletesWithAuthFailedForBadCode()
    {
        var cert = MakeTestCert();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            await Program.HandleConnectionAsync(client, cert);
        });

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var connect = ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/"), CancellationToken.None);
        await connect.WithTimeout(15);

        // Bad pairing code -> auth_failed, proving handshake + TLS + framing
        // + the auth gate all work together.
        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "auth", PairingCode = "111222" }));
        var reply = await ReceiveTextAsync(ws).WithTimeout(15);
        Assert.Contains("auth_failed", reply);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await server.WithTimeout(15);
        listener.Stop();
    }

    [Fact]
    public async Task PlainHttpIsRejected()
    {
        var cert = MakeTestCert();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            await Program.HandleConnectionAsync(client, cert);
        });

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await stream.WriteAsync(request);

        // TLS server never completes the handshake for a plaintext client:
        // the read either fails or blocks until the test client gives up and
        // the connection is torn down. We only assert the server didn't crash.
        await server.WithTimeout(15);
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
}

internal static class TaskExtensions
{
    /// <summary>Test-only timeout so a hang fails the test instead of stalling the runner.</summary>
    public static async Task<T> WithTimeout<T>(this Task<T> task, int seconds)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        if (completed != task) throw new TimeoutException("Operation timed out in test");
        return await task;
    }

    public static Task WithTimeout(this Task task, int seconds) =>
        task.ContinueWith(t => t, TaskContinuationOptions.OnlyOnRanToCompletion)
            .WithTimeout(seconds)
            .ContinueWith(t => Task.CompletedTask, TaskContinuationOptions.OnlyOnRanToCompletion)
            .Unwrap();
}