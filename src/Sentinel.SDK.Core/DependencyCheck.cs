using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Sentinel.SDK.Core;

// ─── Preflight Issue Record ───

/// <summary>
/// A single issue found during the pre-flight system check.
/// Ported from js-sdk/preflight.js — matches the JS issue structure exactly.
/// </summary>
/// <param name="Severity">Issue severity: "error", "warning", or "info".</param>
/// <param name="Component">Which component this issue relates to: "wireguard", "v2ray", "protocols", "system".</param>
/// <param name="Message">Human-readable summary of the issue.</param>
/// <param name="Detail">Extended explanation of why this matters.</param>
/// <param name="Action">Actionable fix instructions for the user.</param>
/// <param name="AutoFix">True if calling preflight with autoClean can fix this automatically.</param>
public record PreflightIssue(
    string Severity,
    string Component,
    string Message,
    string Detail,
    string Action,
    bool AutoFix
);

/// <summary>
/// Readiness flags indicating which VPN protocols are available.
/// Ported from js-sdk/preflight.js line 340-344.
/// </summary>
/// <param name="WireGuard">True if WireGuard is installed AND the app is running as admin.</param>
/// <param name="V2Ray">True if V2Ray binary was found on disk or in PATH.</param>
/// <param name="AnyProtocol">True if at least one protocol (WireGuard or V2Ray) is available.</param>
public record ProtocolReadiness(
    bool WireGuard,
    bool V2Ray,
    bool AnyProtocol
);

/// <summary>
/// Complete pre-flight system check result.
/// Ported from js-sdk/preflight.js line 338-351.
/// </summary>
/// <param name="Ok">True if there are zero errors (warnings and info are acceptable).</param>
/// <param name="Ready">Protocol readiness flags.</param>
/// <param name="Issues">All detected issues, ordered by check sequence.</param>
/// <param name="Summary">Human-readable one-line summary.</param>
public record PreflightResult(
    bool Ok,
    ProtocolReadiness Ready,
    List<PreflightIssue> Issues,
    string Summary
);

/// <summary>
/// Result of checking for orphaned WireGuard tunnels.
/// Ported from js-sdk/preflight.js line 25-50.
/// </summary>
/// <param name="Found">True if at least one orphaned tunnel was detected.</param>
/// <param name="Tunnels">Names of orphaned tunnels (e.g. "wgsent0").</param>
public record OrphanedTunnelResult(bool Found, string[] Tunnels);

/// <summary>
/// Result of cleaning orphaned WireGuard tunnels.
/// Ported from js-sdk/preflight.js line 56-69.
/// </summary>
/// <param name="Cleaned">Number of tunnels successfully removed.</param>
/// <param name="Errors">Error messages for tunnels that could not be removed.</param>
public record CleanTunnelResult(int Cleaned, string[] Errors);

/// <summary>
/// Result of checking for orphaned V2Ray processes.
/// Ported from js-sdk/preflight.js line 77-99.
/// </summary>
/// <param name="Found">True if at least one orphaned V2Ray process was detected.</param>
/// <param name="Pids">Process IDs of running V2Ray instances.</param>
public record OrphanedV2RayResult(bool Found, int[] Pids);

/// <summary>
/// A detected conflicting VPN application.
/// Ported from js-sdk/preflight.js line 104-115.
/// </summary>
/// <param name="Name">Human-readable VPN name (e.g. "NordVPN").</param>
/// <param name="Running">True if the VPN process was detected as running.</param>
public record VpnConflict(string Name, bool Running);

/// <summary>
/// A port conflict detection result.
/// Ported from js-sdk/preflight.js line 153-176.
/// </summary>
/// <param name="Port">The port number that was checked.</param>
/// <param name="InUse">True if the port is already in use by another process.</param>
public record PortConflict(int Port, bool InUse);

// ─── Preflight Options ───

/// <summary>
/// Options for the preflight check.
/// Ported from js-sdk/preflight.js line 189-191.
/// </summary>
public class PreflightOptions
{
    /// <summary>When true, automatically clean orphaned tunnels and V2Ray processes.</summary>
    public bool AutoClean { get; set; } = false;

    /// <summary>Explicit path to V2Ray executable. Overrides auto-detection.</summary>
    public string? V2RayExePath { get; set; }
}

