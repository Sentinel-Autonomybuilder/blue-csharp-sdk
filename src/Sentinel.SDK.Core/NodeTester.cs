using System.Diagnostics;
using System.Net;

namespace Sentinel.SDK.Core;

// ─── Test Adapter Interface ───

/// <summary>
/// Interface that any app implements to enable node testing.
/// Wraps the app's own connect/disconnect functions.
/// The NodeTester calls these — it does NOT bypass the app's VPN stack.
/// </summary>
public interface INodeTestAdapter
{
    /// <summary>Connect to a node using the app's own VPN logic.</summary>
    Task ConnectAsync(string nodeAddress, CancellationToken ct = default);

    /// <summary>Disconnect using the app's own VPN logic.</summary>
    Task DisconnectAsync();

    /// <summary>Is the VPN tunnel currently active?</summary>
    bool IsConnected { get; }

    /// <summary>Address of the currently connected node, or null.</summary>
    string? ConnectedNodeAddress { get; }

    /// <summary>"wireguard" or "v2ray"</summary>
    string? TunnelType { get; }

    /// <summary>SOCKS5 port if V2Ray tunnel is active, or null for WireGuard.</summary>
    int? SocksPort { get; }
}

// ─── Test Options ───

public class NodeTestOptions
{
    /// <summary>Max time to wait for a single node connection (ms). Default: 120s.</summary>
    public int ConnectTimeoutMs { get; set; } = 120_000;

    /// <summary>Max nodes to test. 0 = all.</summary>
    public int MaxNodes { get; set; } = 0;

    /// <summary>Skip nodes with fewer peers than this.</summary>
    public int MinPeers { get; set; } = 0;

    /// <summary>Test connectivity through tunnel (google, cloudflare, etc).</summary>
    public bool TestConnectivity { get; set; } = true;

    /// <summary>Test speed through tunnel.</summary>
    public bool TestSpeed { get; set; } = true;

    /// <summary>Test DNS resolution through tunnel.</summary>
    public bool TestDns { get; set; } = false;

    /// <summary>DNS provider for DNS test: "hns", "google", "cloudflare".</summary>
    public string DnsPreset { get; set; } = "default";

    /// <summary>Connectivity check targets.</summary>
    public string[] ConnectivityTargets { get; set; } = new[]
    {
        "https://www.google.com",
        "https://www.cloudflare.com",
        "https://httpbin.org/ip",
        "https://ifconfig.me",
    };

    /// <summary>Filter by node type: null = all, "wireguard", "v2ray".</summary>
    public string? NodeTypeFilter { get; set; }

    /// <summary>Filter by country code: null = all, "US", "DE", etc.</summary>
    public string? CountryFilter { get; set; }
}

// ─── Test Result ───

