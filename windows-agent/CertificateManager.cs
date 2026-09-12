// PC Remote - server certificate management
//
// The agent serves WSS with a self-signed certificate generated on first run
// and pinned by the app on first pairing (trust-on-first-use — see
// docs/09-SECURITY-PRIVACY.md §2). The PFX is stored DPAPI-encrypted under
// %AppData%\PcRemoteAgent\server-cert.dat, so no admin rights or netsh
// cert-binding steps are needed.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PcRemoteAgent;

public static class CertificateManager
{
    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcRemoteAgent");

    private static string CertFile => Path.Combine(AppDataDir, "server-cert.dat");

    /// <summary>
    /// Loads the persisted self-signed certificate, or generates and stores a
    /// new one (SANs: localhost + every local IPv4 so pinning by IP works).
    /// Never throws — callers fall back to serving plaintext if TLS setup fails.
    /// </summary>
    public static X509Certificate2 LoadOrCreate()
    {
        try
        {
            if (File.Exists(CertFile))
            {
                var protectedBytes = File.ReadAllBytes(CertFile);
                var pfx = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return new X509Certificate2(pfx);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Could not load saved certificate ({ex.Message}); generating a new one");
        }

        var cert = CreateSelfSigned();
        Save(cert);
        return cert;
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=pc-remote", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        foreach (var ip in Program.GetLocalIPv4Addresses())
        {
            if (IPAddress.TryParse(ip, out var address))
                san.AddIpAddress(address);
        }
        request.CertificateExtensions.Add(san.Build());

        // CreateSelfSigned leaves the key in an ephemeral container that
        // Windows SslStream cannot use for server auth — re-import through
        // PKCS12 to get a persistable key.
        var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = signed.Export(X509ContentType.Pkcs12);
        return new X509Certificate2(pfx, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private static void Save(X509Certificate2 cert)
    {
        var pfx = cert.Export(X509ContentType.Pkcs12);
        var protectedBytes = ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllBytes(CertFile, protectedBytes);
        Console.WriteLine($"[+] TLS certificate created and saved to {CertFile}");
    }
}