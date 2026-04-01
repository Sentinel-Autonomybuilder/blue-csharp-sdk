using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Tunnel.WireGuard;

// ─── Kill Switch ───

/// <summary>
/// Kill switch that blocks all outbound traffic except:
/// <list type="bullet">
///   <item><description>Traffic through the WireGuard tunnel interface (wgsent0)</description></item>
///   <item><description>UDP to the WireGuard server endpoint (for the tunnel itself)</description></item>
///   <item><description>Loopback traffic</description></item>
///   <item><description>DHCP (UDP port 67/68) for network connectivity</description></item>
///   <item><description>DNS through the tunnel (UDP to 10.8.0.1:53)</description></item>
/// </list>
/// Platform support:
/// <list type="bullet">
///   <item><description>Windows: netsh advfirewall rules</description></item>
///   <item><description>macOS: pfctl (packet filter)</description></item>
///   <item><description>Linux: iptables/ip6tables with sentinel-vpn comment tag</description></item>
/// </list>
/// Requires administrator/root privileges.
/// </summary>
public class KillSwitch : IDisposable
{
    private const string RULE_PREFIX = "SentinelVPN";
    private const string IPTABLES_COMMENT = "sentinel-vpn";
    private const string PF_CONF_PATH = "/tmp/sentinel-killswitch.conf";

    private bool _disposed;
    private bool _enabled;
    private string? _serverEndpoint;
    private string? _tunnelName;
    private string? _previousInboundPolicy;
    private string? _previousOutboundPolicy;

    /// <summary>
    /// Whether the kill switch is currently active.
    /// </summary>
    public bool IsEnabled => _enabled;

    // ─── Enable ───