// ─── Known VPN Processes ───

/// <summary>
/// Known VPN software entry for conflict detection.
/// Ported from js-sdk/preflight.js line 104-115.
/// </summary>
internal record KnownVpn(string Name, string Process, string Service);

/// <summary>
/// Pre-flight dependency and system verification.
/// Checks ALL 7 categories from the JS SDK before any connection attempt:
/// 1. WireGuard installed + admin rights
/// 2. V2Ray binary found
/// 3. Neither protocol available (critical error)
/// 4. Orphaned WireGuard tunnels (from previous crashes)
/// 5. Orphaned V2Ray processes
/// 6. Conflicting VPN software (10 known names)
/// 7. Port conflicts (10808, 10809, 10810)
///
/// Returns a structured report with severity, message, detail, action, autoFix flag.
/// Ported from js-sdk/preflight.js (353 lines, 7 categories).
/// </summary>
public static class DependencyCheck
{
    // ─── Constants ───

    /// <summary>Timeout for external process calls during checks (milliseconds).</summary>
    private const int ProcessTimeoutMs = 5000;

    /// <summary>Expected V2Ray version string fragment.</summary>
    private const string ExpectedV2RayVersion = "5.2.1";

    /// <summary>
    /// Known VPN processes that conflict with WireGuard routing.
    /// Ported from js-sdk/preflight.js line 104-115.
    /// </summary>
    private static readonly KnownVpn[] KnownVpnProcesses =
    [
        new("NordVPN",     "nordvpn",     "nordvpn-service"),
        new("ExpressVPN",  "expressvpn",  "ExpressVpnService"),
        new("Surfshark",   "surfshark",   "Surfshark"),
        new("ProtonVPN",   "protonvpn",   "ProtonVPN Service"),
        new("Mullvad",     "mullvad-vpn", "mullvad"),
        new("CyberGhost",  "cyberghost",  "CyberGhostVPN"),
        new("PIA",         "pia-client",  "PrivateInternetAccessService"),
        new("Windscribe",  "windscribe",  "WindscribeService"),
        new("TunnelBear",  "tunnelbear",  "TunnelBearService"),
        new("OpenVPN",     "openvpn",     "OpenVPNService"),
    ];

    /// <summary>
    /// Common V2Ray SOCKS5 ports to check for conflicts.
    /// Ported from js-sdk/preflight.js line 154.
    /// </summary>
    private static readonly int[] PortsToCheck = [10808, 10809, 10810];

    // ─── Legacy Verify (backward-compatible) ───

    /// <summary>
    /// Legacy result record for backward compatibility.
    /// New code should use <see cref="Preflight"/> instead.
    /// </summary>
    public record DependencyResult(
        bool Ok,
        bool V2RayFound,
        string? V2RayPath,
        bool WireGuardAvailable,
        string Platform,
        List<string> Errors
    );

    /// <summary>
    /// Legacy verify method — kept for backward compatibility.
    /// Delegates to <see cref="Preflight"/> internally and maps the result.
    /// </summary>
    public static DependencyResult Verify(string? v2rayPath = null)
    {
        var report = Preflight(new PreflightOptions { V2RayExePath = v2rayPath });
        var errors = report.Issues
            .Where(i => i.Severity == "error")
            .Select(i => i.Message)
            .ToList();

        return new DependencyResult(
            Ok: report.Ok,
            V2RayFound: report.Ready.V2Ray,
            V2RayPath: FindV2Ray(v2rayPath),
            WireGuardAvailable: report.Ready.WireGuard,
            Platform: GetPlatformString(),
            Errors: errors
        );
    }

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

    // ─── 3. Conflicting VPN Detection ───
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

    // ─── 4. Port Conflict Detection ───
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

    // ─── Main Preflight Check ───
    // Ported from js-sdk/preflight.js line 193-352

