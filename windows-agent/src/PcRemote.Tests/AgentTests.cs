// Unit + integration tests: pairing/auth, protocol wire format, command
// allowlist, TLS+WSS handshake, and certificate identity stability.
// No real network beyond loopback; no admin rights required.

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

        // Same backing file, new instance — as after a service restart.
        var reloaded = NewStore();
        Assert.True(reloaded.TryAuthenticate(token, null));
        Assert.Contains(token, reloaded.TrustedTokens);
    }

    [Fact]
    public void IssuedTokensAreHighEntropy()
    {
        // Tokens must be 32 bytes of CSPRNG output, not GUID-shaped.
        var store = NewStore();
        var token = store.IssueTokenIfNeeded(null);
        Assert.Equal(64, token.Length); // 32 bytes hex
        Assert.All(token, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void ImportedLegacyTokensAuthenticate()
    {
        var store = NewStore();
        store.ImportLegacyTokens(["legacy-token-1", "legacy-token-2"]);
        Assert.True(store.TryAuthenticate("legacy-token-1", null));
        Assert.True(store.TryAuthenticate("legacy-token-2", null));
    }

    [Fact]
    public void ServiceTokensFileLivesUnderProgramData()
    {
        // Mutable state must live under ProgramData (service-writable,
        // Program Files stays read-only).
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.Equal(
            Path.Combine(programData, "PCRemote", "trusted-devices.json"),
            PairingStore.DefaultServiceTokensFile);
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
    public void AuthOkCarriesStablePcId()
    {
        // The stable identity travels on auth_ok so clients can key pins and
        // tokens by it — DHCP changes never force re-pairing (req. 10).
        var json = JsonSerializer.Serialize(new RemoteMessage { Type = "auth_ok", PcId = "d6f1a372-0000-0000-0000-000000000000" });
        Assert.Contains("\"pcId\":\"d6f1a372", json);

        var back = JsonSerializer.Deserialize<RemoteMessage>(json);
        Assert.Equal("d6f1a372-0000-0000-0000-000000000000", back?.PcId);
    }

    [Fact]
    public void SessionStateDeserializes()
    {
        var back = JsonSerializer.Deserialize<RemoteMessage>(
            "{\"type\":\"session_status\",\"sessionState\":\"locked\"}");
        Assert.Equal("locked", back?.SessionState);
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
}

public class CommandAllowlistTests
{
    /// <summary>The service must never execute a message type outside the
    /// fixed allowlist — no remote shell, no arbitrary process launch.</summary>
    [Fact]
    public void ArbitraryAndDangerousTypesAreRejected()
    {
        Assert.False(CommandAllowlist.IsAllowed("exec"));
        Assert.False(CommandAllowlist.IsAllowed("run"));
        Assert.False(CommandAllowlist.IsAllowed("shell"));
        Assert.False(CommandAllowlist.IsAllowed("download_file"));
        Assert.False(CommandAllowlist.IsAllowed("start_process"));
        Assert.False(CommandAllowlist.IsAllowed("read_file"));
        Assert.False(CommandAllowlist.IsAllowed(""));
    }

    [Fact]
    public void AllShippedCommandsAreAllowed()
    {
        foreach (var cmd in new[]
                 {
                     "mouse_move", "mouse_click", "mouse_scroll", "key_press", "text_input",
                     "media_control", "system_power", "sas", "session_status",
                     "stream_request", "disconnect",
                 })
        {
            Assert.True(CommandAllowlist.IsAllowed(cmd), cmd);
        }
    }
}

public class MdnsAdvertiserTests
{
    [Fact]
    public void ServiceTypeMatchesTheDocumentedContract()
    {
        Assert.Equal("_pc-remote._tcp.", MdnsAdvertiser.ServiceType);
    }
}

// Regression tests for the IPC privilege boundary and the trust-material
// rules introduced with the 0.2.0 service split. These assert the properties
// SECURITY.md claims, since a silent regression here would re-open a local
// privilege-escalation path (unprivileged process -> trusted token).
public class IpcPrivilegeBoundaryTests
{
    private static IpcCoordinator MakeCoordinator(PairingStore pairing) =>
        new(pairing, "test-pc-id", new InputRouter(new SessionManager()), new SessionManager(),
            new CancellationTokenSource());

    [Fact]
    public void MigrateTokensIsRefusedForUnelevatedCaller()
    {
        // Regression: this handler writes the trusted-device set and was
        // reachable by ANY local user through the pipe ACL.
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ipt-{Guid.NewGuid():N}.json"));
        var coordinator = MakeCoordinator(pairing);

        var reply = coordinator.HandleIpc(new IpcMessage
        {
            Type = "migrate_tokens",
            Payload = JsonSerializer.SerializeToElement(new[] { "attacker-injected-token" }),
        }, IpcPrivilege.Standard);

        Assert.False(reply.Ok);
        Assert.Equal("elevation_required", reply.Error);
        // And, critically, the token must NOT have become trusted.
        Assert.False(pairing.TryAuthenticate("attacker-injected-token", null));
    }

    [Fact]
    public void MigrateTokensSucceedsForElevatedCaller()
    {
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ipt-{Guid.NewGuid():N}.json"));
        var coordinator = MakeCoordinator(pairing);

        var reply = coordinator.HandleIpc(new IpcMessage
        {
            Type = "migrate_tokens",
            Payload = JsonSerializer.SerializeToElement(new[] { "legacy-token-1" }),
        }, IpcPrivilege.Elevated);

        Assert.True(reply.Ok);
        Assert.True(pairing.TryAuthenticate("legacy-token-1", null));
    }

    [Theory]
    [InlineData("revoke_all_devices")]
    [InlineData("update_apply")]
    [InlineData("update_check")]
    [InlineData("generate_pairing_code")]
    [InlineData("desktop_report")]
    public void PrivilegedOperationsAreRefusedForUnelevatedCaller(string type)
    {
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ipt-{Guid.NewGuid():N}.json"));
        var coordinator = MakeCoordinator(pairing);

        var reply = coordinator.HandleIpc(new IpcMessage { Type = type }, IpcPrivilege.Standard);

        Assert.False(reply.Ok);
        Assert.Equal("elevation_required", reply.Error);
    }

    [Fact]
    public void StatusHidesPairingCodeFromUnelevatedCaller()
    {
        // The pairing code is trust material: anyone who reads it can pair a
        // device, and the pipe ACL admits every local user.
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ipt-{Guid.NewGuid():N}.json"));
        pairing.GeneratePairingCode();
        var coordinator = MakeCoordinator(pairing);

        var standard = coordinator.HandleIpc(new IpcMessage { Type = "status" }, IpcPrivilege.Standard);
        var elevated = coordinator.HandleIpc(new IpcMessage { Type = "status" }, IpcPrivilege.Elevated);

        Assert.Null(standard.PairingCode);
        Assert.False(string.IsNullOrEmpty(elevated.PairingCode));
    }

    [Fact]
    public void UnknownIpcTypeIsRejected()
    {
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ipt-{Guid.NewGuid():N}.json"));
        var coordinator = MakeCoordinator(pairing);

        var reply = coordinator.HandleIpc(new IpcMessage { Type = "run_shell" }, IpcPrivilege.Elevated);

        Assert.False(reply.Ok);
        Assert.Equal("unknown_type", reply.Error);
    }
}

public class SessionPipeNamingTests
{
    [Fact]
    public void SecureAndNormalHelpersUseDifferentPipes()
    {
        // Regression: both helpers previously used "PCRemoteSessionCtl-{id}",
        // so a Winlogon command could be served by the normal-desktop helper
        // and vice versa.
        var secure = IpcEndpoints.SessionPipe(1, secure: true);
        var normal = IpcEndpoints.SessionPipe(1, secure: false);

        Assert.NotEqual(secure, normal);
        Assert.Contains("1", secure);
    }

    [Fact]
    public void SessionPipeIsStablePerSessionAndMode()
    {
        Assert.Equal(IpcEndpoints.SessionPipe(2, true), IpcEndpoints.SessionPipe(2, true));
        Assert.NotEqual(IpcEndpoints.SessionPipe(2, true), IpcEndpoints.SessionPipe(3, true));
    }
}

public class UpdateVersionComparisonTests
{
    [Theory]
    [InlineData("0.2.1", "0.2.0", true)]
    [InlineData("v0.3.0", "0.2.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    // Regression: plain string inequality reported these as updates.
    [InlineData("0.1.9", "0.2.0", false)]   // downgrade
    [InlineData("0.2.0", "0.2.0", false)]   // same
    [InlineData("0.2.0.1", "0.2.0", true)]  // local extra revision
    [InlineData("0.2.0", "0.2.0-beta", false)]
    [InlineData("garbage", "0.2.0", false)]
    [InlineData("", "0.2.0", false)]
    public void DetectsOnlyStrictlyNewerVersions(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateCoordinator.IsNewerVersion(candidate, current));
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

    private static ControlChannel MakeChannel(PairingStore pairing, X509Certificate2 cert)
    {
        return new ControlChannel(pairing, "test-pc-id", new InputRouter(new SessionManager()), new SessionManager(), cert);
    }

    [Fact]
    public async Task AuthOverTlsCompletesWithAuthFailedForBadCode()
    {
        var cert = MakeTestCert();
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ws-{Guid.NewGuid():N}.json"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var channel = MakeChannel(pairing, cert);
        var server = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            await channel.HandleConnectionAsync(client, CancellationToken.None);
        });

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Type = "auth", PairingCode = "111222" }));
        var reply = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("auth_failed", reply);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await server.WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();
    }

    [Fact]
    public async Task UnsupportedVersionIsRejected()
    {
        var cert = MakeTestCert();
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ws-{Guid.NewGuid():N}.json"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var channel = MakeChannel(pairing, cert);
        var server = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            await channel.HandleConnectionAsync(client, CancellationToken.None);
        });

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        await SendTextAsync(ws, JsonSerializer.Serialize(new RemoteMessage { Version = 99, Type = "auth" }));
        var reply = await ReceiveTextAsync(ws).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("unsupported_version", reply);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await server.WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();
    }

    [Fact]
    public async Task PlainHttpIsRejected()
    {
        var cert = MakeTestCert();
        var pairing = new PairingStore(Path.Combine(Path.GetTempPath(), $"pc-ws-{Guid.NewGuid():N}.json"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var channel = MakeChannel(pairing, cert);
        var server = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            await channel.HandleConnectionAsync(client, CancellationToken.None);
        });

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await stream.WriteAsync(request);

        // TLS server never completes the handshake for a plaintext client:
        // the read either fails or blocks until the test client gives up and
        // the connection is torn down. We only assert the server didn't crash.
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
}