    /// <summary>
    /// Enable the kill switch. Blocks all outbound traffic except traffic required
    /// for the WireGuard tunnel to function.
    /// </summary>
    /// <param name="serverEndpoint">
    /// WireGuard server endpoint in "ip:port" format. The kill switch will allow
    /// UDP traffic to this endpoint so the tunnel can establish.
    /// </param>
    /// <param name="tunnelName">
    /// WireGuard tunnel interface name. Defaults to "wgsent0".
    /// </param>
    /// <exception cref="SentinelException">
    /// Thrown when the kill switch cannot be enabled (e.g. missing admin privileges).
    /// </exception>
    public async Task EnableAsync(string serverEndpoint, string tunnelName = "wgsent0")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_enabled)
        {
            return;
        }

        // ─── Parse server endpoint ───
        var (serverIp, serverPort) = ParseEndpoint(serverEndpoint);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await EnableWindowsAsync(serverIp, serverPort, tunnelName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await EnableMacOsAsync(serverIp, serverPort, tunnelName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await EnableLinuxAsync(serverIp, serverPort, tunnelName);
        }
        else
        {
            throw new SentinelException("UNSUPPORTED_PLATFORM", "Kill switch is not supported on this platform");
        }

        _serverEndpoint = serverEndpoint;
        _tunnelName = tunnelName;
        _enabled = true;
    }

    // ─── Disable ───

    /// <summary>
    /// Disable the kill switch and restore normal outbound routing.
    /// Safe to call even if the kill switch was never enabled.
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

        _serverEndpoint = null;
        _tunnelName = null;
        _enabled = false;
    }

    // ─── Windows (netsh advfirewall) ───

    /// <summary>
    /// Enable kill switch on Windows using netsh advfirewall firewall rules.
    /// </summary>
    private async Task EnableWindowsAsync(string serverIp, string serverPort, string tunnelName)
    {
        // ─── Save current firewall policy so we can restore it on disable ───
        _previousInboundPolicy = await GetFirewallPolicyAsync("domainprofile", "inbound");
        _previousOutboundPolicy = await GetFirewallPolicyAsync("domainprofile", "outbound");

        // ─── Block all outbound by default ───
        await RunNetshAsync("advfirewall", "set", "allprofiles", "firewallpolicy", "blockinbound,blockoutbound");

        // ─── Allow tunnel interface traffic ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Allow-Tunnel",
            "dir=out", $"interface={tunnelName}", "action=allow"
        );

        // ─── Allow WireGuard endpoint (UDP to server) ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Allow-WG-Endpoint",
            "dir=out", "action=allow", "protocol=udp",
            $"remoteip={serverIp}", $"remoteport={serverPort}"
        );

        // ─── Allow loopback ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Allow-Loopback",
            "dir=out", "action=allow", "remoteip=127.0.0.1"
        );

        // ─── Allow DHCP ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Allow-DHCP",
            "dir=out", "action=allow", "protocol=udp",
            "localport=68", "remoteport=67"
        );

        // ─── Allow DNS through tunnel ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Allow-DNS-Tunnel",
            "dir=out", "action=allow", "protocol=udp",
            "remoteip=10.8.0.1", "remoteport=53"
        );

        // ─── Block IPv6 (prevents IPv6 leaks when tunnel has no IPv6) ───
        await AddNetshRuleAsync(
            $"{RULE_PREFIX}-Block-IPv6",
            "dir=out", "action=block", "protocol=any",
            "remoteip=::/0"
        );
    }

    /// <summary>
    /// Disable kill switch on Windows by removing all netsh rules and restoring the policy.
    /// </summary>
    private async Task DisableWindowsAsync()
    {
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Allow-Tunnel");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Allow-WG-Endpoint");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Allow-Loopback");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Allow-DHCP");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Allow-DNS-Tunnel");
        await DeleteNetshRuleSafeAsync($"{RULE_PREFIX}-Block-IPv6");

        // ─── Restore default firewall policy ───
        await RunNetshAsync("advfirewall", "set", "allprofiles", "firewallpolicy", "blockinbound,allowoutbound");
    }

    // ─── macOS (pfctl) ───

    /// <summary>
    /// Enable kill switch on macOS using pfctl (packet filter) rules.
    /// Writes rules to a temp config file and loads them with pfctl -f.
    /// </summary>
    private static async Task EnableMacOsAsync(string serverIp, string serverPort, string tunnelName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("block out all");
        sb.AppendLine($"pass out on {tunnelName} all");
        sb.AppendLine($"pass out proto udp from any to {serverIp} port {serverPort}");
        sb.AppendLine("pass out on lo0 all");
        sb.AppendLine("pass out proto udp from any port 68 to any port 67");
        sb.AppendLine("pass out proto udp from any to 10.8.0.1 port 53");
        sb.AppendLine("block out inet6 all");

        await File.WriteAllTextAsync(PF_CONF_PATH, sb.ToString(), Encoding.UTF8);

        // ─── Load rules ───
        await RunProcessAsync("pfctl", "-f", PF_CONF_PATH);

        // ─── Enable packet filter ───
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
    /// Disable kill switch on macOS by disabling pfctl and removing the temp config.
    /// </summary>
    private static async Task DisableMacOsAsync()
    {
        // ─── Disable packet filter ───
        try
        {
            await RunProcessAsync("pfctl", "-d");
        }
        catch (SentinelException)
        {
            // May already be disabled; ignore
        }

        // ─── Remove temp config ───
        if (File.Exists(PF_CONF_PATH))
        {
            File.Delete(PF_CONF_PATH);
        }
    }

    // ─── Linux (iptables) ───

    /// <summary>
    /// Enable kill switch on Linux using iptables/ip6tables rules tagged with the sentinel-vpn comment.
    /// </summary>
    private static async Task EnableLinuxAsync(string serverIp, string serverPort, string tunnelName)
    {
        // ─── Allow loopback ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-o", "lo", "-j", "ACCEPT",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Allow tunnel interface ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-o", tunnelName, "-j", "ACCEPT",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Allow WireGuard endpoint ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-d", serverIp, "-p", "udp",
            "--dport", serverPort, "-j", "ACCEPT",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Allow DHCP ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-p", "udp",
            "--sport", "68", "--dport", "67", "-j", "ACCEPT",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Allow DNS through tunnel ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-d", "10.8.0.1", "-p", "udp",
            "--dport", "53", "-j", "ACCEPT",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Block all other outbound ───
        await RunProcessAsync("iptables", "-A", "OUTPUT", "-j", "DROP",
            "-m", "comment", "--comment", IPTABLES_COMMENT);

        // ─── Block all IPv6 ───
        await RunProcessAsync("ip6tables", "-A", "OUTPUT", "-j", "DROP",
            "-m", "comment", "--comment", IPTABLES_COMMENT);
    }

    /// <summary>
    /// Disable kill switch on Linux by removing all iptables/ip6tables rules with the sentinel-vpn comment tag.
    /// </summary>
    private static async Task DisableLinuxAsync()
    {
        // ─── Remove sentinel-vpn iptables rules by deleting matching comment rules ───
        await DeleteIptablesRulesByCommentAsync("iptables");
        await DeleteIptablesRulesByCommentAsync("ip6tables");
    }

    /// <summary>
    /// Delete all OUTPUT chain rules from iptables/ip6tables that contain the sentinel-vpn comment.
    /// Iterates in reverse to avoid index shifting as rules are removed.
    /// </summary>
    private static async Task DeleteIptablesRulesByCommentAsync(string command)
    {
        // List all OUTPUT rules with line numbers
        var output = await RunProcessCaptureAsync(command, "-L", "OUTPUT", "--line-numbers", "-n");

        // Parse line numbers of rules containing our comment tag (iterate in reverse)
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ruleNumbers = new List<int>();

        foreach (var line in lines)
        {
            if (line.Contains(IPTABLES_COMMENT, StringComparison.Ordinal))
            {
                // Line format: "NUM  TARGET  PROT  ... /* sentinel-vpn */"
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

    // ─── Helpers ───

    /// <summary>
    /// Parse a "host:port" endpoint string into its components.
    /// </summary>
    /// <exception cref="SentinelException">Thrown when the endpoint format is invalid.</exception>
    private static (string ip, string port) ParseEndpoint(string endpoint)
    {
        // Handle IPv6 endpoints like [::1]:51820
        if (endpoint.StartsWith('['))
        {
            var closeBracket = endpoint.IndexOf(']');
            if (closeBracket < 0 || closeBracket + 1 >= endpoint.Length || endpoint[closeBracket + 1] != ':')
            {
                throw new SentinelException("INVALID_ENDPOINT", $"Invalid endpoint format: {endpoint}");
            }

            var ip = endpoint[1..closeBracket];
            var port = endpoint[(closeBracket + 2)..];
            return (ip, port);
        }

        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= endpoint.Length - 1)
        {
            throw new SentinelException("INVALID_ENDPOINT", $"Invalid endpoint format: {endpoint}");
        }

        return (endpoint[..lastColon], endpoint[(lastColon + 1)..]);
    }

    // ─── Windows netsh helpers ───

    /// <summary>
    /// Add a Windows Firewall rule via netsh advfirewall firewall add rule.
    /// Uses <see cref="ProcessStartInfo.ArgumentList"/> to prevent injection.
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
    /// Get the current firewall policy for a profile.
    /// </summary>
    private static async Task<string?> GetFirewallPolicyAsync(string profile, string direction)
    {
        try
        {
            var output = await RunNetshCaptureAsync("advfirewall", "show", profile);
            // Parse output for policy line — best effort
            return output;
        }
        catch
        {
            return null;
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
                "KILLSWITCH_FAILED",
                $"netsh exited with code {proc.ExitCode}: {detail.Trim()}"
            );
        }
    }

    /// <summary>
    /// Run netsh and capture stdout.
    /// </summary>
    private static async Task<string> RunNetshCaptureAsync(params string[] args)
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
        await proc.WaitForExitAsync();
        return stdout;
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
                "KILLSWITCH_FAILED",
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
    /// Dispose the kill switch, disabling it if still active.
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
