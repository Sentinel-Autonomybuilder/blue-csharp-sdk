using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Tunnel.WireGuard;

// ─── DNS Leak Prevention ───

/// <summary>
/// Prevents DNS leaks by blocking DNS traffic (port 53) on all interfaces except
/// through the tunnel's DNS server.
/// <para>Platform support:</para>
/// <list type="bullet">
///   <item><description>Windows: netsh advfirewall rules blocking port 53 and DoH</description></item>
///   <item><description>macOS: pfctl anchor rules for DNS blocking</description></item>
///   <item><description>Linux: iptables rules blocking port 53 except to tunnel DNS</description></item>
/// </list>
/// <para>
/// When enabled, the following traffic is blocked:
/// <list type="bullet">
///   <item><description>All outbound UDP port 53 (standard DNS) — except to the tunnel DNS</description></item>
///   <item><description>All outbound TCP port 53 (DNS-over-TLS)</description></item>
///   <item><description>DNS-over-HTTPS to well-known public resolvers (1.1.1.1, 8.8.8.8, 8.8.4.4, 9.9.9.9)</description></item>
/// </list>
/// </para>
/// Requires administrator/root privileges.
/// </summary>
public class DnsLeakPrevention : IDisposable
{
    private const string RULE_PREFIX = "SentinelVPN";
    private const string DEFAULT_TUNNEL_DNS = "10.8.0.1";
    private const string IPTABLES_COMMENT = "sentinel-vpn-dns";
    private const string PF_DNS_CONF_PATH = "/tmp/sentinel-dns-leak.conf";

    /// <summary>
    /// Well-known DNS-over-HTTPS server IPs to block.
    /// Prevents the OS or browsers from bypassing the tunnel DNS via DoH.
    /// </summary>
    private static readonly string[] DOH_SERVERS =
    [
        "1.1.1.1",
        "1.0.0.1",
        "8.8.8.8",
        "8.8.4.4",
        "9.9.9.9",
        "149.112.112.112",
    ];

    private bool _disposed;
    private bool _enabled;
    private string? _tunnelDns;

    /// <summary>
    /// Whether DNS leak prevention is currently active.
    /// </summary>
    public bool IsEnabled => _enabled;

    // ─── Enable ───

