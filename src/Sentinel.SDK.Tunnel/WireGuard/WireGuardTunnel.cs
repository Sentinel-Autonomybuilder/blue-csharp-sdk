using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Tunnel.WireGuard;

// ─── WireGuard Config ───

/// <summary>
/// Configuration for a WireGuard tunnel derived from a Sentinel node handshake.
/// </summary>
/// <param name="ClientPrivateKey">X25519 private key bytes (32 bytes).</param>
/// <param name="AssignedAddresses">Addresses assigned to this client (e.g. ["10.8.0.2/24"]).</param>
/// <param name="ServerPublicKey">Base64-encoded X25519 public key of the server.</param>
/// <param name="ServerEndpoint">Server endpoint in "ip:port" format.</param>
/// <param name="FullTunnel">When true, AllowedIPs = 0.0.0.0/0, ::/0 (route all traffic).</param>
/// <param name="SplitIPs">Specific IPs/CIDRs for split-tunnel mode (ignored when FullTunnel is true).</param>
/// <param name="EnableKillSwitch">When true, a kill switch blocks all non-tunnel traffic while VPN is active.</param>
/// <param name="EnableDnsLeakPrevention">When true, DNS leak prevention forces all DNS through the tunnel.</param>
public record WireGuardConfig(
    byte[] ClientPrivateKey,
    string[] AssignedAddresses,
    string ServerPublicKey,
    string ServerEndpoint,
    bool FullTunnel = true,
    string[]? SplitIPs = null,
    bool EnableKillSwitch = false,
    bool EnableDnsLeakPrevention = false
)
{
    /// <summary>MTU for the WireGuard interface. Default: 1420.</summary>
    public int Mtu { get; init; } = 1420;

    /// <summary>DNS servers for the tunnel. Default: Handshake DNS (censorship-resistant).</summary>
    public string Dns { get; init; } = Constants.DnsPresets.Resolve();

    /// <summary>PersistentKeepalive interval in seconds. Default: 30.</summary>
    public int Keepalive { get; init; } = 30;
}

// ─── WireGuard Tunnel ───

/// <summary>
/// Manages a WireGuard tunnel on Windows, macOS, and Linux.
/// <list type="bullet">
///   <item><description>Windows: wireguard.exe /installtunnelservice and /uninstalltunnelservice</description></item>
///   <item><description>macOS/Linux: wg-quick up/down</description></item>
/// </list>
/// Requires administrator/root privileges for tunnel installation/removal.
/// </summary>
public class WireGuardTunnel : IDisposable, IAsyncDisposable
{
    private static readonly string CONFIG_DIR = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? @"C:\ProgramData\sentinel-wg"
        : "/tmp/sentinel-wg";

    private const string DEFAULT_TUNNEL_NAME = "wgsent0";
    private static readonly string DEFAULT_DNS = Constants.DnsPresets.Resolve();
    private const int DEFAULT_MTU = 1420;
    private const int KEEPALIVE_SECONDS = 30;
    private const int SERVICE_WAIT_TIMEOUT_MS = 15_000;
    private const int SERVICE_POLL_INTERVAL_MS = 500;

    private bool _disposed;
    private bool _installed;
    private readonly KillSwitch _killSwitch = new();
    private readonly DnsLeakPrevention _dnsLeakPrevention = new();

    /// <summary>
    /// Tunnel interface name (default: "wgsent0").
    /// </summary>
    public string TunnelName { get; }

    /// <summary>
    /// Whether the tunnel service is currently active.
    /// </summary>
    public bool IsActive => CheckServiceActive();

    /// <summary>
    /// Whether the kill switch is currently active (blocking non-tunnel traffic).
    /// </summary>
    public bool IsKillSwitchEnabled => _killSwitch.IsEnabled;

    /// <summary>
    /// Whether DNS leak prevention is currently active.
    /// </summary>
    public bool IsDnsLeakPreventionEnabled => _dnsLeakPrevention.IsEnabled;

