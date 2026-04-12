// ─── Native VPN Client for Handshake dVPN ───
// Direct connection to any Sentinel node. No plan required.
// Default DNS: Handshake resolvers (103.196.38.38, 103.196.38.39)

using System.IO;
using System.Net.Http;
using Sentinel.SDK.Core;
using Sentinel.SDK.Node;

namespace HandshakeDVPN.Services;

public class NativeVpnClient : IHnsVpnBackend
{
    private SentinelWallet? _wallet;
    private ChainClient? _chain;
    private SentinelVpnClient? _vpn;
    private readonly ISdkLogger _logger;
    private bool _chainInitialized;
    // No fee granter for direct-connect apps

    public AppSettings Settings { get; set; }

    public string? WalletAddress => _wallet?.Address;
    public bool HasWallet => _wallet != null;
    public ChainClient? GetChain() => _chain;
    public SentinelWallet? GetWallet() => _wallet;
    public async Task EnsureChainPublicAsync() => await EnsureChainAsync();

    // ─── Events ───
    public event Action<string>? OnLog;
    public event Action<string, string?>? OnProgress;
    public event Action<List<HnsNodeInfo>>? OnNodesEnriched { add { } remove { } }

    public NativeVpnClient(string? mnemonic)
    {
        Settings = AppSettings.Load();
        _logger = new BridgeLogger(msg => OnLog?.Invoke(msg));

        if (!string.IsNullOrEmpty(mnemonic))
            InitWallet(mnemonic);
    }

    private void InitWallet(string mnemonic)
    {
        _wallet = SentinelWallet.FromMnemonic(mnemonic);
        _chain = new ChainClient(logger: _logger);

        var v2rayPath = FindBinary("v2ray.exe");
        _vpn = new SentinelVpnClient(_wallet, new SentinelVpnOptions
        {
            FullTunnel = true,
            SystemProxy = true,
            V2RayExePath = v2rayPath,
            Dns = Settings.GetDnsString(),
        });

        _vpn.Progress += (_, e) => OnProgress?.Invoke(e.Step, e.Detail);
        _vpn.Connected += (_, e) => OnLog?.Invoke($"Connected to {e.Result.NodeAddress}");
        _vpn.Disconnected += (_, e) => OnLog?.Invoke($"Disconnected: {e.Reason}");
        _vpn.Error += (_, e) => OnLog?.Invoke($"SDK error: {e.Exception.Message}");
    }

    private async Task EnsureChainAsync()
    {
        if (_chain == null) throw new InvalidOperationException("No wallet loaded");
        if (_chainInitialized) return;
        OnLog?.Invoke("Probing LCD endpoints...");
        await _chain.InitializeAsync();
        _chainInitialized = true;
        OnLog?.Invoke("LCD ready");

        // No auto fee grant detection — direct-connect apps pay their own gas
        // Fee grants are for plan-based apps where an operator covers gas fees
    }

    // ─── Wallet ───

    public async Task<BalanceData?> GetBalanceAsync()
    {
        try
        {
            await EnsureChainAsync();
            var bal = await _chain!.GetBalanceAsync(_wallet!.Address);
            return new BalanceData { Udvpn = bal.Udvpn, P2P = (double)bal.P2P, Display = bal.Display };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Balance error: {ex.Message}");
            return null;
        }
    }

