using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Sentinel.SDK.Core;

// ─── Network Checks (VPN conflicts, port conflicts, admin check) ───

public static partial class DependencyCheck
{
    // ─── Admin / Privilege Check ───

    /// <summary>
    /// Check if the current process has administrator/root privileges.
    /// Ported from js-sdk/wireguard.js line 39 (IS_ADMIN constant).
    /// </summary>
    internal static bool CheckIsAdmin()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // Linux/macOS: check effective UID
        try
        {
            return Environment.IsPrivilegedProcess;
        }
        catch
        {
            return false;
        }
    }

    // ─── Conflicting VPN Detection ───
    // Ported from js-sdk/preflight.js line 121-145

    /// <summary>
    /// Check for running VPN software that may conflict with WireGuard routing.
    /// Scans the running process list for 10 known VPN applications.
    /// Ported from js-sdk/preflight.js line 121-145.
    /// </summary>
    /// <returns>List of detected conflicting VPNs.</returns>
    public static VpnConflict[] CheckVpnConflicts()
    {
        // Ported from js-sdk/preflight.js line 121-145
        var conflicts = new List<VpnConflict>();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Ported from js-sdk/preflight.js line 125-132
                var tasks = RunProcess("tasklist", ["/NH", "/FO", "CSV"],
                    ProcessTimeoutMs).ToLowerInvariant();
                foreach (var vpn in KnownVpnProcesses)
                {
                    // Ported from js-sdk/preflight.js line 128
                    if (tasks.Contains(vpn.Process.ToLowerInvariant()))
                    {
                        conflicts.Add(new VpnConflict(vpn.Name, true));
                    }
                }
            }
            else
            {
                // Ported from js-sdk/preflight.js line 134-141
                string ps;
                try
                {
                    ps = RunProcess("ps", ["aux"], 3000).ToLowerInvariant();
                }
                catch
                {
                    // Fallback: ps -ef
                    ps = RunProcess("ps", ["-ef"], 3000).ToLowerInvariant();
                }

                foreach (var vpn in KnownVpnProcesses)
                {
                    if (ps.Contains(vpn.Process.ToLowerInvariant()))
                    {
                        conflicts.Add(new VpnConflict(vpn.Name, true));
                    }
                }
            }
        }
        catch
        {
            // tasklist/ps may fail — ported from js-sdk/preflight.js line 132
        }

        return [.. conflicts];
    }

    // ─── Port Conflict Detection ───
    // Ported from js-sdk/preflight.js line 153-176

    /// <summary>
    /// Check if common V2Ray SOCKS5 ports are in use.
    /// Checks ports 10808, 10809, 10810.
    /// Ported from js-sdk/preflight.js line 153-176.
    /// </summary>
    /// <returns>List of ports that are currently in use.</returns>
    public static PortConflict[] CheckPortConflicts()
    {
        // Ported from js-sdk/preflight.js line 153-176
        var conflicts = new List<PortConflict>();

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Ported from js-sdk/preflight.js line 159-163
                var netstat = RunProcess("netstat", ["-ano"], ProcessTimeoutMs);
                foreach (var port in PortsToCheck)
                {
                    // Ported from js-sdk/preflight.js line 161: netstat.includes(`:${port} `)
                    if (netstat.Contains($":{port} "))
                    {
                        conflicts.Add(new PortConflict(port, true));
                    }
                }
            }
            else
            {
                // Ported from js-sdk/preflight.js line 166-171
                foreach (var port in PortsToCheck)
                {
                    try
                    {
                        RunProcess("lsof", ["-i", $":{port}", "-t"], 3000);
                        // If lsof succeeds (exit 0), port is in use
                        conflicts.Add(new PortConflict(port, true));
                    }
                    catch
                    {
                        // lsof exits non-zero when port is free — ported from js-sdk/preflight.js line 170
                    }
                }
            }
        }
        catch
        {
            // netstat may fail — ported from js-sdk/preflight.js line 173
        }

        return [.. conflicts];
    }
}
