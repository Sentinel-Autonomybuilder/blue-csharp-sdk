using System.Diagnostics;
using System.Net;
using System.Net.Security;

namespace Sentinel.SDK.Core;

// ─── Speed Test Runner (Direct + SOCKS5) ───

public static partial class SpeedTest
{
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
}
