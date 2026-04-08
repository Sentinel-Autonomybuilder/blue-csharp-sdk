using System.Diagnostics;
using System.Runtime.InteropServices;

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
///
/// This is a partial class — split across:
///   DependencyCheck.cs         — Core class, records, Preflight orchestrator, helpers
///   DependencyCheck.Tunnels.cs — WireGuard check, V2Ray check, binary detection
///   DependencyCheck.Cleanup.cs — Orphan tunnel cleanup, V2Ray process cleanup
///   DependencyCheck.Network.cs — Port conflicts, VPN conflicts, admin check
/// </summary>
public static partial class DependencyCheck
{
    // ─── Constants ───

    /// <summary>Timeout for external process calls during checks (milliseconds).</summary>
    internal const int ProcessTimeoutMs = 5000;

    /// <summary>Expected V2Ray version string fragment.</summary>
    internal const string ExpectedV2RayVersion = "5.2.1";

    /// <summary>
    /// Known VPN processes that conflict with WireGuard routing.
    /// Ported from js-sdk/preflight.js line 104-115.
    /// </summary>
    internal static readonly KnownVpn[] KnownVpnProcesses =
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
    internal static readonly int[] PortsToCheck = [10808, 10809, 10810];

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

    // ─── Shared Helpers ───

    /// <summary>
    /// Get the platform string for the legacy DependencyResult.
    /// </summary>
    internal static string GetPlatformString()
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
    internal static string RunProcess(string fileName, string[] args, int timeoutMs)
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
