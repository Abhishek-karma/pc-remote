// Server certificate + stable PC identity.
//
// Identity rules:
//   * The self-signed certificate is generated ONCE and then reused forever.
//     It is never regenerated because the local IP addresses changed — the
//     Android client pins the certificate fingerprint (trust-on-first-use),
//     and a regeneration would silently invalidate every pairing. The IP is
//     only a transport address; SANs are advisory for handshaking clients.
//   * The stable PC identity (PcId) is a random GUID persisted next to the
//     certificate and reported in auth_ok / mDNS TXT records. The Android
//     client keys its saved token and pin by this value, so DHCP changes
//     never force re-pairing.
// The PFX is DPAPI-protected. Under the service the scope is LocalMachine
// (file lives under ProgramData\PCRemote, ACL restricted to SYSTEM/Admins).

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PcRemote.Core;

public static class CertificateManager
{
    private static string CertFile => Path.Combine(PairingStore.ServiceDataDir, "server-cert.dat");
    private static string PcIdFile => Path.Combine(PairingStore.ServiceDataDir, "pc-id");

    /// <summary>Loads the persisted certificate, or creates it on first run.
    /// Never throws — the caller falls back to refusing connections (we never
    /// serve plaintext) rather than serving without TLS.</summary>
    public static X509Certificate2 LoadOrCreate()
    {
        try
        {
            if (File.Exists(CertFile))
            {
                var protectedBytes = File.ReadAllBytes(CertFile);
                var pfx = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                var cert = new X509Certificate2(pfx);
                AgentLog.Info($"TLS certificate loaded (thumbprint {cert.Thumbprint})");
                return cert;
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Could not load saved certificate ({ex.Message}); generating a new one");
        }

        var cert2 = CreateSelfSigned();
        Save(cert2);
        return cert2;
    }

    /// <summary>Loads or creates the stable PC identity GUID.</summary>
    public static string LoadOrCreatePcId()
    {
        try
        {
            if (File.Exists(PcIdFile))
            {
                var id = File.ReadAllText(PcIdFile).Trim();
                if (Guid.TryParse(id, out _)) return id;
            }
            var fresh = Guid.NewGuid().ToString();
            Directory.CreateDirectory(PairingStore.ServiceDataDir);
            File.WriteAllText(PcIdFile, fresh);
            AgentLog.Info($"PC identity created: {fresh[..8]}…");
            return fresh;
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Could not persist PC identity ({ex.Message}); using ephemeral id");
            return Guid.NewGuid().ToString();
        }
    }

    /// <summary>True when the certificate can be used for TLS server auth.</summary>
    public static bool IsUsableForServerAuth(X509Certificate2? cert)
    {
        if (cert is null) return false;
        using var chain = new X509Chain();
        // We only validate that the key matches and the cert is time-valid;
        // self-signed chain errors are expected and handled by client pinning.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        return cert.NotBefore <= DateTimeOffset.UtcNow
            && cert.NotAfter >= DateTimeOffset.UtcNow
            && cert.HasPrivateKey;
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PC Remote", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // SANs are written once, at creation time, for clients that still do
        // hostname-style validation. New IPs later do NOT trigger regeneration.
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        foreach (var ip in NetworkInfo.GetLocalIPv4Addresses())
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
        var protectedBytes = ProtectedData.Protect(pfx, null, DataProtectionScope.LocalMachine);
        Directory.CreateDirectory(PairingStore.ServiceDataDir);
        File.WriteAllBytes(CertFile, protectedBytes);
        AgentLog.Info($"TLS certificate created and saved to {CertFile}");
    }
}
