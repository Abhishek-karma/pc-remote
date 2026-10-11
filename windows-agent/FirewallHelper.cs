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

        try
        {
            var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;

            // Verify the rules are actually bound to this exe and port — a stale
            // rule (e.g. from a unit-test host) must not satisfy this check.
            var hasTcp = HasCorrectRule(TcpRuleName, exePath, Program.Port);
            var hasMdns = HasCorrectRule(MdnsRuleName, null, 5353);

            if (hasTcp && hasMdns) return;

            // Attempt quiet addition first (succeeds if app runs with admin rights)
            if (!hasTcp) AddRule(TcpRuleName, "TCP", Program.Port, exePath, elevated: false);
            if (!hasMdns) AddRule(MdnsRuleName, "UDP", 5353, exePath, elevated: false);

            // Verify if rules were created; if not, request elevation (UAC prompt) once
            if (!HasCorrectRule(TcpRuleName, exePath, Program.Port) || !HasCorrectRule(MdnsRuleName, null, 5353))
            {
                if (!HasCorrectRule(TcpRuleName, exePath, Program.Port)) AddRule(TcpRuleName, "TCP", Program.Port, exePath, elevated: true);
                if (!HasCorrectRule(MdnsRuleName, null, 5353)) AddRule(MdnsRuleName, "UDP", 5353, exePath, elevated: true);
            }
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

    /// <summary>
    /// True only when a rule with this name exists AND is bound to the expected
    /// program and port. A stale rule (e.g. left over from a unit-test host) must
    /// not satisfy this check, otherwise the agent never provisions its own rule.
    /// </summary>
    public static bool HasCorrectRule(string ruleName, string? expectedProgram, int expectedPort)
    {
        if (!HasRule(ruleName)) return false;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{ruleName}\" verbose",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(3000);
            if (proc?.ExitCode != 0) return false;

            var programOk = string.IsNullOrEmpty(expectedProgram)
                || output.Contains(expectedProgram, StringComparison.OrdinalIgnoreCase);
            var portOk = output.Contains("LocalPort:", StringComparison.OrdinalIgnoreCase) &&
                         output.Contains(expectedPort.ToString(), StringComparison.Ordinal);
            return programOk && portOk;
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
