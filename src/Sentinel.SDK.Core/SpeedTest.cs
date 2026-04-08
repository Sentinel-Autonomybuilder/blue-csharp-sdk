using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Sentinel.SDK.Core;

// ─── Speed Result ───
// Ported from js-sdk/speedtest.js lines 67–78

/// <summary>
/// Result of a speed test measurement.
/// </summary>
/// <param name="Mbps">Measured throughput in megabits per second.</param>
/// <param name="Chunks">Number of chunks downloaded during the test.</param>
/// <param name="Adaptive">Adaptive mode label matching JS: "probe-only", "multi-request", "probe-fallback",
/// "fallback", "rescue", "rescue-fallback", "google-fallback".</param>
/// <param name="TotalBytes">Total bytes downloaded across all chunks.</param>
/// <param name="Seconds">Total elapsed time in seconds.</param>
/// <param name="FallbackHost">If a fallback target was used, its hostname (e.g. "proof.ovh.net").</param>
public record SpeedResult(
    double Mbps,
    int Chunks,
    string Adaptive,
    long TotalBytes = 0,
    double Seconds = 0,
    string? FallbackHost = null
);

// ─── Speed Comparison ───
// Ported from js-sdk/speedtest.js lines 547–566

/// <summary>
/// Comparison between two speed test results.
/// </summary>
/// <param name="Improved">True if download delta is positive.</param>
/// <param name="Degraded">True if download delta is less than -1 Mbps.</param>
/// <param name="DeltaMbps">Absolute speed difference in Mbps (positive = faster, negative = slower).</param>
/// <param name="PercentChange">Percentage change relative to the "before" measurement.</param>
public record SpeedComparison(bool Improved, bool Degraded, double DeltaMbps, double PercentChange);

// ─── Fallback Host Definition ───
// Ported from js-sdk/speedtest.js lines 74–77

/// <summary>
/// A fallback download target for when Cloudflare is unreachable through a tunnel.
/// </summary>
internal record FallbackTarget(string Host, string Path, int Size);

// ─── Speed Test ───

/// <summary>
/// Speed test using Cloudflare's public CDN (no auth required).
/// Measures download throughput either directly or through a VPN tunnel (SOCKS5 proxy).
///
/// 7-level fallback chain (ported from JS SDK):
///   1. Cloudflare CDN with pre-resolved IP (DNS cached 5 min)
///   2. Adaptive: if probe >= 3 Mbps, 5x parallel 1MB downloads
///   3. OVH fallback (proof.ovh.net/files/1Mb.dat)
///   4. Tele2 fallback (speedtest.tele2.net/1MB.zip)
///   5. Rescue mode: 60s timeout, keep-alive, Cloudflare
///   6. Google fallback: google.com page download
///   7. Connected-no-throughput: report 0 Mbps (don't report FAIL)
///
/// Pre-connectivity check for SOCKS5 (google, cloudflare, 1.1.1.1, 2 attempts with 3s delay).
/// DNS pre-resolved before tunnel install. Fresh HttpClient per request (NOT shared handler).
/// </summary>
public static partial class SpeedTest
{
    // ─── Constants ───
    // Ported from js-sdk/speedtest.js lines 58–65

    /// <summary>Cloudflare speed test host — always up, no auth, geographically distributed.</summary>
    private const string CfHost = "speed.cloudflare.com";

    /// <summary>Cloudflare download endpoint.</summary>
    private const string CfDown = $"https://{CfHost}/__down";

    /// <summary>Size of each download chunk in bytes (1 MB).</summary>
    private const int ChunkBytes = 1 * 1024 * 1024; // 1MB per chunk

    /// <summary>Number of sequential requests in phase 2 = 5MB total.</summary>
    private const int ChunkCount = 5;

    /// <summary>Probe size in bytes (1 MB).</summary>
    private const int ProbeBytes = 1 * 1024 * 1024; // 1MB probe

    /// <summary>Threshold in Mbps — below this, skip phase 2.</summary>
    private const double ProbeThresholdMbps = 3.0;

    /// <summary>DNS cache TTL: 5 minutes.</summary>
    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromMinutes(5);

    // ─── Fallback Targets ───
    // Ported from js-sdk/speedtest.js lines 74–77

    private static readonly FallbackTarget[] FallbackUrls =
    {
        new("proof.ovh.net", "/files/1Mb.dat", 1_000_000),
        new("speedtest.tele2.net", "/1MB.zip", 1_000_000),
    };

    // ─── DNS Cache ───
    // Ported from js-sdk/speedtest.js lines 84–88

    private static string? _cachedCfIp;
    private static DateTime _cachedCfTime = DateTime.MinValue;
    private static readonly Dictionary<string, string> _cachedFallbackIps = new();
    private static readonly object _dnsLock = new();

    // ─── Connectivity Check Targets ───
    // Ported from js-sdk/speedtest.js lines 424–428

    private static readonly string[] ConnectivityTargets =
    {
        "https://www.google.com",
        "https://www.cloudflare.com",
        "https://one.one.one.one",
    };

    // ─── Public API ───

    /// <summary>
    /// Flush cached DNS resolutions. Call when switching VPN connections.
    /// </summary>
    // Ported from js-sdk/speedtest.js lines 90–94
    public static void FlushDnsCache()
    {
        lock (_dnsLock)
        {
            _cachedCfIp = null;
            _cachedCfTime = DateTime.MinValue;
            _cachedFallbackIps.Clear();
        }
    }

    /// <summary>
    /// Resolve all speedtest target IPs (Cloudflare + fallbacks).
    /// Used for WireGuard split tunneling — only these IPs get routed through the tunnel.
    /// MUST be called BEFORE installing the tunnel (DNS won't work through a dead tunnel).
    /// </summary>
    // Ported from js-sdk/speedtest.js lines 526–535
    public static async Task<string[]> ResolveSpeedTestIPsAsync()
    {
        await ResolveCfHostAsync().ConfigureAwait(false);
        await ResolveFallbackHostsAsync().ConfigureAwait(false);

        var ips = new List<string>();
        lock (_dnsLock)
        {
            if (_cachedCfIp is not null) ips.Add(_cachedCfIp);
            foreach (var ip in _cachedFallbackIps.Values)
            {
                if (ip is not null) ips.Add(ip);
            }
        }
        return ips.ToArray();
    }

    /// <summary>
    /// Pre-resolve CF hostname so WireGuard DNS issues don't affect speedtests. Call once at startup.
    /// </summary>
    // Ported from js-sdk/speedtest.js line 519 (export of resolveCfHost)
    public static Task ResolveCfHostAsync() => ResolveCfHostInternalAsync();

    // ─── Calculation ───
    // Ported from js-sdk/defaults.js lines 223–227

    /// <summary>
    /// Calculate throughput in megabits per second.
    /// Formula: (totalBytes * 8) / totalSeconds / 1,000,000
    /// Matches JS: bytesToMbps(bytes, seconds)
    /// </summary>
    private static double CalculateMbps(long totalBytes, double totalSeconds)
    {
        if (totalSeconds <= 0) return 0;
        return (totalBytes * 8.0) / totalSeconds / 1_000_000.0;
    }

    // ─── Internal Result Types ───

    /// <summary>Result from a single fresh download.</summary>
    private record FreshDownloadResult(long Bytes, double Seconds);

    /// <summary>Result from multi-request measurement.</summary>
    private record MultiRequestResult(double Mbps, int Chunks, long TotalBytes, double Seconds);
}
