// Local network interface enumeration — the only place IP addresses are
// treated as anything more than transport details. Virtual adapters
// (WSL, VMware, Hyper-V, VirtualBox) are filtered out so the UI and
// certificate SANs only list real LAN addresses.

using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PcRemote.Core;

public static class NetworkInfo
{
    public static IEnumerable<string> GetLocalIPv4Addresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            string name = ni.Name ?? "";
            string desc = ni.Description ?? "";
            if (IsVirtual(name) || IsVirtual(desc)) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(addr.Address))
                {
                    yield return addr.Address.ToString();
                }
            }
        }
    }

    private static bool IsVirtual(string s) =>
        s.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);
}