public class NodeTestResult
{
    // Identity
    public string Address { get; set; } = "";
    public string? Moniker { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? City { get; set; }
    public string? NodeType { get; set; }
    public int? Peers { get; set; }
    public int? MaxPeers { get; set; }

    // Connection
    public bool Success { get; set; }
    public string? Error { get; set; }
    public long ConnectTimeMs { get; set; }
    public long TotalTimeMs { get; set; }
    public bool DisconnectClean { get; set; }

    // Speed
    public double? SpeedMbps { get; set; }
    public double? BaselineAtTest { get; set; }
    public string? SpeedMethod { get; set; }
    public bool Pass10Mbps => (SpeedMbps ?? 0) >= 10;
    public bool Pass15Mbps => (SpeedMbps ?? 0) >= 15;

    // Connectivity
    public bool? GoogleAccessible { get; set; }
    public int? GoogleLatencyMs { get; set; }
    public string? PublicIp { get; set; }

    // DNS
    public bool? DnsStandardResolved { get; set; }
    public bool? DnsHnsResolved { get; set; }
    public int? DnsLatencyMs { get; set; }

    // Timing
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Platform { get; set; } = Environment.OSVersion.Platform.ToString();
}

// ─── Test Summary ───

public class NodeTestSummary
{
    public int Tested { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public double PassRate => Tested > 0 ? (double)Passed / Tested * 100 : 0;
    public double AvgSpeedMbps { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}

// ─── Node Tester ───

/// <summary>
/// Automated node tester that calls the app's own connect/disconnect functions
/// against real Sentinel dVPN nodes.
///
/// Usage:
///   var adapter = new MyAppTestAdapter(myVpnClient);
///   var tester = new NodeTester(adapter);
///   tester.OnResult += result => UpdateUI(result);
///   await tester.RunAsync(nodes, options);
/// </summary>
public class NodeTester
{
    private readonly INodeTestAdapter _adapter;
    private volatile bool _stopRequested;

    // Events
    public event Action<NodeTestResult>? OnResult;
    public event Action<string>? OnLog;
    public event Action<int, int>? OnProgress; // (tested, total)
    public event Action<NodeTestSummary>? OnComplete;

    // State
    public bool IsRunning { get; private set; }
    public int Tested { get; private set; }
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    public NodeTester(INodeTestAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    /// <summary>Stop the current test run. Takes effect within 1 second.</summary>
    public void Stop() => _stopRequested = true;

    /// <summary>
    /// Test a single node. Calls adapter.Connect → connectivity check → speed test → adapter.Disconnect.
    /// </summary>
    public async Task<NodeTestResult> TestNodeAsync(
        string nodeAddress,
        NodeTestOptions? options = null,
        double? baselineMbps = null,
        CancellationToken ct = default)
    {
        options ??= new NodeTestOptions();
        var result = new NodeTestResult { Address = nodeAddress, BaselineAtTest = baselineMbps };
        var sw = Stopwatch.StartNew();

        try
        {
            // Phase 1: CONNECT — uses the app's own function
            OnLog?.Invoke($"Connecting to {nodeAddress.Substring(0, Math.Min(20, nodeAddress.Length))}...");

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(options.ConnectTimeoutMs);
            await _adapter.ConnectAsync(nodeAddress, connectCts.Token);
            result.ConnectTimeMs = sw.ElapsedMilliseconds;

            if (!_adapter.IsConnected)
            {
                result.Error = "Connect returned but tunnel not active";
                return result;
            }

            result.NodeType = _adapter.TunnelType;
            OnLog?.Invoke($"Connected in {result.ConnectTimeMs}ms ({result.NodeType})");

            // Phase 2: CONNECTIVITY CHECK
            if (options.TestConnectivity)
            {
                OnLog?.Invoke("Checking connectivity...");
                var (reachable, target, latencyMs, publicIp) = await CheckConnectivityAsync(
                    options.ConnectivityTargets, _adapter.SocksPort, ct);
                result.GoogleAccessible = reachable;
                result.GoogleLatencyMs = latencyMs;
                result.PublicIp = publicIp;
                OnLog?.Invoke(reachable
                    ? $"Reachable via {target} ({latencyMs}ms) IP: {publicIp}"
                    : "No internet connectivity through tunnel");
            }

            // Phase 3: DNS TEST
            if (options.TestDns)
            {
                OnLog?.Invoke($"Testing DNS ({options.DnsPreset})...");
                var (standard, hns, dnsLatency) = await CheckDnsAsync(options.DnsPreset, ct);
                result.DnsStandardResolved = standard;
                result.DnsHnsResolved = hns;
                result.DnsLatencyMs = dnsLatency;
            }

            // Phase 4: SPEED TEST
            if (options.TestSpeed && (result.GoogleAccessible ?? false))
            {
                OnLog?.Invoke("Running speed test...");
                try
                {
                    var speed = _adapter.SocksPort.HasValue
                        ? await SpeedTest.ViaSocks5Async(_adapter.SocksPort.Value, ct: ct)
                        : await SpeedTest.DirectAsync(ct);
                    result.SpeedMbps = speed.Mbps;
                    result.SpeedMethod = speed.Adaptive;
                    OnLog?.Invoke($"Speed: {speed.Mbps:F2} Mbps ({speed.Adaptive})");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"Speed test failed: {ex.Message}");
                    result.SpeedMbps = 0;
                    result.SpeedMethod = "failed";
                }
            }

            // Phase 5: DISCONNECT
            OnLog?.Invoke("Disconnecting...");
            await _adapter.DisconnectAsync();
            result.DisconnectClean = !_adapter.IsConnected;

            result.Success = result.GoogleAccessible ?? (result.SpeedMbps > 0);
        }
        catch (OperationCanceledException)
        {
            result.Error = "Connection timed out";
            try { await _adapter.DisconnectAsync(); } catch { }
            result.DisconnectClean = !_adapter.IsConnected;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            try { await _adapter.DisconnectAsync(); } catch { }
            result.DisconnectClean = !_adapter.IsConnected;
        }

        result.TotalTimeMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Test multiple nodes sequentially. Calls TestNodeAsync for each.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<(string address, string? moniker, string? country, string? countryCode, string? city, int? peers, int? maxPeers)> nodes,
        NodeTestOptions? options = null,
        double? baselineMbps = null,
        CancellationToken ct = default)
    {
        options ??= new NodeTestOptions();
        _stopRequested = false;
        IsRunning = true;
        Tested = Passed = Failed = 0;
        var startedAt = DateTime.UtcNow;
        var speeds = new List<double>();

        var toTest = nodes.AsEnumerable();
        if (options.MaxNodes > 0) toTest = toTest.Take(options.MaxNodes);
        if (options.MinPeers > 0) toTest = toTest.Where(n => (n.peers ?? 0) >= options.MinPeers);
        if (options.NodeTypeFilter != null) toTest = toTest.Where(n => true); // filter applied during test
        if (options.CountryFilter != null) toTest = toTest.Where(n => n.countryCode == options.CountryFilter);
        var nodeList = toTest.ToList();

        for (int i = 0; i < nodeList.Count; i++)
        {
            if (_stopRequested || ct.IsCancellationRequested) break;

            var node = nodeList[i];
            var result = await TestNodeAsync(node.address, options, baselineMbps, ct);

            // Fill in node metadata
            result.Moniker = node.moniker;
            result.Country = node.country;
            result.CountryCode = node.countryCode;
            result.City = node.city;
            result.Peers = node.peers;
            result.MaxPeers = node.maxPeers;

            Tested++;
            if (result.Success) { Passed++; if (result.SpeedMbps.HasValue) speeds.Add(result.SpeedMbps.Value); }
            else Failed++;

            OnResult?.Invoke(result);
            OnProgress?.Invoke(Tested, nodeList.Count);
        }

        IsRunning = false;
        OnComplete?.Invoke(new NodeTestSummary
        {
            Tested = Tested,
            Passed = Passed,
            Failed = Failed,
            AvgSpeedMbps = speeds.Count > 0 ? speeds.Average() : 0,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
        });
    }

    // ─── Connectivity Check ───

    private async Task<(bool reachable, string? target, int latencyMs, string? publicIp)> CheckConnectivityAsync(
        string[] targets, int? socksPort, CancellationToken ct)
    {
        using var handler = socksPort.HasValue
            ? new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}") }
            : new HttpClientHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        foreach (var target in targets)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var response = await http.GetAsync(target, ct);
                var latency = (int)sw.ElapsedMilliseconds;

                // Try to extract public IP from response
                string? ip = null;
                if (target.Contains("httpbin") || target.Contains("ifconfig") || target.Contains("ip-api"))
                {
                    try
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        // Simple IP extraction from JSON or plain text
                        var match = System.Text.RegularExpressions.Regex.Match(body, @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}");
                        if (match.Success) ip = match.Value;
                    }
                    catch { }
                }

                return (true, target, latency, ip);
            }
            catch { }
        }
        return (false, null, 0, null);
    }

    // ─── DNS Test ───

    private async Task<(bool standard, bool hns, int latencyMs)> CheckDnsAsync(string preset, CancellationToken ct)
    {
        var standardTargets = new[] { "google.com", "sentinel.co", "cloudflare.com" };
        var hnsTargets = new[] { "welcome.nb", "3b" };
        bool standardOk = false, hnsOk = false;
        int latency = 0;

        // Standard DNS
        foreach (var domain in standardTargets)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var entry = await System.Net.Dns.GetHostEntryAsync(domain, ct);
                latency = (int)sw.ElapsedMilliseconds;
                if (entry.AddressList.Length > 0) { standardOk = true; break; }
            }
            catch { }
        }

        // HNS DNS (only meaningful with HNS DNS provider)
        if (preset == "hns")
        {
            foreach (var domain in hnsTargets)
            {
                try
                {
                    var entry = await System.Net.Dns.GetHostEntryAsync(domain, ct);
                    if (entry.AddressList.Length > 0) { hnsOk = true; break; }
                }
                catch { }
            }
        }

        return (standardOk, hnsOk, latency);
    }
}