    /// <summary>
    /// Complete pre-flight system check. Run at app startup before any connection.
    /// Checks all 7 categories from the JS SDK:
    /// 1. WireGuard installed + admin rights
    /// 2. V2Ray binary found
    /// 3. Neither protocol available (critical error)
    /// 4. Orphaned WireGuard tunnels (auto-clean optional)
    /// 5. Orphaned V2Ray processes
    /// 6. Conflicting VPN software
    /// 7. Port conflicts
    ///
    /// Ported from js-sdk/preflight.js line 193-352.
    /// </summary>
    /// <param name="opts">Options controlling auto-clean and V2Ray path.</param>
    /// <returns>Structured pre-flight report.</returns>
    public static PreflightResult Preflight(PreflightOptions? opts = null)
    {
        opts ??= new PreflightOptions();
        var issues = new List<PreflightIssue>();

        // ── 1. WireGuard ──
        // Ported from js-sdk/preflight.js line 197-221
        var wgAvailable = CheckWireGuardInstalled();
        var isAdmin = CheckIsAdmin();

        if (!wgAvailable)
        {
            // Ported from js-sdk/preflight.js line 198-209
            string action;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                action = "Download and install from: https://download.wireguard.com/windows-client/wireguard-installer.exe";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                action = "Run: brew install wireguard-tools";
            else
                action = "Run: sudo apt install wireguard (Ubuntu/Debian) or sudo dnf install wireguard-tools (Fedora)";

            issues.Add(new PreflightIssue(
                Severity: "warning",
                Component: "wireguard",
                Message: "WireGuard is not installed.",
                Detail: "WireGuard nodes (faster, more reliable) will not work. V2Ray nodes still work without it.",
                Action: action,
                AutoFix: false
            ));
        }
        else if (!isAdmin)
        {
            // Ported from js-sdk/preflight.js line 210-221
            var action = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Right-click your app \u2192 \"Run as administrator\", or add a manifest with requireAdministrator."
                : "Run with: sudo dotnet your-app.dll";

            issues.Add(new PreflightIssue(
                Severity: "warning",
                Component: "wireguard",
                Message: "WireGuard requires administrator privileges.",
                Detail: "WireGuard is installed but the app is not running as admin. WireGuard nodes will fail. V2Ray nodes still work.",
                Action: action,
                AutoFix: false
            ));
        }

        // ── 2. V2Ray ──
        // Ported from js-sdk/preflight.js line 224-248
        var v2path = FindV2Ray(opts.V2RayExePath);

        if (v2path is null)
        {
            // Ported from js-sdk/preflight.js line 239-248
            issues.Add(new PreflightIssue(
                Severity: "warning",
                Component: "v2ray",
                Message: "V2Ray binary not found.",
                Detail: "V2Ray nodes will not work. WireGuard nodes still work without it.",
                Action: "Place v2ray.exe + geoip.dat + geosite.dat in a bin/ folder, or set V2RayExePath in options.",
                AutoFix: false
            ));
        }

        // ── 3. Neither installed ──
        // Ported from js-sdk/preflight.js line 251-261
        if (!wgAvailable && v2path is null)
        {
            issues.Add(new PreflightIssue(
                Severity: "error",
                Component: "protocols",
                Message: "No VPN protocol available. Cannot connect to any node.",
                Detail: "Neither WireGuard nor V2Ray is installed. You need at least one.",
                Action: "Install WireGuard (recommended) and/or place V2Ray binary in a bin/ folder.",
                AutoFix: false
            ));
        }

        // ── 4. Orphaned WireGuard tunnels ──
        // Ported from js-sdk/preflight.js line 264-291
        var orphanedWg = CheckOrphanedTunnels();
        if (orphanedWg.Found)
        {
            if (opts.AutoClean)
            {
                // Ported from js-sdk/preflight.js line 267-280
                var cleaned = CleanOrphanedTunnels();
                if (cleaned.Errors.Length > 0)
                {
                    var action = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? $"Run as admin: sc stop WireGuardTunnel${orphanedWg.Tunnels[0]} && sc delete WireGuardTunnel${orphanedWg.Tunnels[0]}"
                        : $"Run: sudo wg-quick down {orphanedWg.Tunnels[0]}";

                    issues.Add(new PreflightIssue(
                        Severity: "warning",
                        Component: "wireguard",
                        Message: $"Found {orphanedWg.Tunnels.Length} orphaned tunnel(s), cleaned {cleaned.Cleaned}. {cleaned.Errors[0]}",
                        Detail: "Stale tunnels from a previous crash. Some could not be removed automatically.",
                        Action: action,
                        AutoFix: false
                    ));
                }
                // If all cleaned, no issue to report — ported from js-sdk/preflight.js line 280
            }
            else
            {
                // Ported from js-sdk/preflight.js line 281-290
                issues.Add(new PreflightIssue(
                    Severity: "warning",
                    Component: "wireguard",
                    Message: $"Found {orphanedWg.Tunnels.Length} orphaned WireGuard tunnel(s): {string.Join(", ", orphanedWg.Tunnels)}",
                    Detail: "Left over from a previous crash or app exit. Will block new connections. Set AutoClean = true to fix automatically.",
                    Action: "Call Preflight(new PreflightOptions { AutoClean = true }) or CleanOrphanedTunnels() to remove them.",
                    AutoFix: true
                ));
            }
        }

        // ── 5. Orphaned V2Ray processes ──
        // Ported from js-sdk/preflight.js line 294-304 + node-connect.js killOrphanV2Ray()
        var orphanedV2 = CheckOrphanedV2Ray();
        if (orphanedV2.Found)
        {
            if (opts.AutoClean)
            {
                // Ported from js-sdk/node-connect.js killOrphanV2Ray() — kill all detected v2ray processes
                var killed = 0;
                var killErrors = new List<string>();
                foreach (var pid in orphanedV2.Pids)
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        proc.Kill(true);
                        proc.WaitForExit(3000);
                        killed++;
                    }
                    catch (Exception ex)
                    {
                        killErrors.Add($"PID {pid}: {ex.Message}");
                    }
                }

                if (killErrors.Count > 0)
                {
                    issues.Add(new PreflightIssue(
                        Severity: "warning",
                        Component: "v2ray",
                        Message: $"Killed {killed}/{orphanedV2.Pids.Length} orphaned V2Ray process(es). Errors: {string.Join("; ", killErrors)}",
                        Detail: "Some V2Ray processes could not be terminated.",
                        Action: "Manually kill remaining V2Ray processes.",
                        AutoFix: false
                    ));
                }
                // If all killed successfully, no issue to report
            }
            else
            {
                issues.Add(new PreflightIssue(
                    Severity: "info",
                    Component: "v2ray",
                    Message: $"Found {orphanedV2.Pids.Length} V2Ray process(es) running: PIDs {string.Join(", ", orphanedV2.Pids)}",
                    Detail: "May be from a previous session or another application. These consume SOCKS5 ports.",
                    Action: "If these are unexpected, they will be replaced on next connection. Set AutoClean = true to kill them.",
                    AutoFix: true
                ));
            }
        }

        // ── 6. Conflicting VPN software ──
        // Ported from js-sdk/preflight.js line 307-318
        var vpnCheck = CheckVpnConflicts();
        if (vpnCheck.Length > 0)
        {
            var names = string.Join(", ", vpnCheck.Select(c => c.Name));
            issues.Add(new PreflightIssue(
                Severity: "warning",
                Component: "system",
                Message: $"Other VPN software detected: {names}",
                Detail: "Running multiple VPNs simultaneously can cause routing conflicts, DNS leaks, or connection failures. Disconnect the other VPN before connecting.",
                Action: $"Disconnect {names} before using this app.",
                AutoFix: false
            ));
        }

        // ── 7. Port conflicts ──
        // Ported from js-sdk/preflight.js line 321-332
        var portCheck = CheckPortConflicts();
        if (portCheck.Length > 0)
        {
            var ports = string.Join(", ", portCheck.Select(c => c.Port));
            issues.Add(new PreflightIssue(
                Severity: "info",
                Component: "v2ray",
                Message: $"SOCKS5 port(s) already in use: {ports}",
                Detail: "V2Ray will use a random port to avoid conflicts. This is usually fine.",
                Action: "No action needed \u2014 SDK uses random ports. If you need a specific port, close the process using it.",
                AutoFix: false
            ));
        }

        // ── Summary ──
        // Ported from js-sdk/preflight.js line 335-351
        var errors = issues.Where(i => i.Severity == "error").ToList();
        var warnings = issues.Where(i => i.Severity == "warning").ToList();

        var summary = errors.Count == 0 && warnings.Count == 0
            ? "All checks passed. Ready to connect."
            : errors.Count > 0
                ? $"{errors.Count} error(s), {warnings.Count} warning(s). Fix errors before connecting."
                : $"{warnings.Count} warning(s). Can still connect with available protocols.";

        return new PreflightResult(
            Ok: errors.Count == 0,
            Ready: new ProtocolReadiness(
                WireGuard: wgAvailable && isAdmin,
                V2Ray: v2path is not null,
                AnyProtocol: (wgAvailable && isAdmin) || v2path is not null
            ),
            Issues: issues,
            Summary: summary
        );
    }

    // ─── Private Helpers ───

    /// <summary>
    /// Check if WireGuard is installed on the system.
    /// Ported from js-sdk/wireguard.js (WG_AVAILABLE constant).
    /// </summary>
    private static bool CheckWireGuardInstalled()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return File.Exists(@"C:\Program Files\WireGuard\wireguard.exe")
                || File.Exists(@"C:\Program Files (x86)\WireGuard\wireguard.exe");
        }

        // Linux/macOS: check for wg-quick
        try
        {
            var psi = new ProcessStartInfo("which", "wg-quick")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if the current process has administrator/root privileges.
    /// Ported from js-sdk/wireguard.js line 39 (IS_ADMIN constant).
    /// </summary>
    private static bool CheckIsAdmin()
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

    /// <summary>
    /// Find V2Ray binary on disk or in PATH.
    /// Ported from js-sdk/preflight.js line 224-236 (findV2Ray closure).
    /// </summary>
    /// <param name="customPath">Explicit path from options, checked first.</param>
    /// <returns>Full path to v2ray binary, or null if not found.</returns>
    private static string? FindV2Ray(string? customPath)
    {
        // Ported from js-sdk/preflight.js line 224-236
        // 1. Check explicit path first
        if (customPath is not null && File.Exists(customPath))
            return customPath;

        // 2. Check V2RAY_PATH environment variable
        // Ported from js-sdk/preflight.js line 228: process.env.V2RAY_PATH
        var envPath = Environment.GetEnvironmentVariable("V2RAY_PATH");
        if (envPath is not null && File.Exists(envPath))
            return envPath;

        // 3. Check common relative and absolute paths
        // Ported from js-sdk/preflight.js line 229-230 + original C# candidates
        string[] candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates =
            [
                Path.Combine("bin", "v2ray.exe"),
                Path.Combine("..", "bin", "v2ray.exe"),
                "v2ray.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "v2ray", "v2ray.exe"),
            ];
        }
        else
        {
            candidates =
            [
                Path.Combine("bin", "v2ray"),
                Path.Combine("..", "bin", "v2ray"),
                "/usr/local/bin/v2ray",
                "/usr/bin/v2ray",
                "/snap/bin/v2ray",
            ];
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return Path.GetFullPath(found);

        // 4. Check PATH (where/which)
        // Ported from js-sdk/preflight.js line 232-235
        try
        {
            var cmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "where" : "which";
            var arg = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "v2ray.exe" : "v2ray";
            var output = RunProcess(cmd, [arg], 3000);
            var firstLine = output.Trim().Split('\n')[0].Trim();
            if (!string.IsNullOrEmpty(firstLine) && File.Exists(firstLine))
                return firstLine;
        }
        catch
        {
            // where/which may fail — ported from js-sdk/preflight.js line 235
        }

        return null;
    }

    /// <summary>
    /// Get the platform string for the legacy DependencyResult.
    /// </summary>
    private static string GetPlatformString()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }

    /// <summary>
    /// Run an external process with arguments, capturing stdout.
    /// Throws if the process fails or times out.
    /// </summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="args">Arguments to pass.</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    /// <returns>Standard output from the process.</returns>
    private static string RunProcess(string fileName, string[] args, int timeoutMs)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            proc.StartInfo.ArgumentList.Add(arg);
        }

        proc.Start();

        // Read stdout and stderr asynchronously to prevent deadlock
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(true); } catch { /* best-effort */ }
            throw new TimeoutException($"Process {fileName} timed out after {timeoutMs}ms");
        }

        stdoutTask.Wait(timeoutMs);
        stderrTask.Wait(timeoutMs);

        return stdoutTask.Result;
    }
}
