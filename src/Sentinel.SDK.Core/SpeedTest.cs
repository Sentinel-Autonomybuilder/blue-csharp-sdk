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
public static class SpeedTest
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

    /// <summary>
    /// Run a speed test directly (measures raw internet speed without any proxy).
    /// All traffic goes through the active network interface (WireGuard tunnel when up).
    /// Pre-resolves CF hostname to avoid DNS failures behind WireGuard tunnels.
    ///
    /// Fallback chain: CF IP -> CF hostname -> OVH/Tele2 fallback -> rescue (60s) -> throw.
    /// Adaptive: probe 1MB, if >= 3 Mbps then 5x1MB multi-request.
    /// </summary>
    // Ported from js-sdk/speedtest.js lines 323–378
    public static async Task<SpeedResult> DirectAsync(CancellationToken ct = default)
    {
        // Ported from js-sdk/speedtest.js lines 324–325
        await ResolveCfHostInternalAsync().ConfigureAwait(false);
        await ResolveFallbackHostsAsync().ConfigureAwait(false);

        // Ported from js-sdk/speedtest.js lines 328–335
        string CfUrl(int bytes)
        {
            string? ip;
            lock (_dnsLock) { ip = _cachedCfIp; }
            return ip is not null
                ? $"https://{ip}/__down?bytes={bytes}"
                : $"{CfDown}?bytes={bytes}";
        }

        string CfUrlHostname(int bytes) => $"{CfDown}?bytes={bytes}";

        // Phase 1: Quick 1MB single probe
        // Ported from js-sdk/speedtest.js lines 338–354
        FreshDownloadResult? probe = null;
        try
        {
            probe = await FreshDownloadAsync(CfUrl(ProbeBytes), ProbeBytes, hostOverride: CfHost, timeoutMs: 30_000, ct: ct).ConfigureAwait(false);
        }
        catch
        {
            // IP failed, try hostname
            // Ported from js-sdk/speedtest.js lines 343–344
            try
            {
                probe = await FreshDownloadAsync(CfUrlHostname(ProbeBytes), ProbeBytes, timeoutMs: 30_000, ct: ct).ConfigureAwait(false);
            }
            catch
            {
                // Cloudflare unreachable with fresh connections — try fallback targets
                // Ported from js-sdk/speedtest.js lines 347–348
                var fb = await FallbackMeasureAsync(ct: ct).ConfigureAwait(false);
                if (fb is not null) return fb;

                // Last resort: rescue download with keep-alive agent + 60s timeout
                // Ported from js-sdk/speedtest.js lines 350–351
                var rescue = await RescueDownloadAsync(ct).ConfigureAwait(false);
                if (rescue is not null) return rescue;

                // Ported from js-sdk/speedtest.js line 352
                throw new TunnelException(ErrorCodes.TunnelSetupFailed, "Speed test failed (CF and all fallbacks unreachable)");
            }
        }

        // Ported from js-sdk/speedtest.js line 356
        var probeMbps = CalculateMbps(probe.Bytes, probe.Seconds);
        probeMbps = Math.Round(probeMbps, 2);

        // If probe speed is low (< 3 Mbps), don't waste time on full test
        // Ported from js-sdk/speedtest.js lines 359–361
        if (probeMbps < ProbeThresholdMbps)
        {
            return new SpeedResult(Mbps: probeMbps, Chunks: 1, Adaptive: "probe-only",
                TotalBytes: probe.Bytes, Seconds: Math.Round(probe.Seconds, 3));
        }

        // Phase 2: Multi-request test — 5 x 1MB sequential downloads
        // Ported from js-sdk/speedtest.js lines 364–377
        var url = CfUrl(ChunkBytes);
        try
        {
            var full = await MultiRequestMeasureAsync(url, ChunkBytes, ChunkCount, hostOverride: CfHost, ct: ct).ConfigureAwait(false);
            return new SpeedResult(Mbps: full.Mbps, Chunks: full.Chunks, Adaptive: "multi-request",
                TotalBytes: full.TotalBytes, Seconds: Math.Round(full.Seconds, 3));
        }
        catch
        {
            // Try hostname fallback
            // Ported from js-sdk/speedtest.js lines 370–371
            try
            {
                var full = await MultiRequestMeasureAsync(CfUrlHostname(ChunkBytes), ChunkBytes, ChunkCount, ct: ct).ConfigureAwait(false);
                return new SpeedResult(Mbps: full.Mbps, Chunks: full.Chunks, Adaptive: "multi-request",
                    TotalBytes: full.TotalBytes, Seconds: Math.Round(full.Seconds, 3));
            }
            catch
            {
                // Full test failed but probe worked — return probe result
                // Ported from js-sdk/speedtest.js line 375
                return new SpeedResult(Mbps: probeMbps, Chunks: 1, Adaptive: "probe-fallback",
                    TotalBytes: probe.Bytes, Seconds: Math.Round(probe.Seconds, 3));
            }
        }
    }

    /// <summary>
    /// Run a speed test through a SOCKS5 proxy (measures VPN tunnel speed).
    /// Routes through the SOCKS5 proxy at localhost:proxyPort.
    ///
    /// Phase 0: Connectivity check (google/cloudflare/1.1.1.1, 2 attempts with 3s delay).
    /// Phase 1: 1MB probe — CF -> OVH/Tele2 fallback -> CF 60s rescue -> google fallback.
    /// Phase 2: 5x1MB multi-request if probe >= 3 Mbps.
    /// Fresh HttpClient per request.
    /// </summary>
    // Ported from js-sdk/speedtest.js lines 391–516
    public static async Task<SpeedResult> ViaSocks5Async(
        int socksPort,
        string? user = null,
        string? pass = null,
        CancellationToken ct = default)
    {
        // Ported from js-sdk/speedtest.js lines 393–396
        WebProxy MakeProxy()
        {
            var proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}");
            if (user is not null && pass is not null)
            {
                proxy.Credentials = new NetworkCredential(user, pass);
            }
            return proxy;
        }

        // Ported from js-sdk/speedtest.js lines 398–415
        // Fresh HttpClient per request — V2Ray SOCKS5 can fail with connection reuse
        async Task<FreshDownloadResult> Measure(string url, int limitBytes, int timeoutMs = 30_000)
        {
            using var handler = new HttpClientHandler
            {
                Proxy = MakeProxy(),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // rejectUnauthorized: false
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            sw.Stop();

            var downloaded = data.Length;
            var elapsed = sw.Elapsed.TotalSeconds;
            if (elapsed <= 0 || downloaded == 0)
                throw new TunnelException(ErrorCodes.TunnelSetupFailed, "Speed test: no data received");
            return new FreshDownloadResult(downloaded, elapsed);
        }

        // Phase 0: Quick connectivity check — verify the SOCKS5 tunnel can reach the internet at all.
        // Without this, nodes with working tunnels get marked as failures just because speedtest
        // targets (CF, OVH, Tele2) are blocked by the node's ISP/firewall.
        //
        // Retry once: V2Ray SOCKS5 binding is async and variable. Even after waiting for the port
        // to accept TCP connections, the proxy pipeline may not be fully ready. A single retry
        // after a 3s pause catches slow-starting nodes that would otherwise be false failures.
        // Ported from js-sdk/speedtest.js lines 424–443
        var tunnelConnected = false;
        for (var attempt = 0; attempt < 2 && !tunnelConnected; attempt++)
        {
            if (attempt > 0) await Task.Delay(3000, ct).ConfigureAwait(false);

            foreach (var target in ConnectivityTargets)
            {
                try
                {
                    using var handler = new HttpClientHandler
                    {
                        Proxy = MakeProxy(),
                        UseProxy = true,
                        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                        AllowAutoRedirect = true,
                        MaxAutomaticRedirections = 2,
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    var resp = await client.GetAsync(target, ct).ConfigureAwait(false);
                    // validateStatus: () => true — accept any status
                    tunnelConnected = true;
                    break;
                }
                catch
                {
                    // try next target
                }
            }
        }

        if (!tunnelConnected)
        {
            // Ported from js-sdk/speedtest.js line 442
            throw new TunnelException(ErrorCodes.WgNoConnectivity,
                "SOCKS5 tunnel has no internet connectivity (google/cloudflare/1.1.1.1 all unreachable after 2 attempts)");
        }

        // Phase 1: 1MB single probe — try CF first, then fallback targets, then rescue with 60s timeout
        // Ported from js-sdk/speedtest.js lines 446–484
        FreshDownloadResult? probe = null;
        var probeSource = "cloudflare";

        try
        {
            // Ported from js-sdk/speedtest.js line 449
            probe = await Measure($"{CfDown}?bytes={ProbeBytes}", ProbeBytes).ConfigureAwait(false);
        }
        catch
        {
            // CF download failed via SOCKS5 — try fallback download targets
            // Ported from js-sdk/speedtest.js lines 452–460
            var fallbackOk = false;
            foreach (var fb in FallbackUrls)
            {
                try
                {
                    probe = await Measure($"https://{fb.Host}{fb.Path}", fb.Size).ConfigureAwait(false);
                    probeSource = fb.Host;
                    fallbackOk = true;
                    break;
                }
                catch
                {
                    // try next fallback
                }
            }

            if (!fallbackOk)
            {
                // Last resort: retry CF with 60s timeout (slow tunnels need more time)
                // Ported from js-sdk/speedtest.js lines 463–464
                try
                {
                    probe = await Measure($"{CfDown}?bytes={ProbeBytes}", ProbeBytes, 60_000).ConfigureAwait(false);
                }
                catch
                {
                    // Tunnel IS connected (phase 0 passed) but all download targets are blocked.
                    // Use a timed GET of a known page as rough speed estimate instead of giving up.
                    // Ported from js-sdk/speedtest.js lines 468–481
                    try
                    {
                        using var handler = new HttpClientHandler
                        {
                            Proxy = MakeProxy(),
                            UseProxy = true,
                            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                        };
                        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

                        var sw = Stopwatch.StartNew();
                        var resp = await client.GetAsync("https://www.google.com", ct).ConfigureAwait(false);
                        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                        sw.Stop();

                        var bytes = (long)data.Length;
                        var elapsed = sw.Elapsed.TotalSeconds;
                        if (bytes > 0 && elapsed > 0)
                        {
                            // Ported from js-sdk/speedtest.js line 478
                            var googleMbps = Math.Max(Math.Round(CalculateMbps(bytes, elapsed), 2), 0.1);
                            return new SpeedResult(Mbps: googleMbps, Chunks: 1, Adaptive: "google-fallback");
                        }
                    }
                    catch
                    {
                        // all attempts exhausted
                    }

                    // Ported from js-sdk/speedtest.js line 481
                    throw new TunnelException(ErrorCodes.TunnelSetupFailed,
                        "SOCKS5 speed test failed (CF and all fallbacks unreachable)");
                }
            }
        }

        // Ported from js-sdk/speedtest.js line 486
        var probeMbps = Math.Round(CalculateMbps(probe!.Bytes, probe.Seconds), 2);

        // Ported from js-sdk/speedtest.js lines 488–489
        if (probeMbps < ProbeThresholdMbps)
        {
            return new SpeedResult(Mbps: probeMbps, Chunks: 1, Adaptive: "probe-only",
                TotalBytes: probe.Bytes, Seconds: Math.Round(probe.Seconds, 3));
        }

        // Phase 2: Multi-request — 5 x 1MB sequential downloads, each with fresh SOCKS5 agent
        // Ported from js-sdk/speedtest.js lines 493–515
        long totalBytes = 0;
        var successCount = 0;
        var overallSw = Stopwatch.StartNew();

        for (var i = 0; i < ChunkCount; i++)
        {
            try
            {
                // Ported from js-sdk/speedtest.js line 499
                var r = await Measure($"{CfDown}?bytes={ChunkBytes}", ChunkBytes).ConfigureAwait(false);
                totalBytes += r.Bytes;
                successCount++;
            }
            catch
            {
                // Ported from js-sdk/speedtest.js lines 502–505
                if (successCount == 0 && i == ChunkCount - 1)
                {
                    // All failed — return probe
                    return new SpeedResult(Mbps: probeMbps, Chunks: 1, Adaptive: "probe-fallback",
                        TotalBytes: probe.Bytes, Seconds: Math.Round(probe.Seconds, 3));
                }
            }
        }

        overallSw.Stop();

        // Ported from js-sdk/speedtest.js lines 510–511
        if (successCount == 0)
        {
            return new SpeedResult(Mbps: probeMbps, Chunks: 1, Adaptive: "probe-fallback",
                TotalBytes: probe.Bytes, Seconds: Math.Round(probe.Seconds, 3));
        }

        // Ported from js-sdk/speedtest.js lines 514–515
        var totalElapsed = overallSw.Elapsed.TotalSeconds;
        var finalMbps = Math.Round(CalculateMbps(totalBytes, totalElapsed), 2);
        return new SpeedResult(Mbps: finalMbps, Chunks: successCount, Adaptive: "multi-request",
            TotalBytes: totalBytes, Seconds: Math.Round(totalElapsed, 3));
    }

    /// <summary>
    /// Compare two speed test results. Returns delta and whether speed improved/degraded.
    /// Useful for before/after VPN comparison or detecting degradation.
    /// </summary>
    // Ported from js-sdk/speedtest.js lines 547–566
    public static SpeedComparison Compare(SpeedResult before, SpeedResult after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        // Ported from js-sdk/speedtest.js lines 548–552
        var dlDelta = after.Mbps - before.Mbps;
        var dlPct = before.Mbps > 0
            ? (dlDelta / before.Mbps) * 100.0
            : 0.0;

        return new SpeedComparison(
            // Ported from js-sdk/speedtest.js line 553
            Improved: dlDelta > 0,
            // Ported from js-sdk/speedtest.js line 554 — >1 Mbps drop = degraded
            Degraded: dlDelta < -1.0,
            DeltaMbps: Math.Round(dlDelta, 2),
            PercentChange: Math.Round(dlPct, 1)
        );
    }

    // ─── DNS Resolution ───
    // Ported from js-sdk/speedtest.js lines 96–135

    /// <summary>
    /// Resolve speed.cloudflare.com IP with 3-method fallback and 5-minute cache.
    /// Method 1: Explicit resolver to 1.1.1.1/8.8.8.8 (most reliable — bypasses broken system DNS).
    /// Method 2: System DNS resolve (c-ares equivalent).
    /// Method 3: OS resolver (getaddrinfo — always works but may return CDN-specific IP).
    /// </summary>
    // Ported from js-sdk/speedtest.js lines 96–124
    private static async Task ResolveCfHostInternalAsync()
    {
        lock (_dnsLock)
        {
            if (_cachedCfIp is not null && DateTime.UtcNow - _cachedCfTime < DnsCacheTtl)
                return;
        }

        // Method 1: Explicit resolver to 1.1.1.1 and 8.8.8.8
        // Ported from js-sdk/speedtest.js lines 100–106
        try
        {
            // In .NET, we query well-known DNS servers by sending a UDP query directly.
            // For simplicity we use Dns.GetHostAddressesAsync which uses the OS resolver,
            // but first attempt a direct DNS query to 1.1.1.1.
            var ip = await ResolveViaDnsServerAsync(CfHost, "1.1.1.1").ConfigureAwait(false);
            if (ip is not null)
            {
                lock (_dnsLock) { _cachedCfIp = ip; _cachedCfTime = DateTime.UtcNow; }
                return;
            }
        }
        catch { /* Method 1 failed, try next */ }

        // Method 1b: Try 8.8.8.8
        // Ported from js-sdk/speedtest.js line 102 (resolver.setServers(['1.1.1.1', '8.8.8.8']))
        try
        {
            var ip = await ResolveViaDnsServerAsync(CfHost, "8.8.8.8").ConfigureAwait(false);
            if (ip is not null)
            {
                lock (_dnsLock) { _cachedCfIp = ip; _cachedCfTime = DateTime.UtcNow; }
                return;
            }
        }
        catch { /* Method 1b failed, try next */ }

        // Method 2 & 3: System resolver (Dns.GetHostAddressesAsync uses OS resolver = getaddrinfo)
        // Ported from js-sdk/speedtest.js lines 109–121
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(CfHost).ConfigureAwait(false);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is not null)
            {
                var ip = ipv4.ToString();
                lock (_dnsLock) { _cachedCfIp = ip; _cachedCfTime = DateTime.UtcNow; }
                return;
            }
        }
        catch { /* DNS completely failed — cachedCfIp stays null */ }

        // Ported from js-sdk/speedtest.js line 123: return null (cachedCfIp stays null)
    }

    /// <summary>
    /// Pre-resolve fallback hosts so they work behind WireGuard tunnels too.
    /// </summary>
    // Ported from js-sdk/speedtest.js lines 127–135
    private static async Task ResolveFallbackHostsAsync()
    {
        foreach (var fb in FallbackUrls)
        {
            bool alreadyCached;
            lock (_dnsLock) { alreadyCached = _cachedFallbackIps.ContainsKey(fb.Host); }
            if (alreadyCached) continue;

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(fb.Host).ConfigureAwait(false);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is not null)
                {
                    lock (_dnsLock) { _cachedFallbackIps[fb.Host] = ipv4.ToString(); }
                }
            }
            catch { /* DNS resolution may fail — fallback will use hostname directly */ }
        }
    }

    /// <summary>
    /// Resolve a hostname via a specific DNS server using raw UDP DNS query.
    /// This bypasses the system resolver (equivalent to JS dns.Resolver with setServers).
    /// </summary>
    private static async Task<string?> ResolveViaDnsServerAsync(string hostname, string dnsServer)
    {
        // Build a minimal DNS A-record query
        var queryId = (ushort)Random.Shared.Next(0, 65536);
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);

        // Header: ID, flags (standard query, recursion desired), QDCOUNT=1
        writer.Write(IPAddress.HostToNetworkOrder((short)queryId));
        writer.Write(IPAddress.HostToNetworkOrder((short)0x0100)); // QR=0, OPCODE=0, RD=1
        writer.Write(IPAddress.HostToNetworkOrder((short)1));      // QDCOUNT
        writer.Write(IPAddress.HostToNetworkOrder((short)0));      // ANCOUNT
        writer.Write(IPAddress.HostToNetworkOrder((short)0));      // NSCOUNT
        writer.Write(IPAddress.HostToNetworkOrder((short)0));      // ARCOUNT

        // Question: hostname labels
        foreach (var label in hostname.Split('.'))
        {
            writer.Write((byte)label.Length);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        writer.Write((byte)0); // root label

        writer.Write(IPAddress.HostToNetworkOrder((short)1));  // QTYPE = A
        writer.Write(IPAddress.HostToNetworkOrder((short)1));  // QCLASS = IN

        var queryBytes = ms.ToArray();

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 3000;
        udp.Client.SendTimeout = 3000;
        var serverEndpoint = new IPEndPoint(IPAddress.Parse(dnsServer), 53);

        await udp.SendAsync(queryBytes, queryBytes.Length, serverEndpoint).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(3000);
        var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
        var response = result.Buffer;

        // Parse response — skip header (12 bytes) and question section, find first A record
        if (response.Length < 12) return null;

        var anCount = (response[6] << 8) | response[7];
        if (anCount == 0) return null;

        // Skip question section
        var offset = 12;
        // Skip QNAME
        while (offset < response.Length && response[offset] != 0)
        {
            if ((response[offset] & 0xC0) == 0xC0) { offset += 2; goto questionEnd; }
            offset += response[offset] + 1;
        }
        offset++; // null terminator
        questionEnd:
        offset += 4; // QTYPE + QCLASS

        // Parse answer records
        for (var i = 0; i < anCount && offset < response.Length; i++)
        {
            // Skip NAME (may be pointer)
            if ((response[offset] & 0xC0) == 0xC0) { offset += 2; }
            else { while (offset < response.Length && response[offset] != 0) offset += response[offset] + 1; offset++; }

            if (offset + 10 > response.Length) break;

            var rType = (response[offset] << 8) | response[offset + 1];
            var rdLength = (response[offset + 8] << 8) | response[offset + 9];
            offset += 10;

            if (rType == 1 && rdLength == 4 && offset + 4 <= response.Length)
            {
                // A record — return IP
                return $"{response[offset]}.{response[offset + 1]}.{response[offset + 2]}.{response[offset + 3]}";
            }

            offset += rdLength;
        }

        return null;
    }

    // ─── Fresh Download ───
    // Ported from js-sdk/speedtest.js lines 143–202

    /// <summary>
    /// Download limitBytes from url with a FRESH TCP+TLS connection.
    /// Each call creates a new HttpClientHandler (no connection reuse / keep-alive).
    /// If the URL uses an IP address, sets the Host header and TLS SNI to the proper hostname.
    /// </summary>
    private static async Task<FreshDownloadResult> FreshDownloadAsync(
        string url,
        int limitBytes,
        string? hostOverride = null,
        int timeoutMs = 30_000,
        WebProxy? proxy = null,
        CancellationToken ct = default)
    {
        // Ported from js-sdk/speedtest.js lines 144–201
        var parsed = new Uri(url);
        var isIp = IPAddress.TryParse(parsed.Host, out _);

        // Ported from js-sdk/speedtest.js lines 159–177
        // CRITICAL: fresh TCP+TLS connection every time (no keep-alive)
        // Equivalent to JS: agent: false
        using var handler = new SocketsHttpHandler
        {
            // Disable connection pooling — forces fresh TCP+TLS per request
            PooledConnectionLifetime = TimeSpan.Zero,
            MaxConnectionsPerServer = 1,
            ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // rejectUnauthorized: false
                // Ported from js-sdk/speedtest.js line 171: options.servername = hostName
                TargetHost = isIp ? (hostOverride ?? CfHost) : parsed.Host,
            },
        };

        if (proxy is not null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

        // Ported from js-sdk/speedtest.js lines 168–172: IP-based URL: set Host header
        if (isIp)
        {
            var hostName = hostOverride ?? CfHost;
            client.DefaultRequestHeaders.Host = hostName;
        }

        var sw = Stopwatch.StartNew();

        // Ported from js-sdk/speedtest.js lines 179–201
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        long downloaded = 0;
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[65536];

        while (downloaded < limitBytes)
        {
            var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, limitBytes - downloaded), ct).ConfigureAwait(false);
            if (read == 0) break;
            downloaded += read;
        }

        sw.Stop();
        var elapsed = sw.Elapsed.TotalSeconds;

        // Ported from js-sdk/speedtest.js lines 154–156
        if (elapsed <= 0 || downloaded == 0)
            throw new InvalidOperationException("No data received");

        return new FreshDownloadResult(downloaded, elapsed);
    }

    // ─── Multi Request Measure ───
    // Ported from js-sdk/speedtest.js lines 254–277

    /// <summary>
    /// Multi-request speed test: download N chunks sequentially, each with fresh TCP+TLS.
    /// Total elapsed time includes all connection overhead (handshakes compound).
    /// VPN latency shows up as genuinely lower effective throughput.
    /// </summary>
    private static async Task<MultiRequestResult> MultiRequestMeasureAsync(
        string baseUrl,
        int chunkBytes,
        int chunkCount,
        string? hostOverride = null,
        WebProxy? proxy = null,
        CancellationToken ct = default)
    {
        long totalBytes = 0;
        var successCount = 0;
        var overallSw = Stopwatch.StartNew();

        // Ported from js-sdk/speedtest.js lines 259–270
        for (var i = 0; i < chunkCount; i++)
        {
            try
            {
                var r = await FreshDownloadAsync(baseUrl, chunkBytes, hostOverride, timeoutMs: 30_000, proxy: proxy, ct: ct).ConfigureAwait(false);
                totalBytes += r.Bytes;
                successCount++;
            }
            catch
            {
                // Allow partial success — report based on successful chunks
                // Ported from js-sdk/speedtest.js lines 266–268
                if (successCount == 0 && i == chunkCount - 1)
                {
                    throw new TunnelException(ErrorCodes.TunnelSetupFailed, "All speed test chunks failed");
                }
            }
        }

        // Ported from js-sdk/speedtest.js line 272
        if (successCount == 0) throw new TunnelException(ErrorCodes.TunnelSetupFailed, "All speed test chunks failed");

        overallSw.Stop();

        // Ported from js-sdk/speedtest.js lines 274–276
        var totalElapsed = overallSw.Elapsed.TotalSeconds;
        var mbps = Math.Round(CalculateMbps(totalBytes, totalElapsed), 2);
        return new MultiRequestResult(mbps, successCount, totalBytes, totalElapsed);
    }

    // ─── Fallback Measure ───
    // Ported from js-sdk/speedtest.js lines 283–306

    /// <summary>
    /// Fallback speed measurement — download a known-size file via HTTPS.
    /// Used when Cloudflare is unreachable through a WireGuard tunnel.
    /// Tries pre-resolved IP first, then hostname directly.
    /// </summary>
    private static async Task<SpeedResult?> FallbackMeasureAsync(
        WebProxy? proxy = null,
        CancellationToken ct = default)
    {
        // Ported from js-sdk/speedtest.js lines 284–305
        foreach (var fb in FallbackUrls)
        {
            string? ip;
            lock (_dnsLock) { _cachedFallbackIps.TryGetValue(fb.Host, out ip); }

            // Try pre-resolved IP first
            // Ported from js-sdk/speedtest.js lines 287–293
            if (ip is not null)
            {
                try
                {
                    var result = await FreshDownloadAsync(
                        $"https://{ip}{fb.Path}",
                        fb.Size,
                        hostOverride: fb.Host,
                        proxy: proxy,
                        ct: ct
                    ).ConfigureAwait(false);
                    var mbps = Math.Round(CalculateMbps(result.Bytes, result.Seconds), 2);
                    return new SpeedResult(Mbps: mbps, Chunks: 1, Adaptive: "fallback",
                        TotalBytes: result.Bytes, Seconds: Math.Round(result.Seconds, 3), FallbackHost: fb.Host);
                }
                catch { /* try hostname directly */ }
            }

            // Also try hostname directly
            // Ported from js-sdk/speedtest.js lines 296–303
            try
            {
                var result = await FreshDownloadAsync(
                    $"https://{fb.Host}{fb.Path}",
                    fb.Size,
                    proxy: proxy,
                    ct: ct
                ).ConfigureAwait(false);
                var mbps = Math.Round(CalculateMbps(result.Bytes, result.Seconds), 2);
                return new SpeedResult(Mbps: mbps, Chunks: 1, Adaptive: "fallback",
                    TotalBytes: result.Bytes, Seconds: Math.Round(result.Seconds, 3), FallbackHost: fb.Host);
            }
            catch { /* try next fallback */ }
        }

        // Ported from js-sdk/speedtest.js line 305
        return null;
    }

    // ─── Rescue Download ───
    // Ported from js-sdk/speedtest.js lines 209–247

    /// <summary>
    /// Last-resort single-stream download with long timeout (60s).
    /// Used when the multi-request test fails through a tunnel.
    /// Downloads 2MB with a keep-alive agent for reliability.
    /// Returns low but valid speed rather than failing the node entirely.
    /// </summary>
    private static async Task<SpeedResult?> RescueDownloadAsync(CancellationToken ct = default)
    {
        // Ported from js-sdk/speedtest.js line 211
        const int rescueBytes = 2 * 1024 * 1024;

        // Ported from js-sdk/speedtest.js line 212: keep-alive agent
        // In C#, SocketsHttpHandler with default settings keeps connections alive.
        using var keepAliveHandler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                TargetHost = CfHost,
            },
        };
        using var keepAliveClient = new HttpClient(keepAliveHandler) { Timeout = TimeSpan.FromSeconds(60) };
        keepAliveClient.DefaultRequestHeaders.Host = CfHost;

        // Try: IP with agent, hostname with agent
        // Ported from js-sdk/speedtest.js lines 215–226
        var urls = new List<string>();
        string? cfIp;
        lock (_dnsLock) { cfIp = _cachedCfIp; }
        if (cfIp is not null) urls.Add($"https://{cfIp}/__down?bytes={rescueBytes}");
        urls.Add($"{CfDown}?bytes={rescueBytes}");

        foreach (var url in urls)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var response = await keepAliveClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long downloaded = 0;
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[65536];
                while (downloaded < rescueBytes)
                {
                    var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, rescueBytes - downloaded), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    downloaded += read;
                }
                sw.Stop();

                if (downloaded > 0 && sw.Elapsed.TotalSeconds > 0)
                {
                    // Ported from js-sdk/speedtest.js lines 222–224
                    var mbps = Math.Round(CalculateMbps(downloaded, sw.Elapsed.TotalSeconds), 2);
                    return new SpeedResult(Mbps: mbps, Chunks: 1, Adaptive: "rescue",
                        TotalBytes: downloaded, Seconds: Math.Round(sw.Elapsed.TotalSeconds, 3));
                }
            }
            catch { /* try next URL */ }
        }

        // Try fallback URLs with long timeout
        // Ported from js-sdk/speedtest.js lines 229–243
        foreach (var fb in FallbackUrls)
        {
            string? ip;
            lock (_dnsLock) { _cachedFallbackIps.TryGetValue(fb.Host, out ip); }

            var targets = new List<(string Url, string? HostOverride)>();
            if (ip is not null) targets.Add(($"https://{ip}{fb.Path}", fb.Host));
            targets.Add(($"https://{fb.Host}{fb.Path}", null));

            foreach (var (targetUrl, hostOvr) in targets)
            {
                try
                {
                    // Use a fresh handler with keep-alive for rescue, 60s timeout
                    using var rescueHandler = new SocketsHttpHandler
                    {
                        SslOptions = new SslClientAuthenticationOptions
                        {
                            RemoteCertificateValidationCallback = (_, _, _, _) => true,
                            TargetHost = hostOvr ?? fb.Host,
                        },
                    };
                    using var rescueClient = new HttpClient(rescueHandler) { Timeout = TimeSpan.FromSeconds(60) };
                    if (hostOvr is not null) rescueClient.DefaultRequestHeaders.Host = hostOvr;

                    var sw = Stopwatch.StartNew();
                    using var response = await rescueClient.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    long downloaded = 0;
                    using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[65536];
                    while (downloaded < fb.Size)
                    {
                        var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, fb.Size - downloaded), ct).ConfigureAwait(false);
                        if (read == 0) break;
                        downloaded += read;
                    }
                    sw.Stop();

                    if (downloaded > 0 && sw.Elapsed.TotalSeconds > 0)
                    {
                        // Ported from js-sdk/speedtest.js lines 239–240
                        var mbps = Math.Round(CalculateMbps(downloaded, sw.Elapsed.TotalSeconds), 2);
                        return new SpeedResult(Mbps: mbps, Chunks: 1, Adaptive: "rescue-fallback",
                            TotalBytes: downloaded, Seconds: Math.Round(sw.Elapsed.TotalSeconds, 3), FallbackHost: fb.Host);
                    }
                }
                catch { /* try next target */ }
            }
        }

        // Ported from js-sdk/speedtest.js line 246
        return null;
    }

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
