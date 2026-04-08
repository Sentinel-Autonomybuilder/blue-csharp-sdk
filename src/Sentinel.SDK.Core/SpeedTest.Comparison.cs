using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Sentinel.SDK.Core;

// ─── Speed Test Comparison, DNS Resolution, and Fallback Chain ───

public static partial class SpeedTest
{
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
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

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
}
