using System.Runtime.InteropServices;

namespace Sentinel.SDK.Core;

// ─── Orphan Cleanup (WireGuard tunnels + V2Ray processes) ───

public static partial class DependencyCheck
{
    // ─── 1. Orphaned WireGuard Tunnel Detection ───
    // Ported from js-sdk/preflight.js line 25-50

    /// <summary>
    /// Check for orphaned WireGuard tunnels (left over from crashes).
    /// On Windows: queries service control manager for WireGuardTunnel$wgsent* services.
    /// On Linux/macOS: checks for wgsent* network interfaces.
    /// Ported from js-sdk/preflight.js line 25-50.
    /// </summary>
    /// <returns>Detection result with tunnel names.</returns>
    public static OrphanedTunnelResult CheckOrphanedTunnels()
    {
        // Ported from js-sdk/preflight.js line 25-50
        var tunnels = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Ported from js-sdk/preflight.js line 29-36
            try
            {
                var output = RunProcess("sc", ["query", "type=", "service", "state=", "all"],
                    ProcessTimeoutMs);
                // Match WireGuardTunnel$wgsent followed by non-whitespace chars
                // Ported from js-sdk/preflight.js line 31: services.match(/WireGuardTunnel\$wgsent\S*/g)
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    output, @"WireGuardTunnel\$wgsent\S*");
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    // Ported from js-sdk/preflight.js line 34: s.replace('WireGuardTunnel$', '')
                    tunnels.Add(m.Value.Replace("WireGuardTunnel$", ""));
                }
            }
            catch
            {
                // sc query may fail — ported from js-sdk/preflight.js line 36
            }
        }
        else
        {
            // Ported from js-sdk/preflight.js line 39-46 (Linux/macOS)
            try
            {
                var output = RunProcess("ip", ["link", "show"], 3000);
                var matches = System.Text.RegularExpressions.Regex.Matches(output, @"wgsent\d+");
                var uniqueNames = new HashSet<string>();
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    uniqueNames.Add(m.Value);
                }
                tunnels.AddRange(uniqueNames);
            }
            catch
            {
                // Try ifconfig as fallback
                try
                {
                    var output = RunProcess("ifconfig", [], 3000);
                    var matches = System.Text.RegularExpressions.Regex.Matches(output, @"wgsent\d+");
                    var uniqueNames = new HashSet<string>();
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        uniqueNames.Add(m.Value);
                    }
                    tunnels.AddRange(uniqueNames);
                }
                catch
                {
                    // ip/ifconfig may not exist — ported from js-sdk/preflight.js line 46
                }
            }
        }

        return new OrphanedTunnelResult(tunnels.Count > 0, [.. tunnels]);
    }

    /// <summary>
    /// Clean up orphaned WireGuard tunnels.
    /// Uses StateManager.RecoverOrphans internally for the actual cleanup,
    /// then re-checks to verify removal.
    /// Ported from js-sdk/preflight.js line 56-69.
    /// </summary>
    /// <returns>Cleanup result with count of removed tunnels and any errors.</returns>
    public static CleanTunnelResult CleanOrphanedTunnels()
    {
        // Ported from js-sdk/preflight.js line 56-69
        var before = CheckOrphanedTunnels();
        if (!before.Found)
        {
            return new CleanTunnelResult(0, []);
        }

        // Delegate to StateManager.RecoverOrphans for the actual cleanup
        // Ported from js-sdk/preflight.js line 60: emergencyCleanupSync()
        StateManager.RecoverOrphans();

        var after = CheckOrphanedTunnels();
        var cleaned = before.Tunnels.Length - after.Tunnels.Length;
        var errors = after.Found
            ? [$"{after.Tunnels.Length} tunnel(s) could not be removed: {string.Join(", ", after.Tunnels)}"]
            : Array.Empty<string>();

        return new CleanTunnelResult(cleaned, errors);
    }

    // ─── 2. V2Ray Orphan Detection ───
    // Ported from js-sdk/preflight.js line 77-99

    /// <summary>
    /// Check for orphaned V2Ray processes.
    /// On Windows: uses tasklist to find v2ray.exe processes.
    /// On Linux/macOS: uses pgrep to find v2ray processes.
    /// Ported from js-sdk/preflight.js line 77-99.
    /// </summary>
    /// <returns>Detection result with process IDs.</returns>
    public static OrphanedV2RayResult CheckOrphanedV2Ray()
    {
        // Ported from js-sdk/preflight.js line 77-99
        var pids = new List<int>();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Ported from js-sdk/preflight.js line 82-87
                var output = RunProcess("tasklist",
                    ["/FI", "IMAGENAME eq v2ray.exe", "/NH", "/FO", "CSV"],
                    ProcessTimeoutMs);
                var lines = output.Split('\n')
                    .Where(l => l.Contains("v2ray.exe", StringComparison.OrdinalIgnoreCase));
                foreach (var line in lines)
                {
                    // Ported from js-sdk/preflight.js line 85: line.match(/"v2ray\.exe","(\d+)"/)
                    var match = System.Text.RegularExpressions.Regex.Match(
                        line, @"""v2ray\.exe"",""(\d+)""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var pid))
                    {
                        pids.Add(pid);
                    }
                }
            }
            else
            {
                // Ported from js-sdk/preflight.js line 89-93
                try
                {
                    var output = RunProcess("pgrep", ["-x", "v2ray"], 3000);
                    foreach (var line in output.Trim().Split('\n'))
                    {
                        if (int.TryParse(line.Trim(), out var pid))
                        {
                            pids.Add(pid);
                        }
                    }
                }
                catch
                {
                    // pgrep may not exist or return non-zero when no match
                }
            }
        }
        catch
        {
            // process listing may fail — ported from js-sdk/preflight.js line 95
        }

        // Ported from js-sdk/preflight.js line 97-98
        return new OrphanedV2RayResult(pids.Count > 0, [.. pids]);
    }
}