    /// <summary>
    /// Creates a new WireGuard tunnel manager.
    /// </summary>
    /// <param name="tunnelName">Tunnel interface name. Defaults to "wgsent0".</param>
    public WireGuardTunnel(string tunnelName = DEFAULT_TUNNEL_NAME)
    {
        TunnelName = tunnelName;
    }

    /// <summary>
    /// Install and activate a WireGuard tunnel from the given configuration.
    /// Writes the .conf file, sets permissions, and installs the tunnel service.
    /// </summary>
    /// <param name="config">WireGuard configuration from a Sentinel node handshake.</param>
    /// <exception cref="SentinelException">Thrown when not running as admin or tunnel installation fails.</exception>
    public async Task InstallAsync(WireGuardConfig config, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureAdministrator();

        var confPath = Path.Combine(CONFIG_DIR, $"{TunnelName}.conf");

        // ─── Clean up any stale tunnel before installing ───
        try { await UninstallAsync(); } catch { }
        try { if (File.Exists(confPath)) File.Delete(confPath); } catch { }

        // ─── Write config file ───
        Directory.CreateDirectory(CONFIG_DIR);
        var confContent = BuildConfFile(config);
        await File.WriteAllTextAsync(confPath, confContent, new UTF8Encoding(false), ct);

        // ─── Set file permissions (match JS SDK: SYSTEM + current user full control) ───
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var username = Environment.UserName;
            await RunProcessAsync("icacls", CONFIG_DIR, "/inheritance:r", "/grant:r", $"{username}:F", "/grant:r", "SYSTEM:F");
            await RunProcessAsync("icacls", confPath, "/inheritance:r", "/grant:r", $"{username}:F", "/grant:r", "SYSTEM:F");
        }
        else
        {
            File.SetUnixFileMode(confPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // ─── Install tunnel service ───
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await RunProcessAsync("wireguard.exe", "/installtunnelservice", confPath);
        }
        else
        {
            await RunProcessAsync("wg-quick", "up", confPath);
        }
        _installed = true;

        // ─── Wait for tunnel to become active ───
        var deadline = Environment.TickCount64 + SERVICE_WAIT_TIMEOUT_MS;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (CheckServiceActive())
            {
                break;
            }
            await Task.Delay(SERVICE_POLL_INTERVAL_MS, ct);
        }

        if (!CheckServiceActive())
        {
            throw new SentinelException(
                "WIREGUARD_TIMEOUT",
                $"WireGuard tunnel '{TunnelName}' did not become active within {SERVICE_WAIT_TIMEOUT_MS / 1000}s"
            );
        }

        // ─── Enable kill switch (blocks all non-tunnel traffic) ───
        if (config.EnableKillSwitch)
        {
            try
            {
                await _killSwitch.EnableAsync(config.ServerEndpoint, TunnelName);
            }
            catch (SentinelException ex)
            {
                // Tunnel is up but kill switch failed — tear down tunnel to avoid unprotected traffic
                await UninstallAsync(ct);
                throw new SentinelException(
                    "KILLSWITCH_ENABLE_FAILED",
                    $"Failed to enable kill switch, tunnel removed for safety: {ex.Message}"
                );
            }
        }