    /// <summary>
    /// Enable DNS leak prevention. Blocks all DNS traffic except to the tunnel's
    /// DNS server, preventing the OS from sending DNS queries outside the tunnel.
    /// </summary>
    /// <param name="tunnelDns">
    /// IP address of the DNS server inside the tunnel. Defaults to "10.8.0.1".
    /// </param>
    /// <exception cref="SentinelException">
    /// Thrown when rules cannot be applied (e.g. missing admin privileges).
    /// </exception>
    public async Task EnableAsync(string tunnelDns = DEFAULT_TUNNEL_DNS)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_enabled)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await EnableWindowsAsync(tunnelDns);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await EnableMacOsAsync(tunnelDns);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await EnableLinuxAsync(tunnelDns);
        }
        else
        {
            throw new SentinelException("UNSUPPORTED_PLATFORM", "DNS leak prevention is not supported on this platform");
        }

        _tunnelDns = tunnelDns;
        _enabled = true;
    }

    // ─── Disable ───

    /// <summary>
    /// Disable DNS leak prevention and restore normal DNS resolution.
    /// Safe to call even if DNS leak prevention was never enabled.
    /// </summary>
    public async Task DisableAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_enabled)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await DisableWindowsAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await DisableMacOsAsync();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await DisableLinuxAsync();
        }

        _tunnelDns = null;
        _enabled = false;
    }

    // ─── Windows (netsh advfirewall) ───

    /// <summary>
    /// Enable DNS leak prevention on Windows using netsh advfirewall firewall rules.
    /// </summary>
    private static async Task EnableWindowsAsync(string tunnelDns)
    {
        // ─── Allow DNS only to tunnel DNS server (add ALLOW before BLOCK) ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Allow-TunnelDNS",
            "dir=out", "action=allow", "protocol=udp",
            $"remoteip={tunnelDns}", "remoteport=53"
        );

        // ─── Block all outbound UDP port 53 (standard DNS) ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Block-DNS",
            "dir=out", "action=block", "protocol=udp",
            "remoteport=53"
        );

        // ─── Block all outbound TCP port 53 (DNS-over-TLS) ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Block-DoT",
            "dir=out", "action=block", "protocol=tcp",
            "remoteport=53"
        );

        // ─── Block DNS-over-HTTPS to well-known resolvers ───
        var dohIps = string.Join(",", DOH_SERVERS);
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Block-DoH",
            "dir=out", "action=block", "protocol=tcp",
            $"remoteip={dohIps}", "remoteport=443"
        );
    }

    /// <summary>
    /// Disable DNS leak prevention on Windows by removing all netsh rules.
    /// </summary>
    private static async Task DisableWindowsAsync()
    {
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Block-DNS");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Allow-TunnelDNS");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Block-DoT");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Block-DoH");
    }

    // ─── macOS (pfctl anchor) ───

    /// <summary>
    /// Enable DNS leak prevention on macOS by loading pf rules that block DNS
    /// traffic except to the tunnel DNS server.
    /// </summary>
    private static async Task EnableMacOsAsync(string tunnelDns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"pass out proto udp from any to {tunnelDns} port 53");
        sb.AppendLine("block out proto udp from any to any port 53");
        sb.AppendLine("block out proto tcp from any to any port 53");

        // ─── Block DoH to well-known resolvers ───
        foreach (var dohServer in DOH_SERVERS)
        {
            sb.AppendLine($"block out proto tcp from any to {dohServer} port 443");
        }

        await File.WriteAllTextAsync(PF_DNS_CONF_PATH, sb.ToString(), Encoding.UTF8);

        // ─── Load DNS pf rules ───
        await RunProcessAsync("pfctl", "-f", PF_DNS_CONF_PATH);

        // ─── Enable packet filter (may already be enabled by kill switch) ───
        try
        {
            await RunProcessAsync("pfctl", "-e");
        }
        catch (SentinelException)
        {
            // pfctl -e exits non-zero if pf is already enabled; safe to ignore
        }
    }

    /// <summary>
    /// Disable DNS leak prevention on macOS by removing the DNS pf config.
    /// </summary>
    private static Task DisableMacOsAsync()
    {
        if (File.Exists(PF_DNS_CONF_PATH))
        {
            File.Delete(PF_DNS_CONF_PATH);
        }

        // We don't disable pf entirely here because the kill switch may still need it.
        // The kill switch's DisableAsync handles pfctl -d when appropriate.
        return Task.CompletedTask;
    }

    // ─── Linux (iptables) ───

    /// <summary>
    /// Enable DNS leak prevention on Linux using iptables rules tagged with the sentinel-vpn-dns comment.
    /// </summary>
    private static async Task EnableLinuxAsync(string tunnelDns)
    {
        // ─── Allow DNS to tunnel DNS server ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-d", tunnelDns, "-p", "udp",
            "--dport", "53", "-j", "ACCEPT",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Block all other outbound UDP port 53 ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-p", "udp",
            "--dport", "53", "-j", "DROP",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Block all outbound TCP port 53 (DNS-over-TLS) ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-p", "tcp",
            "--dport", "53", "-j", "DROP",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Block DoH to well-known resolvers ───
        var dohIps = string.Join(",", DOH_SERVERS);
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-d", dohIps, "-p", "tcp",
            "--dport", "443", "-j", "DROP",
            "-m", "comment", "--comment", IPTABLES_COMMENT);
    }

    /// <summary>
    /// Disable DNS leak prevention on Linux by removing all iptables rules with the sentinel-vpn-dns comment tag.
    /// </summary>
    private static async Task DisableLinuxAsync()
    {
        await DeleteIptablesRulesByCommentAsync("iptables");
    }

    /// <summary>
    /// Delete all OUTPUT chain rules from iptables that contain the sentinel-vpn-dns comment.
    /// Iterates in reverse to avoid index shifting as rules are removed.
    /// </summary>
    private static async Task DeleteIptablesRulesByCommentAsync(string command)
    {
        var output = await RunProcessCaptureAsync(command, "-L", "OUTPUT", "--line-numbers", "-n");

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ruleNumbers = new List<int>();

        foreach (var line in lines)
        {
            if (line.Contains(IPTABLES_COMMENT, StringComparison.Ordinal))
            {
                var parts = line.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[0], out var num))
                {
                    ruleNumbers.Add(num);
                }
            }
        }

        // Delete in reverse order so indices remain valid
        ruleNumbers.Sort();
        ruleNumbers.Reverse();

        foreach (var num in ruleNumbers)
        {
            try
            {
                await RunProcessAsync(command, "-D", "OUTPUT", num.ToString());
            }
            catch (SentinelException)
            {
                // Best effort — rule may already be gone
            }
        }
    }

    // ─── Windows netsh helpers ───

    /// <summary>
    /// Add a Windows Firewall rule via netsh advfirewall firewall add rule.
    /// Uses <see cref="ProcessStartInfo.ArgumentList"/> to prevent command injection.
    /// </summary>
    private static async Task AddNetshRuleAsync(string name, params string[] args)
    {
        var allArgs = new List<string> { "advfirewall", "firewall", "add", "rule", $"name={name}" };
        allArgs.AddRange(args);
        await RunNetshAsync(allArgs.ToArray());
    }

    /// <summary>
    /// Delete a Windows Firewall rule by name. Does not throw if the rule does not exist.
    /// </summary>
    private static async Task DeleteNetshRuleSafeAsync(string name)
    {
        try
        {
            await RunNetshAsync("advfirewall", "firewall", "delete", "rule", $"name={name}");
        }
        catch (SentinelException)
        {
            // Rule may not exist; ignore
        }
    }

    /// <summary>
    /// Run netsh with the given arguments using ArgumentList (injection-safe).
    /// </summary>
    /// <exception cref="SentinelException">Thrown when netsh exits with non-zero code.</exception>
    private static async Task RunNetshAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new SentinelException("PROCESS_START", "Failed to start netsh");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new SentinelException(
                "DNS_LEAK_PREVENTION_FAILED",
                $"netsh exited with code {proc.ExitCode}: {detail.Trim()}"
            );
        }
    }

    // ─── Cross-platform process helpers ───

    /// <summary>
    /// Run an external process and wait for completion.
    /// Uses ArgumentList for safe argument passing (no shell injection).
    /// </summary>
    /// <exception cref="SentinelException">Thrown when the process exits with non-zero code.</exception>
    private static async Task RunProcessAsync(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new SentinelException("PROCESS_START", $"Failed to start {fileName}");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new SentinelException(
                "DNS_LEAK_PREVENTION_FAILED",
                $"{fileName} exited with code {proc.ExitCode}: {detail.Trim()}"
            );
        }
    }

    /// <summary>
    /// Run an external process and capture its stdout output.
    /// </summary>
    private static async Task<string> RunProcessCaptureAsync(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new SentinelException("PROCESS_START", $"Failed to start {fileName}");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout;
    }

    // ─── IDisposable ───

    /// <summary>
    /// Dispose the DNS leak prevention, disabling it if still active.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_enabled)
        {
            try
            {
                Task.Run(() => DisableAsync()).GetAwaiter().GetResult();
            }
            catch
            {
                // Suppress — disposal must not throw
            }
        }

        GC.SuppressFinalize(this);
    }
}