    public Task<ImportData?> ImportWalletAsync(string mnemonic)
    {
        try
        {
            if (_vpn != null)
            {
                try { _vpn.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
                _vpn.Dispose();
            }
            _chain?.Dispose();
            _wallet?.Dispose();
            _chainInitialized = false;

            InitWallet(mnemonic);
            return Task.FromResult<ImportData?>(new ImportData { Address = _wallet!.Address, Valid = true });
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Import error: {ex.Message}");
            return Task.FromResult<ImportData?>(null);
        }
    }

    public Task<WalletData?> CreateWalletAsync(int strength = 128)
    {
        var w = SentinelWallet.Generate(strength);
        var result = new WalletData { Address = w.Address, Mnemonic = w.ExportMnemonicString() ?? "" };
        w.Dispose();
        return Task.FromResult<WalletData?>(result);
    }

    // ─── Nodes (ALL active — no plan filter) ───

    public async Task<HnsNodesData?> GetAllNodesAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureChainAsync();
            OnLog?.Invoke("Querying all active nodes from chain...");
            var chainNodes = await _chain!.GetActiveNodesAsync(limit: 5000, ct: ct);
            OnLog?.Invoke($"Found {chainNodes.Count} nodes — probing status...");

            // Enrich all nodes with live status (30 parallel workers, 6s timeout)
            var sem = new SemaphoreSlim(30);
            var enriched = new HnsNodeInfo[chainNodes.Count];
            var done = 0;
            var tasks = chainNodes.Select(async (n, i) =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var url = n.RemoteUrl ?? n.RemoteAddrs?.FirstOrDefault();
                    if (url == null) { enriched[i] = ToNodeInfo(n, null); return; }
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(6));
                    var status = await NodeClient.GetStatusAsync(url, ct: cts.Token);
                    enriched[i] = ToNodeInfo(n, status);
                }
                catch { enriched[i] = ToNodeInfo(n, null); }
                finally
                {
                    sem.Release();
                    var count = Interlocked.Increment(ref done);
                    if (count % 100 == 0)
                        OnLog?.Invoke($"Probed {count}/{chainNodes.Count} nodes...");
                }
            });
            await Task.WhenAll(tasks);

            var sorted = enriched
                .OrderByDescending(n => n.Moniker != null)
                .ThenBy(n => n.Country ?? "ZZZ")
                .ThenBy(n => n.City ?? "")
                .ThenByDescending(n => n.BandwidthDown ?? 0)
                .ToList();

            var online = sorted.Count(n => n.Moniker != null);
            OnLog?.Invoke($"Ready: {online}/{sorted.Count} nodes online");
            return new HnsNodesData { Nodes = sorted, Total = sorted.Count };
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Node load FAILED: {ex.Message}");
            return null;
        }
    }

    // ─── Subscriptions / Sessions ───

    public async Task<List<ActiveSession>?> GetActiveSessionsAsync()
    {
        try
        {
            await EnsureChainAsync();

            // Get subscriptions — these have expiry dates and price info
            var subs = await _chain!.GetSubscriptionsAsync(_wallet!.Address);
            var activeSubs = subs.Where(s => s.Status.Contains("active", StringComparison.OrdinalIgnoreCase)).ToList();

            // Get sessions for each subscription's nodes
            var sessions = await _chain.GetSessionsAsync(_wallet.Address, status: "1");
            try { var pending = await _chain.GetSessionsAsync(_wallet.Address, status: "3"); sessions.AddRange(pending); } catch { }

            var result = new List<ActiveSession>();
            var seen = new HashSet<string>();

            // Map sessions to subscriptions for richer data
            foreach (var s in sessions)
            {
                if (!seen.Add(s.Id)) continue;
                long.TryParse(s.DownloadBytes, out var dl);
                long.TryParse(s.UploadBytes, out var ul);
                long.TryParse(s.MaxBytes, out var max);

                // Try to find matching subscription for this node
                var matchSub = activeSubs.FirstOrDefault(sub => sub.PlanId == "0"); // direct subs
                var payMode = SessionTracker.GetMode(s.Id);

                // Determine if hourly from subscription price or local tracker
                string? inactiveAt = null;
                foreach (var sub in activeSubs)
                {
                    if (sub.Price != null)
                    {
                        inactiveAt = sub.InactiveAt;
                        break;
                    }
                }

                result.Add(new ActiveSession
                {
                    SessionId = s.Id,
                    NodeAddress = s.NodeAddress,
                    DownloadBytes = dl,
                    UploadBytes = ul,
                    MaxBytes = max,
                    Status = s.Status,
                    PayMode = payMode,
                    InactiveAt = inactiveAt,
                });
            }

            // Also add subscriptions that don't have active sessions (time remaining but not connected)
            foreach (var sub in activeSubs)
            {
                if (sub.PlanId != "0") continue; // skip plan subs — shown in Plans tab
                var hasSession = result.Any(r => true); // all subs shown
                if (!string.IsNullOrEmpty(sub.InactiveAt))
                {
                    // Check if this subscription's time is worth showing
                    if (DateTime.TryParse(sub.InactiveAt, out var exp) && exp > DateTime.UtcNow)
                    {
                        var left = exp - DateTime.UtcNow;
                        var existing = result.FirstOrDefault();
                        // Update InactiveAt on existing sessions
                        foreach (var r in result)
                        {
                            if (r.InactiveAt == null) r.InactiveAt = sub.InactiveAt;
                        }
                    }
                }
            }

            return result.OrderByDescending(s => long.TryParse(s.SessionId, out var id) ? id : 0).ToList();
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Sessions error: {ex.Message}");
            return null;
        }
    }

    public async Task<(long max, long used)?> QueryAllocationAsync(ulong sessionId)
    {
        try
        {
            await EnsureChainAsync();
            var alloc = await _chain!.QuerySessionAllocationAsync(sessionId);
            if (alloc != null)
                return (alloc.MaxBytes, alloc.UsedBytes);
            return null;
        }
        catch { return null; }
    }

    // ─── Plans ───

    public async Task<List<PlanInfo>?> DiscoverPlansAsync()
    {
        try
        {
            await EnsureChainAsync();
            OnLog?.Invoke("Discovering plans...");

            // /sentinel/plan/v3/plans/{id} returns 501 Not Implemented on chain v3
            // Must use subscription endpoint to probe for plans that exist
            var result = new List<PlanInfo>();
            var subs = _wallet != null ? await _chain!.GetSubscriptionsAsync(_wallet.Address) : new();
            var grants = _wallet != null ? await _chain!.QueryFeeGrantsAsync(_wallet.Address) : new();

            // Probe plan IDs 1-100 using lightweight queries
            // Use raw LCD to check plan existence (limit=1 for speed)
            var maxId = Settings.PlanProbeMax;
            var sem = new SemaphoreSlim(20); // more workers = faster
            var found = new System.Collections.Concurrent.ConcurrentBag<PlanInfo>();
            var tasks = Enumerable.Range(1, maxId).Select(async id =>
            {
                await sem.WaitAsync();
                try
                {
                    // Quick check: does this plan have any subscribers?
                    int subCount = 0;
                    string? priceDisplay = null;
                    string? priceUdvpn = null;
                    try
                    {
                        // Use GetPlanStatsAsync which handles pagination correctly
                        var stats = await _chain!.GetPlanStatsAsync(id, "");
                        subCount = stats.TotalOnChain ?? stats.SubscriberCount;
                        if (subCount == 0) return;
                    }
                    catch { return; }

                    // Get node count
                    int nodeCount = 0;
                    try
                    {
                        var nodes = await _chain.GetPlanNodesAsync(id);
                        nodeCount = nodes.Count;
                    }
                    catch { }

                    // Check user's subscription
                    var userSub = subs.FirstOrDefault(s => s.PlanId == id.ToString() && s.Status.Contains("active", StringComparison.OrdinalIgnoreCase));
                    if (userSub?.Price != null)
                    {
                        priceUdvpn = userSub.Price.QuoteValue ?? userSub.Price.BaseValue;
                        priceDisplay = $"{FormatP2P(priceUdvpn)} P2P";
                    }

                    // Calculate expiry
                    string? expiresAt = userSub?.InactiveAt;
                    string? expiresDisplay = null;
                    if (expiresAt != null && DateTime.TryParse(expiresAt, out var expiry))
                    {
                        var left = expiry - DateTime.UtcNow;
                        if (left.TotalDays > 1) expiresDisplay = $"{(int)left.TotalDays}d left";
                        else if (left.TotalHours > 1) expiresDisplay = $"{(int)left.TotalHours}h left";
                        else if (left.TotalMinutes > 0) expiresDisplay = $"{(int)left.TotalMinutes}m left";
                        else expiresDisplay = "expired";
                    }

                    found.Add(new PlanInfo
                    {
                        Id = id,
                        Subscribers = subCount,
                        NodeCount = nodeCount,
                        PriceDisplay = priceDisplay ?? "—",
                        PriceUdvpn = priceUdvpn ?? "0",
                        IsSubscribed = userSub != null,
                        SubscriptionId = userSub?.Id,
                        HasFeeGrant = grants.Count > 0,
                        ExpiresAt = expiresAt,
                        ExpiresDisplay = expiresDisplay,
                    });
                }
                catch { }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);

            result = found.OrderBy(p => p.Id).ToList();
            OnLog?.Invoke($"Found {result.Count} plans ({result.Count(p => p.IsSubscribed)} subscribed)");
            return result;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Plan discovery error: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> SubscribeToPlanAsync(int planId)
    {
        try
        {
            await EnsureChainAsync();
            OnLog?.Invoke($"Subscribing to Plan #{planId}...");
            var msg = MessageBuilder.StartSubscription(_wallet!.Address, (ulong)planId);
            var txBuilder = new TransactionBuilder(_wallet, _chain!);
            var tx = await txBuilder.BroadcastAsync(msg);
            OnLog?.Invoke($"Subscribe TX: {tx.TxHash} (code={tx.Code})");
            if (!tx.Success) { OnLog?.Invoke($"Failed: {tx.RawLog}"); return null; }

            await Task.Delay(5000);
            var subs = await _chain!.GetSubscriptionsAsync(_wallet!.Address);
            var match = subs.FirstOrDefault(s => s.PlanId == planId.ToString() && s.Status.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase));
            return match?.Id;
        }
        catch (Exception ex) { OnLog?.Invoke($"Subscribe error: {ex.Message}"); return null; }
    }

    public async Task<ConnectData?> ConnectViaPlanAsync(ulong subscriptionId, string nodeAddress)
    {
        if (_wallet == null) throw new InvalidOperationException("No wallet loaded");

        // Disconnect existing connection first
        try { if (_vpn != null) await _vpn.DisconnectAsync(); } catch { }

        // Clean up stale WireGuard tunnel
        try
        {
            var wgExe = FindBinary("wireguard.exe") ?? "wireguard.exe";
            var psi = new System.Diagnostics.ProcessStartInfo(wgExe, "/uninstalltunnelservice wgsent0")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }

        // Detect fee grant
        string? feeGranter = null;
        try
        {
            var grants = await _chain!.QueryFeeGrantsAsync(_wallet.Address);
            if (grants.Count > 0) feeGranter = grants[0].Granter;
        }
        catch { }

        _vpn?.Dispose();
        _vpn = new SentinelVpnClient(_wallet, new SentinelVpnOptions
        {
            FullTunnel = true,
            SystemProxy = true,
            V2RayExePath = FindBinary("v2ray.exe"),
            Dns = Settings.GetDnsString(),
            FeeGranter = feeGranter,
        });
        _vpn.Progress += (_, e) => OnProgress?.Invoke(e.Step, e.Detail);
        _vpn.Connected += (_, e) => OnLog?.Invoke($"Connected to {e.Result.NodeAddress}");
        _vpn.Disconnected += (_, e) => OnLog?.Invoke($"Disconnected: {e.Reason}");
        _vpn.Error += (_, e) => OnLog?.Invoke($"SDK error: {e.Exception.Message}");

        try
        {
            if (feeGranter != null) OnLog?.Invoke($"Fee grant: {feeGranter} pays gas");
            var r = await _vpn.ConnectViaSubscriptionAsync(subscriptionId, nodeAddress);
            return MapResult(r);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Plan connect error: {ex.Message}");
            throw;
        }
    }

    // ─── Node Testing ───

    public async Task<NodeTestResult> TestNodeAsync(string nodeAddress, HnsNodeInfo? nodeInfo = null, CancellationToken ct = default)
    {
        if (_wallet == null) throw new InvalidOperationException("No wallet loaded");
        var result = new NodeTestResult
        {
            Address = nodeAddress,
            Moniker = nodeInfo?.Moniker,
            Country = nodeInfo?.Country,
            City = nodeInfo?.City,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SentinelVpnClient? testVpn = null;
        try
        {
            await EnsureChainAsync();

            // Phase 0: Pre-check node online + get peers
            OnLog?.Invoke($"[TEST] Checking {nodeAddress[..20]}...");
            try
            {
                var chainNode = await _chain!.GetNodeAsync(nodeAddress);
                if (chainNode != null)
                {
                    var url = chainNode.RemoteUrl ?? chainNode.RemoteAddrs?.FirstOrDefault();
                    if (url != null)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var status = await NodeClient.GetStatusAsync(url, ct: cts.Token);
                        result.Protocol = status.Type;
                        result.Moniker = status.Moniker;
                        result.Country = status.Location?.Country;
                        result.City = status.Location?.City;
                        result.Peers = status.Peers;
                        result.ReportedBandwidth = status.Bandwidth?.Download;
                        OnLog?.Invoke($"[TEST] Online: {status.Type}, {status.Peers} peers, {status.Location?.Country}");
                    }
                }
            }
            catch (Exception ex) { OnLog?.Invoke($"[TEST] Pre-check failed: {ex.Message}"); }

            if (ct.IsCancellationRequested) { result.Error = "Stopped"; return result; }

            // Phase 1: Connect
            OnLog?.Invoke($"[TEST] Connecting...");

            // Cleanup stale tunnels
            try
            {
                var wgExe = FindBinary("wireguard.exe") ?? "wireguard.exe";
                var psi = new System.Diagnostics.ProcessStartInfo(wgExe, "/uninstalltunnelservice wgsent0")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
            }
            catch { }

            // Create dedicated VPN client for testing
            testVpn = new SentinelVpnClient(_wallet, new SentinelVpnOptions
            {
                FullTunnel = true,
                SystemProxy = true,
                V2RayExePath = FindBinary("v2ray.exe"),
                Dns = Settings.GetDnsString(),
                ForceNewSession = true,
                Gigabytes = 1,
            });
            testVpn.Progress += (_, e) => OnLog?.Invoke($"[TEST] {e.Detail ?? e.Step}");

            // Retry connect on transient errors (sequence mismatch, invalid session)
            Sentinel.SDK.Node.ConnectionResult? connectResult = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    connectResult = await testVpn.ConnectAsync(nodeAddress);
                    break;
                }
                catch (Exception retryEx) when (attempt == 0 &&
                    (retryEx.Message.Contains("sequence mismatch", StringComparison.OrdinalIgnoreCase)
                    || retryEx.Message.Contains("invalid session", StringComparison.OrdinalIgnoreCase)
                    || retryEx.Message.Contains("code 105", StringComparison.OrdinalIgnoreCase)))
                {
                    OnLog?.Invoke($"[TEST] Retry: {retryEx.Message[..Math.Min(60, retryEx.Message.Length)]}");
                    await Task.Delay(5000);
                    // Dispose and recreate testVpn for fresh state
                    try { await testVpn.DisconnectAsync(); } catch { }
                    try { testVpn.Dispose(); } catch { }
                    testVpn = new SentinelVpnClient(_wallet, new SentinelVpnOptions
                    {
                        FullTunnel = true, SystemProxy = true,
                        V2RayExePath = FindBinary("v2ray.exe"),
                        Dns = Settings.GetDnsString(),
                        ForceNewSession = true, Gigabytes = 1,
                    });
                    testVpn.Progress += (_, e) => OnLog?.Invoke($"[TEST] {e.Detail ?? e.Step}");
                }
            }
            if (connectResult == null) throw new Exception("Connect failed after retry");

            result.Connected = true;
            result.Protocol = connectResult.ServiceType;
            result.SessionId = connectResult.SessionId;
            result.ConnectSeconds = sw.Elapsed.TotalSeconds;
            result.Transport = connectResult.ServiceType?.Contains("v2ray", StringComparison.OrdinalIgnoreCase) == true
                ? "V2" : "WG";

            // SDK already waited 5s + verified tunnel via ipify.org (15s timeout)
            var verified = connectResult.Verification?.Working ?? false;
            OnLog?.Invoke($"[TEST] Connected via {result.Protocol} in {result.ConnectSeconds:F1}s (verified: {verified})");

            // If SDK verification failed, tunnel is broken — skip speed test
            if (!verified)
            {
                result.SpeedMbps = 0;
                result.SpeedMethod = "tunnel-not-verified";
                result.Pass = false;
                result.Error = "Tunnel connected but traffic not routing (SDK verification failed)";
                OnLog?.Invoke("[TEST] FAIL — tunnel not routing traffic");
            }
            else if (!ct.IsCancellationRequested)
            {
                // Phase 2: Speed Test
                OnLog?.Invoke("[TEST] Running speed test...");
                var speed = await RunSpeedTestAsync(connectResult.ServiceType, connectResult.SocksPort, CancellationToken.None);
                result.SpeedMbps = speed.Mbps;
                result.SpeedMethod = speed.Method;
                OnLog?.Invoke($"[TEST] Speed: {speed.Mbps:F1} Mbps ({speed.Method}, {speed.Chunks} chunks)");
            }

            if (verified && !ct.IsCancellationRequested)
            {
                // Phase 3: Google accessibility check
                OnLog?.Invoke("[TEST] Checking Google accessibility...");
                var (googleOk, googleMs) = await CheckGoogleAsync(connectResult.ServiceType, connectResult.SocksPort, CancellationToken.None);
                result.GoogleAccessible = googleOk;
                result.GoogleLatencyMs = googleMs;
                OnLog?.Invoke($"[TEST] Google: {(googleOk ? $"OK ({googleMs}ms)" : "BLOCKED")}");
            }

            // Verdict (only set if not already set by tunnel-not-verified path)
            if (result.Error == null)
            {
                result.Pass = result.Connected && (result.SpeedMbps ?? 0) >= 1.0;
                if (result.Connected) OnLog?.Invoke($"[TEST] {(result.Pass ? "PASS" : "FAIL")} — {result.SpeedMbps:F1} Mbps");
            }
        }
        catch (OperationCanceledException)
        {
            result.Error = "Cancelled";
            OnLog?.Invoke("[TEST] Cancelled");
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.ConnectSeconds = sw.Elapsed.TotalSeconds;
            OnLog?.Invoke($"[TEST] FAILED: {ex.Message}");
        }
        finally
        {
            // Always cleanup the test VPN client
            if (testVpn != null)
            {
                try { await testVpn.DisconnectAsync(); } catch { }
                try { testVpn.Dispose(); } catch { }
            }
            try
            {
                var wgExe = FindBinary("wireguard.exe") ?? "wireguard.exe";
                var psi = new System.Diagnostics.ProcessStartInfo(wgExe, "/uninstalltunnelservice wgsent0")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
            }
            catch { }
        }
        result.Timestamp = DateTime.UtcNow;

        // Log failures to JSONL (matches Node Tester's failures.jsonl format)
        if (!result.Pass)
        {
            try
            {
                var failLog = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HandshakeDVPN", "test-failures.jsonl");
                var dir = System.IO.Path.GetDirectoryName(failLog)!;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var entry = System.Text.Json.JsonSerializer.Serialize(new
                {
                    ts = result.Timestamp.ToString("o"),
                    node = result.Address,
                    moniker = result.Moniker ?? "",
                    peers = result.Peers ?? 0,
                    type = result.Protocol ?? "",
                    error = result.Error ?? "Speed < 1 Mbps",
                    country = result.Country ?? "",
                    city = result.City ?? "",
                    connectSeconds = result.ConnectSeconds,
                    speedMbps = result.SpeedMbps,
                });
                System.IO.File.AppendAllText(failLog, entry + "\n");
            }
            catch { }
        }

        return result;
    }

    // ─── Speed Test Constants ───
    private const double PROBE_CUTOFF_MBPS = 3.0;
    private const double PASS_THRESHOLD_MBPS = 1.0;
    private const double MIN_THROUGHPUT = 0.01;
    private const int MULTI_REQUEST_CHUNKS = 5;
    private const int V2RAY_CONNECTIVITY_ATTEMPTS = 3;
    private const int V2RAY_CONNECTIVITY_DELAY_MS = 5000;

    // ─── Baseline measurement ───
    private double? _lastBaselineMbps;

    public async Task<double> MeasureBaselineAsync()
    {
        OnLog?.Invoke("[TEST] Measuring baseline speed (direct internet, no tunnel)...");
        var (mbps, method, chunks) = await RunSpeedTestAsync(null, null, CancellationToken.None);
        _lastBaselineMbps = mbps;
        OnLog?.Invoke($"[TEST] Baseline: {mbps:F1} Mbps ({method})");
        return mbps;
    }

    public double? LastBaseline => _lastBaselineMbps;

    // ─── Speed Test (matches Node Tester protocol/speedtest.js) ───

    private static readonly string[] SPEED_TARGETS = {
        "https://speed.cloudflare.com/__down?bytes=1048576",
        "https://proof.ovh.net/files/1Mb.dat",
        "https://speedtest.tele2.net/1MB.zip",
    };

    private static readonly string[] CONNECTIVITY_TARGETS = {
        "https://www.google.com",
        "https://www.cloudflare.com",
        "https://1.1.1.1/cdn-cgi/trace",
        "https://httpbin.org/ip",
        "https://ifconfig.me",
        "http://ip-api.com/json",
    };

    private static HttpClient MakeClient(bool isV2Ray, int? socksPort, int timeoutSec = 30)
    {
        // CRITICAL: Fresh client per request for V2Ray (connection reuse fails with SOCKS5)
        if (isV2Ray && socksPort > 0)
        {
            var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true,
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        }
        return new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
    }

    private static async Task<(double Mbps, string Method, int Chunks)> RunSpeedTestAsync(
        string? serviceType, int? socksPort, CancellationToken ct)
    {
        var isV2Ray = (serviceType ?? "").Contains("v2ray", StringComparison.OrdinalIgnoreCase);

        // ─── Phase 0: V2Ray connectivity pre-check ───
        // SOCKS5 binding is async — proxy may not be ready even after port accepts TCP.
        // SDK already verifies WireGuard tunnel (5s wait + ipify.org check in ConnectAsync),
        // but V2Ray SOCKS5 still needs explicit pre-check before speed test.
        if (isV2Ray)
        {
            bool tunnelConnected = false;
            for (int attempt = 0; attempt < 3 && !tunnelConnected; attempt++)
            {
                if (attempt > 0) await Task.Delay(5000);
                foreach (var target in CONNECTIVITY_TARGETS)
                {
                    try
                    {
                        using var c = MakeClient(true, socksPort, 15);
                        var resp = await c.GetAsync(target, ct);
                        tunnelConnected = true;
                        break;
                    }
                    catch { }
                }
            }
            if (!tunnelConnected) return (0, "no-connectivity", 0);
        }

        // ─── Phase 1: 1MB probe ───
        double probeMbps = 0;
        string? usedTarget = null;

        // Try each target with fresh client
        foreach (var target in SPEED_TARGETS)
        {
            try
            {
                using var c = MakeClient(isV2Ray, socksPort, 30);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var data = await c.GetByteArrayAsync(target, ct);
                sw.Stop();
                probeMbps = (data.Length * 8.0 / 1_000_000) / sw.Elapsed.TotalSeconds;
                usedTarget = target;
                break;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Speed target {target} failed: {ex.Message}"); continue; }
        }

        // Rescue: retry Cloudflare with 60s timeout
        if (probeMbps < MIN_THROUGHPUT)
        {
            try
            {
                using var c = MakeClient(isV2Ray, socksPort, 60);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var data = await c.GetByteArrayAsync(SPEED_TARGETS[0], ct);
                sw.Stop();
                probeMbps = (data.Length * 8.0 / 1_000_000) / sw.Elapsed.TotalSeconds;
                usedTarget = SPEED_TARGETS[0];
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Rescue failed: {ex.Message}"); }
        }

        // Small file fallback: 100KB from multiple sources (DNS/routing may block large downloads)
        if (probeMbps < MIN_THROUGHPUT)
        {
            var smallTargets = new[] {
                "https://1.1.1.1/cdn-cgi/trace", // IP-based, no DNS needed
                "http://ip-api.com/json",
                "https://httpbin.org/bytes/102400",
            };
            foreach (var target in smallTargets)
            {
                try
                {
                    using var c = MakeClient(isV2Ray, socksPort, 15);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var data = await c.GetByteArrayAsync(target, ct);
                    sw.Stop();
                    if (data.Length > 100 && sw.Elapsed.TotalSeconds > 0)
                    {
                        probeMbps = Math.Max(0.05, (data.Length * 8.0 / 1_000_000) / sw.Elapsed.TotalSeconds);
                        return (probeMbps, "small-file-fallback", 1);
                    }
                }
                catch { continue; }
            }
        }

        // Google fallback: use Google page load as rough estimate
        if (probeMbps < MIN_THROUGHPUT)
        {
            try
            {
                using var c = MakeClient(isV2Ray, socksPort, 15);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var data = await c.GetByteArrayAsync("https://www.google.com", ct);
                sw.Stop();
                if (data.Length > 0 && sw.Elapsed.TotalSeconds > 0)
                {
                    probeMbps = Math.Max(0.1, (data.Length * 8.0 / 1_000_000) / sw.Elapsed.TotalSeconds);
                    return (probeMbps, "google-fallback", 1);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Google fallback failed: {ex.Message}"); }
        }

        // Connected but no throughput
        if (probeMbps < MIN_THROUGHPUT && isV2Ray) return (MIN_THROUGHPUT, "connected-no-throughput", 0);
        if (probeMbps < MIN_THROUGHPUT) return (0, "probe-failed", 0);

        // ─── Phase 2: Decision ───
        if (probeMbps < PROBE_CUTOFF_MBPS) return (probeMbps, "probe-only", 1);

        // ─── Phase 2: Multi-request (5 × 1MB) ───
        var totalBytes = 0L;
        var overallSw = System.Diagnostics.Stopwatch.StartNew();
        var chunks = 0;
        for (int i = 0; i < MULTI_REQUEST_CHUNKS; i++)
        {
            try
            {
                using var c = MakeClient(isV2Ray, socksPort, 30); // fresh client per chunk
                var data = await c.GetByteArrayAsync(usedTarget!, ct);
                totalBytes += data.Length;
                chunks++;
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Multi-request chunk {i} failed: {ex.Message}"); break; }
        }
        overallSw.Stop();

        if (chunks > 0)
        {
            var fullMbps = (totalBytes * 8.0 / 1_000_000) / overallSw.Elapsed.TotalSeconds;
            return (fullMbps, "multi-request", chunks);
        }

        // Multi-request failed but probe worked
        return (probeMbps, "probe-fallback", 1);
    }

    private static async Task<(bool ok, int ms)> CheckGoogleAsync(
        string? serviceType, int? socksPort, CancellationToken ct)
    {
        try
        {
            using var client = MakeClient(
                (serviceType ?? "").Contains("v2ray", StringComparison.OrdinalIgnoreCase),
                socksPort, 10);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resp = await client.GetAsync("https://www.google.com/generate_204", ct);
            sw.Stop();
            return (resp.IsSuccessStatusCode || (int)resp.StatusCode == 204, (int)sw.ElapsedMilliseconds);
        }
        catch { return (false, 0); }
    }

    // ─── Connection (direct — no plan) ───

    public async Task<ConnectData?> ConnectDirectAsync(string nodeAddress, int amount = 1, bool preferHourly = false)
    {
        if (_wallet == null) throw new InvalidOperationException("No wallet loaded");

        // Clean up any stale WireGuard tunnel before connecting
        try
        {
            var wgExe = FindBinary("wireguard.exe") ?? "wireguard.exe";
            var psi = new System.Diagnostics.ProcessStartInfo(wgExe, "/uninstalltunnelservice wgsent0")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { /* no tunnel to clean */ }

        // Recreate VPN client with user's payment + DNS choices
        _vpn?.Dispose();
        _vpn = new SentinelVpnClient(_wallet, new SentinelVpnOptions
        {
            FullTunnel = true,
            SystemProxy = true,
            V2RayExePath = FindBinary("v2ray.exe"),
            Dns = Settings.GetDnsString(),
            Gigabytes = preferHourly ? 1 : amount,
            PreferHourly = preferHourly,
            ForceNewSession = true, // Always create fresh session — stale sessions cause 404 on allocation
            // No fee granter — user pays own gas
        });
        _vpn.Progress += (_, e) => OnProgress?.Invoke(e.Step, e.Detail);
        _vpn.Connected += (_, e) => OnLog?.Invoke($"Connected to {e.Result.NodeAddress}");
        _vpn.Disconnected += (_, e) => OnLog?.Invoke($"Disconnected: {e.Reason}");
        _vpn.Error += (_, e) => OnLog?.Invoke($"SDK error: {e.Exception.Message}");

        try
        {
            var r = await _vpn.ConnectAsync(nodeAddress);
            return MapResult(r);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Connect error: {ex.Message}");
            if (_vpn.IsConnected)
            {
                var s = _vpn.GetStatus();
                return new ConnectData
                {
                    Status = "connected",
                    SessionId = s?.SessionId,
                    NodeAddress = s?.NodeAddress ?? nodeAddress,
                    Protocol = s?.ServiceType,
                };
            }
            throw;
        }
    }

    public async Task<StatusData?> DisconnectAsync()
    {
        if (_vpn != null) await _vpn.DisconnectAsync();
        return new StatusData { Status = "disconnected" };
    }

    public Task<VpnStatusData?> GetStatusAsync()
    {
        if (_vpn == null) return Task.FromResult<VpnStatusData?>(new VpnStatusData { Connected = false });
        var s = _vpn.GetStatus();
        if (s == null) return Task.FromResult<VpnStatusData?>(new VpnStatusData { Connected = false });

        return Task.FromResult<VpnStatusData?>(new VpnStatusData
        {
            Connected = s.Connected,
            NodeAddress = s.NodeAddress,
            SessionId = s.SessionId,
            Protocol = s.ServiceType,
            UptimeMs = (long)s.Uptime.TotalMilliseconds,
            UptimeFormatted = FormatUptime(s.Uptime),
        });
    }

    // ─── IP ───

    public async Task<string?> GetPublicIpAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            return (await client.GetStringAsync("https://api.ipify.org")).Trim();
        }
        catch { return null; }
    }

    // ─── Diagnostic ───

    public Task<DiagnosticData?> GetDiagnosticAsync()
    {
        return Task.FromResult<DiagnosticData?>(new DiagnosticData { Ok = true, Issues = new() });
    }

    // ─── Dispose ───

    public void Dispose()
    {
        try { _vpn?.DisconnectAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        _vpn?.Dispose();
        _chain?.Dispose();
        _wallet?.Dispose();
    }

    // ─── Helpers ───

    private static ConnectData MapResult(ConnectionResult r) => new()
    {
        Status = "connected",
        SessionId = r.SessionId,
        NodeAddress = r.NodeAddress,
        Protocol = r.ServiceType,
        SocksPort = r.SocksPort,
    };

    public static HnsNodeInfo ToNodeInfoStatic(ChainNode n, NodeStatus? s) => ToNodeInfo(n, s);

    private static HnsNodeInfo ToNodeInfo(ChainNode n, NodeStatus? s)
    {
        var gbPrice = n.GigabytePrices?.FirstOrDefault(p => p.Denom == "udvpn");
        var hrPrice = n.HourlyPrices?.FirstOrDefault(p => p.Denom == "udvpn");

        return new HnsNodeInfo
        {
            Address = n.Address,
            Moniker = s?.Moniker,
            Country = s?.Location?.Country,
            City = s?.Location?.City,
            ServiceType = s?.Type,
            Peers = s?.Peers,
            ClockDriftSec = s?.ClockDriftSec,
            BandwidthDown = s?.Bandwidth?.Download,
            BandwidthUp = s?.Bandwidth?.Upload,
            GbPriceUdvpn = gbPrice?.QuoteValue ?? gbPrice?.BaseValue,
            GbPriceDisplay = gbPrice != null ? FormatP2P(gbPrice.QuoteValue ?? gbPrice.BaseValue) : null,
            HourlyPriceUdvpn = hrPrice?.QuoteValue ?? hrPrice?.BaseValue,
            HourlyPriceDisplay = hrPrice != null ? FormatP2P(hrPrice.QuoteValue ?? hrPrice.BaseValue) : null,
        };
    }

    public static string FormatP2PPublic(string udvpnStr) => FormatP2P(udvpnStr);

    private static string FormatP2P(string udvpnStr)
    {
        // Chain returns BaseValue as integer OR decimal string (e.g., "52573.099722991367791")
        // Parse as double, take integer part, divide by 1M
        double raw;
        if (long.TryParse(udvpnStr, out var longVal))
            raw = longVal;
        else if (double.TryParse(udvpnStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dblVal))
            raw = Math.Floor(dblVal); // take integer part of the udvpn value
        else
            return "?";

        var p2p = raw / 1_000_000.0;
        if (p2p >= 100) return $"{(int)p2p}";
        if (p2p >= 10) return $"{p2p:F1}".TrimEnd('0').TrimEnd('.');
        if (p2p >= 1) return $"{p2p:F2}".TrimEnd('0').TrimEnd('.');
        if (p2p >= 0.01) return $"{p2p:F2}";
        if (p2p >= 0.001) return $"{p2p:F3}";
        if (p2p >= 0.0001) return $"{p2p:F4}";
        if (p2p > 0) return $"{p2p:G3}";
        return "0";
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    public static string? FindBinaryPublic(string name) => FindBinary(name);

    private static string? FindBinary(string name)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bin", name),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "bin", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Sentinel SDK", "daemon", "bin", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "sentinel-node-tester", "bin", name),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ─── SDK Logger Bridge ───

    private class BridgeLogger : ISdkLogger
    {
        private readonly Action<string> _log;
        public BridgeLogger(Action<string> log) => _log = log;
        public void Debug(string message) { }
        public void Info(string message) => _log(message);
        public void Warn(string message) => _log($"WARN: {message}");
        public void Error(string message, Exception? ex = null) => _log($"ERROR: {message}");
    }
}
