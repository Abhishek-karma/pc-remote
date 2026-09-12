// PC Remote - mDNS Advertiser
// Uses Makaretu.Dns.Multicast (DNS-SD over mDNS) to advertise the agent
// as "_pc-remote._tcp." on the LAN so the Android app can discover it
// without requiring the user to type an IP address.
//
// See docs/07-API-SPECIFICATION.md §7 for the service contract.
// See docs/14-OBSERVABILITY-LOGGING.md for logging rules — hostname only,
// never tokens or pairing codes.

using Makaretu.Dns;

namespace PcRemoteAgent;

public static class MdnsAdvertiser
{
    /// <summary>
    /// DNS-SD service type (without trailing dot — Makaretu normalises it).
    /// The canonical discovery name is _pc-remote._tcp.local.
    /// </summary>
    public const string ServiceType = "_pc-remote._tcp.";

    private static ServiceDiscovery? _discovery;

    /// <summary>
    /// Advertise the agent on all local network interfaces. Non-blocking:
    /// the multicast responder runs in background threads owned by
    /// Makaretu.Dns. Call <see cref="Stop"/> on shutdown to send
    /// Goodbye packets and release the socket.
    /// </summary>
    public static void Start(int port)
    {
        try
        {
            // Instance name is the machine name so each PC advertises a unique
            // service instance (the app dedupes by instance name).
            var instance = Environment.MachineName;
            var profile = new ServiceProfile(instance, ServiceType, (ushort)port);
            profile.AddProperty("name", instance);

            _discovery = new ServiceDiscovery();
            _discovery.Advertise(profile);

            Console.WriteLine(
                $"mDNS: advertising \"{instance}\" {ServiceType} on port {port}");
        }
        catch (Exception ex)
        {
            // Discovery is a convenience, not a hard dependency — fall back to
            // the manual-IP flow the console already prints.
            Console.WriteLine($"[!] mDNS advertisement failed: {ex.Message}"
                + " — manual IP entry only");
        }
    }

    /// <summary>
    /// Unadvertise and release the mDNS socket. Safe to call if Start was
    /// never called or already stopped.
    /// </summary>
    public static void Stop()
    {
        try
        {
            _discovery?.Unadvertise();
            _discovery?.Dispose();
        }
        catch
        {
            // Best-effort cleanup on shutdown; swallow.
        }

        _discovery = null;
    }
}
