// PC Remote Windows Agent - Automatic Firewall Helper
// Automatically verifies and creates Windows Firewall rules for WSS (58642/TCP) and mDNS (5353/UDP)
// so users do not need to manually run PowerShell/netsh commands.

using System.Diagnostics;
using System.Reflection;

namespace PcRemoteAgent;

public static class FirewallHelper
{
    public const string TcpRuleName = "PC Remote Agent";
    public const string MdnsRuleName = "PC Remote Agent mDNS";

    public static void EnsureFirewallRules()
    {
        if (!OperatingSystem.IsWindows()) return;

        CleanupLegacyService();

        try
        {
            var hasTcp = HasRule(TcpRuleName);
            var hasMdns = HasRule(MdnsRuleName);

            if (hasTcp && hasMdns) return;

            var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;

            // Attempt quiet addition first (succeeds if app runs with admin rights)
            if (!hasTcp) AddRule(TcpRuleName, "TCP", Program.Port, exePath, elevated: false);
            if (!hasMdns) AddRule(MdnsRuleName, "UDP", 5353, exePath, elevated: false);

            // Verify if rules were created; if not, request elevation (UAC prompt) once
            if (!HasRule(TcpRuleName) || !HasRule(MdnsRuleName))
            {
                if (!HasRule(TcpRuleName)) AddRule(TcpRuleName, "TCP", Program.Port, exePath, elevated: true);
                if (!HasRule(MdnsRuleName)) AddRule(MdnsRuleName, "UDP", 5353, exePath, elevated: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Firewall auto-config notice: {ex.Message}");
        }
    }

    private static void CleanupLegacyService()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query PCRemoteService",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
            proc?.WaitForExit(2000);
            if (proc?.ExitCode == 0)
            {
                Process.Start(new ProcessStartInfo { FileName = "sc.exe", Arguments = "stop PCRemoteService", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
                Process.Start(new ProcessStartInfo { FileName = "sc.exe", Arguments = "delete PCRemoteService", CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(2000);
            }
        }
        catch { }
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

    private static bool AddRule(string ruleName, string protocol, int port, string? exePath, bool elevated)
    {
        try
        {
            var args = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol={protocol} localport={port} profile=any enable=yes";
            if (!string.IsNullOrEmpty(exePath))
            {
                args += $" program=\"{exePath}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = elevated
            };

            if (elevated)
            {
                psi.Verb = "runas";
            }

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
