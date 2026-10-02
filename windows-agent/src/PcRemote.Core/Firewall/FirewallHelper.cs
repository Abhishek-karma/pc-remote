// PC Remote - Firewall rules
// Least exposure (requirement 9): the WSS listener is allowed only on
// Private/Domain profiles and only for the local subnet; never on Public.
// The installer creates the same rules at install time; this helper repairs
// them if they are missing (e.g. after a Windows reset of the firewall).

using System.Diagnostics;

namespace PcRemote.Core;

public static class FirewallHelper
{
    public const string TcpRuleName = "PC Remote Service (LAN, private)";
    public const string MdnsRuleName = "PC Remote mDNS (LAN, private)";

    public static void EnsureFirewallRules()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (HasRule(TcpRuleName) && HasRule(MdnsRuleName)) return;

            if (!HasRule(TcpRuleName)) AddRule(TcpRuleName, "TCP", 58642);
            if (!HasRule(MdnsRuleName)) AddRule(MdnsRuleName, "UDP", 5353);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Firewall auto-config notice: {ex.Message}");
        }
    }

    public static bool HasRule(string ruleName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{ruleName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Private + domain profiles only, scoped to the local subnet.
    /// Never Public. Called by the service (SYSTEM), so no elevation prompt.</summary>
    private static bool AddRule(string ruleName, string protocol, int port)
    {
        try
        {
            var args = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow " +
                       $"protocol={protocol} localport={port} profile=private,domain " +
                       $"remoteip=localsubnet enable=yes";
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
