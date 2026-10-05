// PC Remote - mDNS Advertiser
// Uses Makaretu.Dns.Multicast (DNS-SD over mDNS) to advertise the service
// as "_pc-remote._tcp." on the LAN so the Android app can discover the PC
// without typing an IP address. The TXT record carries the stable pcid so
// clients can recognize the same PC across DHCP changes.

using Makaretu.Dns;

namespace PcRemote.Core;

public static class MdnsAdvertiser
{
    public const string ServiceType = "_pc-remote._tcp.";

    private static ServiceDiscovery? _discovery;

    /// <summary>
    /// Advertise the service on all local network interfaces. Non-blocking.
    /// Call <see cref="Stop"/> on shutdown to send Goodbye packets.
    /// </summary>
    public static void Start(int port, string pcId)
    {
        try
        {
            // Instance name is the machine name so each PC advertises a unique
            // service instance (the app dedupes by instance name).
            var instance = Environment.MachineName;
            var profile = new ServiceProfile(instance, ServiceType, (ushort)port);
            profile.AddProperty("name", instance);
            profile.AddProperty("pcid", pcId);

            _discovery = new ServiceDiscovery();
            _discovery.Advertise(profile);

            Log.Info($"mDNS: advertising \"{instance}\" {ServiceType} on port {port} (pcid {pcId[..8]}…)");
        }
        catch (Exception ex)
        {
            // Discovery is a convenience, not a hard dependency — fall back to
            // the manual-IP flow.
            Log.Warn($"mDNS advertisement failed: {ex.Message} — manual IP entry only");
        }
    }

    /// <summary>Unadvertise and release the mDNS socket. Safe to call if Start
    /// was never called or already stopped.</summary>
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