        // ─── Enable DNS leak prevention ───
        if (config.EnableDnsLeakPrevention)
        {
            try
            {
                await _dnsLeakPrevention.EnableAsync();
            }
            catch (SentinelException ex)
            {
                // Tunnel is up but DNS leak prevention failed — tear down for safety
                await UninstallAsync(ct);
                throw new SentinelException(
                    "DNS_LEAK_PREVENTION_ENABLE_FAILED",
                    $"Failed to enable DNS leak prevention, tunnel removed for safety: {ex.Message}"
                );
            }
        }
    }

    // ─── Verify-Before-Capture (Two-Phase Install) ───

    /// <summary>
    /// Safe verification IPs — route only Cloudflare endpoints during verification phase.
    /// These are used as AllowedIPs before we know the tunnel actually works.
    /// </summary>
    private static readonly string[] VerifyIPs = ["1.1.1.1/32", "1.0.0.1/32"];

    /// <summary>
    /// HTTP targets to probe during verification phase (must match VerifyIPs).
    /// </summary>
    private static readonly string[] VerifyTargets = ["https://1.1.1.1", "https://1.0.0.1"];

    /// <summary>
    /// Install a WireGuard tunnel with verify-before-capture: for full tunnel mode, first installs
    /// with safe split IPs (1.1.1.1/32, 1.0.0.1/32), verifies traffic flows, then reinstalls
    /// with 0.0.0.0/0. This prevents killing the user's internet if the node is broken.
    /// Translated line-by-line from JS SDK node-connect.js setupWireGuard() v28.
    /// </summary>
    /// <param name="config">WireGuard configuration from a Sentinel node handshake.</param>
    /// <param name="onProgress">Optional progress callback (step, detail).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SentinelException">Thrown when tunnel installation or verification fails.</exception>
    public async Task VerifyAndInstallAsync(
        WireGuardConfig config,
        Action<string, string>? onProgress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureAdministrator();

        // Determine if we need the two-phase approach:
        // fullTunnel=true AND no explicit splitIPs → install with safe IPs first
        var needsFullTunnelSwitch = config.FullTunnel
            && (config.SplitIPs is null || config.SplitIPs.Length == 0);

        if (!needsFullTunnelSwitch)
        {
            // No two-phase needed — explicit splitIPs or split tunnel mode
            // Just do a normal install
            await InstallAsync(config, ct);
            return;
        }

        // ─── Phase 1: Install with safe split IPs (1.1.1.1/32, 1.0.0.1/32) ───
        // User's internet is NOT captured — only Cloudflare IPs are routed through tunnel.
        onProgress?.Invoke("tunnel", "Full tunnel mode — installing with safe verify IPs first...");

        var verifyConfig = config with
        {
            FullTunnel = false,
            SplitIPs = VerifyIPs,
        };

        // Exponential retry for install — node may not have registered peer yet.
        // Delays: 1.5s, 1.5s, 2s (total budget: 5s, matches JS SDK).
        var installDelays = new[] { 1500, 1500, 2000 };
        var tunnelInstalled = false;

        for (var i = 0; i < installDelays.Length; i++)
        {
            await Task.Delay(installDelays[i], ct);
            ct.ThrowIfCancellationRequested();

            try
            {
                onProgress?.Invoke("tunnel", $"Installing WireGuard tunnel (attempt {i + 1}/{installDelays.Length})...");
                await InstallAsync(verifyConfig, ct);
                tunnelInstalled = true;
                break;
            }
            catch (SentinelException) when (i < installDelays.Length - 1)
            {
                onProgress?.Invoke("tunnel", $"Tunnel install attempt {i + 1} failed, retrying...");
                // Continue to next attempt
            }
        }

        if (!tunnelInstalled)
        {
            throw new TunnelException(
                ErrorCodes.TunnelSetupFailed,
                "WireGuard tunnel failed to install after all retry attempts"
            );
        }

        // ─── Phase 1.5: Verify connectivity through the safe tunnel ───
        // If the tunnel is broken, we find out NOW — user's internet is still fine.
        onProgress?.Invoke("verify", "Verifying tunnel connectivity...");

        var tunnelWorks = await VerifyConnectivityAsync(VerifyTargets, maxAttempts: 1, ct: ct);
        if (!tunnelWorks)
        {
            onProgress?.Invoke("verify", "WireGuard tunnel installed but no traffic flows. Tearing down immediately...");
            try { await UninstallAsync(ct); } catch { }
            throw new TunnelException(
                ErrorCodes.WgNoConnectivity,
                "WireGuard tunnel installed (service RUNNING) but connectivity check failed — " +
                "no traffic flows through the tunnel. The node may have rejected the peer or the session may be stale."
            );
        }

        // ─── Phase 2: Switch from safe split IPs to full tunnel (0.0.0.0/0) ───
        // Tunnel is verified working — now capture all traffic.
        onProgress?.Invoke("tunnel", "Verified! Switching to full tunnel (0.0.0.0/0)...");

        // InstallAsync handles its own cleanup of the previous tunnel (UninstallAsync at top).
        await InstallAsync(config, ct);

        onProgress?.Invoke("verify", "WireGuard connected and verified!");
    }

    /// <summary>
    /// Verify that traffic actually flows through the WireGuard tunnel.
    /// Tries HTTP GET requests to reliable targets. For full tunnel (0.0.0.0/0) all traffic
    /// goes through it. For split tunnel, only the routed IPs are testable.
    /// Translated from JS SDK verifyWgConnectivity().
    /// </summary>
    /// <param name="targets">HTTP(S) URLs to probe. Default: Cloudflare endpoints.</param>
    /// <param name="maxAttempts">Number of retry rounds (default 1). 2s delay between rounds.</param>
    /// <param name="timeoutMs">Per-request timeout in milliseconds (default 5000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if any target responds successfully.</returns>
    public static async Task<bool> VerifyConnectivityAsync(
        string[]? targets = null,
        int maxAttempts = 1,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        targets ??= ["https://1.1.1.1", "https://www.cloudflare.com"];

        using var handler = new HttpClientHandler
        {
            // Accept any status — we just need a response
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(2000, ct);

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var response = await http.GetAsync(target, ct);
                    return true; // Any response = tunnel works
                }
                catch
                {
                    // Expected: target may be unreachable through tunnel
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Remove the tunnel service and clean up the configuration file.
    /// </summary>
    /// <exception cref="SentinelException">Thrown when uninstall fails.</exception>
    public async Task UninstallAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_installed)
        {
            return;
        }

        // ─── Disable kill switch and DNS leak prevention before removing tunnel ───
        if (_killSwitch.IsEnabled)
        {
            try
            {
                await _killSwitch.DisableAsync();
            }
            catch (SentinelException)
            {
                // Best effort — continue with uninstall
            }
        }

        if (_dnsLeakPrevention.IsEnabled)
        {
            try
            {
                await _dnsLeakPrevention.DisableAsync();
            }
            catch (SentinelException)
            {
                // Best effort — continue with uninstall
            }
        }

        // ─── Uninstall tunnel service ───
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await RunProcessAsync("wireguard.exe", "/uninstalltunnelservice", TunnelName);
            }
            else
            {
                await RunProcessAsync("wg-quick", "down", TunnelName);
            }
        }
        catch (SentinelException)
        {
            // Service may already be removed; log but don't throw
        }

        _installed = false;

        // ─── Remove config file ───
        var confPath = Path.Combine(CONFIG_DIR, $"{TunnelName}.conf");
        if (File.Exists(confPath))
        {
            File.Delete(confPath);
        }
    }

    /// <summary>
    /// Build the WireGuard .conf file content from the given configuration.
    /// </summary>
    private static string BuildConfFile(WireGuardConfig config)
    {
        var privateKeyBase64 = Convert.ToBase64String(config.ClientPrivateKey);
        var addresses = string.Join(", ", config.AssignedAddresses);

        var allowedIPs = config.FullTunnel
            ? "0.0.0.0/0, ::/0"
            : string.Join(", ", (config.SplitIPs ?? []).Select(ip => ip.Contains('/') ? ip : $"{ip}/32"));

        if (!config.FullTunnel && (config.SplitIPs is null || config.SplitIPs.Length == 0))
        {
            throw new SentinelException(
                "WIREGUARD_CONFIG",
                "Split tunnel mode requires at least one IP in SplitIPs"
            );
        }

        // Use LF line endings (\n) — WireGuard on Windows handles LF but may choke on CRLF
        var sb = new StringBuilder();
        sb.Append("[Interface]\n");
        sb.Append($"PrivateKey = {privateKeyBase64}\n");
        sb.Append($"Address = {addresses}\n");
        sb.Append($"MTU = {config.Mtu}\n");
        // Only set DNS for full tunnel; split tunnel uses system DNS (safer, matches JS SDK)
        if (config.FullTunnel)
        {
            sb.Append($"DNS = {config.Dns}\n");
        }
        sb.Append('\n');
        sb.Append("[Peer]\n");
        sb.Append($"PublicKey = {config.ServerPublicKey}\n");
        sb.Append($"Endpoint = {config.ServerEndpoint}\n");
        sb.Append($"AllowedIPs = {allowedIPs}\n");
        sb.Append($"PersistentKeepalive = {config.Keepalive}\n");

        return sb.ToString();
    }

    /// <summary>
    /// Check if the WireGuard tunnel service is running.
    /// <list type="bullet">
    ///   <item><description>Windows: sc query for the WireGuardTunnel$ service</description></item>
    ///   <item><description>macOS/Linux: wg show to check interface status (exit 0 = active)</description></item>
    /// </list>
    /// </summary>
    private bool CheckServiceActive()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("query");
                psi.ArgumentList.Add($"WireGuardTunnel${TunnelName}");

                using var proc = Process.Start(psi);
                if (proc is null) return false;

                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wg",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("show");
                psi.ArgumentList.Add(TunnelName);

                using var proc = Process.Start(psi);
                if (proc is null) return false;

                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                return proc.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensure the current process is running with administrator/root privileges.
    /// <list type="bullet">
    ///   <item><description>Windows: checks WindowsPrincipal for Administrator role</description></item>
    ///   <item><description>macOS/Linux: checks Environment.IsPrivilegedProcess (uid 0)</description></item>
    /// </list>
    /// </summary>
    /// <exception cref="SentinelException">Thrown when not running as administrator/root.</exception>
    private static void EnsureAdministrator()
    {
        bool isAdmin;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        else
        {
            isAdmin = Environment.IsPrivilegedProcess;
        }

        if (!isAdmin)
        {
            throw new SentinelException(
                "ADMIN_REQUIRED",
                "WireGuard tunnel management requires administrator/root privileges"
            );
        }
    }

    /// <summary>
    /// Run an external process and wait for completion.
    /// Uses ArgumentList for safe argument passing (no shell injection via string interpolation).
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
                "PROCESS_FAILED",
                $"{fileName} exited with code {proc.ExitCode}: {detail.Trim()}"
            );
        }
    }

    // ─── IAsyncDisposable ───

    /// <summary>
    /// Asynchronously dispose the tunnel manager, uninstalling the tunnel if still active.
    /// Prefer this over <see cref="Dispose"/> to avoid sync-over-async deadlocks.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_installed)
            {
                try
                {
                    await UninstallAsync();
                }
                catch
                {
                    // Suppress — disposal must not throw
                }
            }

            _killSwitch.Dispose();
            _dnsLeakPrevention.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ─── IDisposable ───

    /// <summary>
    /// Dispose the tunnel manager, uninstalling the tunnel if still active.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_installed)
        {
            // Fire-and-forget cleanup; best effort.
            // Wrap in Task.Run to avoid sync-over-async deadlock when
            // Dispose is called from a UI thread with a SynchronizationContext.
            try
            {
                Task.Run(() => UninstallAsync()).GetAwaiter().GetResult();
            }
            catch
            {
                // Suppress — disposal must not throw
            }
        }

        _killSwitch.Dispose();
        _dnsLeakPrevention.Dispose();

        GC.SuppressFinalize(this);
    }
}
