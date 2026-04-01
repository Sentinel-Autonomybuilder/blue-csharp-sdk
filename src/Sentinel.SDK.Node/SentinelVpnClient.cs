using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sentinel.SDK.Core;
using Sentinel.SDK.Tunnel.V2Ray;
using Sentinel.SDK.Tunnel.WireGuard;

namespace Sentinel.SDK.Node;

// ─── Options & Config Records ────────────────────────────────────────────────

/// <summary>
/// Configuration options for <see cref="SentinelVpnClient"/>.
/// </summary>
public record SentinelVpnOptions
{
    /// <summary>LCD (REST API) endpoint URLs. Falls back to <see cref="Constants.DefaultLcdUrls"/> if null.</summary>
    public string[]? LcdUrls { get; init; }

    /// <summary>RPC endpoint URLs. Falls back to <see cref="Constants.DefaultRpcUrls"/> if null.</summary>
    public string[]? RpcUrls { get; init; }

    /// <summary>When true, route all traffic through the tunnel (default). When false, split-tunnel mode.</summary>
    public bool FullTunnel { get; init; } = true;

    /// <summary>When true, configure the system SOCKS5 proxy for V2Ray connections.</summary>
    public bool SystemProxy { get; init; } = true;

    /// <summary>Full path to v2ray.exe. Required only when connecting to V2Ray nodes.</summary>
    public string? V2RayExePath { get; init; }

    /// <summary>Number of gigabytes to subscribe for when creating a new session (default: 1). Ignored when Hours is set.</summary>
    public int Gigabytes { get; init; } = 1;

    /// <summary>
    /// Hours to purchase (e.g. 1, 4, 8, 24). When set (> 0), uses hourly pricing instead of per-GB.
    /// Takes precedence over Gigabytes and PreferHourly.
    /// </summary>
    public int Hours { get; init; } = 0;

    /// <summary>
    /// Prefer hourly sessions when cheaper than per-GB (default: false). Ignored when Hours is explicitly set.
    /// When true, checks if the node offers hourly_prices with udvpn denom
    /// and uses hours-based session if the hourly price is lower than the gigabyte price.
    /// </summary>
    public bool PreferHourly { get; init; } = false;

    /// <summary>
    /// When true, always create a new session even if an existing active session is found.
    /// When false (default), reuse existing active sessions to avoid unnecessary payment.
    /// </summary>
    public bool ForceNewSession { get; init; } = false;

    /// <summary>
    /// Optional logger for SDK diagnostics. Defaults to <see cref="ConsoleSdkLogger"/> if null.
    /// Pass <see cref="NullSdkLogger"/> to suppress all output.
    /// </summary>
    public ISdkLogger? Logger { get; init; }

    /// <summary>
    /// Plan owner's address (sent1...) to use as fee granter.
    /// When set, the TX includes fee.granter so the plan operator pays gas.
    /// The plan operator is responsible for issuing fee grants to subscribers.
    /// If null, user pays their own gas.
    /// </summary>
    public string? FeeGranter { get; init; }

    /// <summary>
    /// Optional TOFU (Trust-On-First-Use) trust store for TLS certificate pinning during handshakes.
    /// When provided, the handshake validates node certificates against previously-seen fingerprints,
    /// protecting against MITM attacks after the initial connection.
    /// When null, handshakes accept all TLS certificates (a warning is logged).
    /// </summary>
    public TofuTrustStore? TofuStore { get; init; }

    /// <summary>
    /// DNS servers for WireGuard tunnel.
    /// <para>
    /// Set a preset name: "handshake" (default), "google", or "cloudflare".
    /// Or set a custom DNS string like "9.9.9.9, 149.112.112.112".
    /// </para>
    /// <para>
    /// Handshake DNS (103.196.38.38/39) is the default — decentralized, censorship-resistant.
    /// </para>
    /// </summary>
    public string? Dns { get; init; }
}

/// <summary>
/// Options for <see cref="SentinelVpnClient.ConnectAutoAsync"/>.
/// </summary>
public record ConnectAutoOptions
{
    /// <summary>ISO 3166-1 alpha-2 country codes to filter by (e.g. ["DE", "US"]). Null = any country.</summary>
    public string[]? Countries { get; init; }

    /// <summary>Service type filter: "wireguard" or "v2ray". Null = any type.</summary>
    public string? ServiceType { get; init; }

    /// <summary>Maximum number of nodes to attempt before giving up (default: 3).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>If set, only try connecting to these specific node addresses (sentnode1...).</summary>
    public string[]? NodePool { get; init; }
}

/// <summary>
/// Options for <see cref="SentinelVpnClient.QuickConnectAsync"/>.
/// </summary>
public record QuickConnectOptions
{
    /// <summary>ISO 3166-1 alpha-2 country codes to filter by (e.g. ["DE", "US"]). Null = any country.</summary>
    public string[]? Countries { get; init; }

    /// <summary>Service type filter: "wireguard" or "v2ray". Null = any type.</summary>
    public string? ServiceType { get; init; }

    /// <summary>Maximum number of nodes to attempt before giving up (default: 3).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>LCD (REST API) endpoint URLs. Falls back to <see cref="Constants.DefaultLcdUrls"/> if null.</summary>
    public string[]? LcdUrls { get; init; }

    /// <summary>RPC endpoint URLs. Falls back to <see cref="Constants.DefaultRpcUrls"/> if null.</summary>
    public string[]? RpcUrls { get; init; }

    /// <summary>Full path to v2ray.exe. Required only when connecting to V2Ray nodes.</summary>
    public string? V2RayExePath { get; init; }

    /// <summary>Number of gigabytes to subscribe for when creating a new session (default: 1). Ignored when Hours is set.</summary>
    public int Gigabytes { get; init; } = 1;

    /// <summary>Hours to purchase (e.g. 1, 4, 8, 24). When set (> 0), uses hourly pricing. Takes precedence over Gigabytes.</summary>
    public int Hours { get; init; } = 0;

    /// <summary>Prefer hourly sessions when cheaper than per-GB (default: false). Ignored when Hours is set.</summary>
    public bool PreferHourly { get; init; } = false;

    /// <summary>When true, route all traffic through the tunnel (default). When false, split-tunnel mode.</summary>
    public bool FullTunnel { get; init; } = true;

    /// <summary>When true, configure the system SOCKS5 proxy for V2Ray connections.</summary>
    public bool SystemProxy { get; init; } = true;

    /// <summary>If set, only try connecting to these specific node addresses (sentnode1...).</summary>
    public string[]? NodePool { get; init; }

    /// <summary>Optional logger for SDK diagnostics. Defaults to console if null.</summary>
    public ISdkLogger? Logger { get; init; }

    /// <summary>Fee granter address. When set, the granter pays gas fees for all transactions.</summary>
    public string? FeeGranter { get; init; }
}

/// <summary>
/// Result of <see cref="SentinelVpnClient.QuickConnectAsync"/>.
/// Wraps the <see cref="ConnectionResult"/> and provides a disposable handle
/// for disconnecting and cleaning up all resources.
/// </summary>
public sealed class QuickConnectResult : IDisposable, IAsyncDisposable
{
    private SentinelVpnClient? _client;

    internal QuickConnectResult(ConnectionResult connection, SentinelVpnClient client)
    {
        Connection = connection;
        _client = client;
    }

    /// <summary>Details of the established VPN connection.</summary>
    public ConnectionResult Connection { get; }

    /// <summary>The underlying VPN client for advanced operations (status, diagnostics).</summary>
    public SentinelVpnClient? Client => _client;

    /// <summary>
    /// Disconnect and dispose all resources (tunnel, wallet, chain client).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return;

        try
        {
            await client.DisconnectAsync();
        }
        catch
        {
            // Suppress — disposal must not throw.
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Synchronous dispose — disconnects and cleans up all resources.
    /// </summary>
    public void Dispose()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return;

        client.Dispose(); // Handles disconnect internally
    }
}

// ─── Result Records ──────────────────────────────────────────────────────────

/// <summary>
/// Result of a successful VPN connection.
/// </summary>
public record ConnectionResult
{
    /// <summary>On-chain session ID.</summary>
    public string SessionId { get; init; } = "";

    /// <summary>Node address connected to (sentnode1...).</summary>
    public string NodeAddress { get; init; } = "";

    /// <summary>Service type of the connection ("wireguard" or "v2ray").</summary>
    public string ServiceType { get; init; } = "";

    /// <summary>Local SOCKS5 proxy port (V2Ray only; null for WireGuard).</summary>
    public int? SocksPort { get; init; }

    /// <summary>SOCKS5 proxy username (V2Ray only; null for WireGuard).</summary>
    public string? SocksUser { get; init; }

    /// <summary>SOCKS5 proxy password (V2Ray only; null for WireGuard).</summary>
    public string? SocksPass { get; init; }

    /// <summary>Assigned VPN IP address (WireGuard only; null for V2Ray).</summary>
    public string? VpnIp { get; init; }

    /// <summary>Post-connection verification result, or null if verification was not performed.</summary>
    public ConnectionVerification? Verification { get; init; }
}

/// <summary>
/// Result of a post-connection IP verification check.
/// </summary>
/// <param name="Working">True if the VPN tunnel is routing traffic (IP check succeeded).</param>
/// <param name="VpnIp">Public IP address as seen through the tunnel, or null if the check failed.</param>
public record ConnectionVerification(bool Working, string? VpnIp);

/// <summary>
/// Comprehensive diagnostic snapshot of the current connection state.
/// All fields are best-effort — the method never throws, even if individual checks fail.
/// </summary>
/// <param name="ServiceRunning">Whether the WireGuard tunnel service is in RUNNING state (Windows only).</param>
/// <param name="InterfaceExists">Whether the tunnel network interface exists on the system.</param>
/// <param name="PeerHandshakeComplete">Whether a WireGuard peer handshake has occurred (latest-handshake > 0).</param>
/// <param name="BytesReceived">Total bytes received through the tunnel, or -1 if unavailable.</param>
/// <param name="BytesSent">Total bytes sent through the tunnel, or -1 if unavailable.</param>
/// <param name="SystemProxySet">Whether the system SOCKS5 proxy is currently configured (Windows registry).</param>
/// <param name="DnsWorking">Whether DNS resolution succeeds (resolves sentinel.co).</param>
/// <param name="TunnelName">The WireGuard tunnel interface name, or null for V2Ray connections.</param>
/// <param name="SocksPort">The local SOCKS5 proxy port for V2Ray connections, or null for WireGuard.</param>
/// <param name="LastError">The last error encountered during diagnostics, or null if all checks succeeded.</param>
public record ConnectionDiagnostics(
    bool ServiceRunning,
    bool InterfaceExists,
    bool PeerHandshakeComplete,
    long BytesReceived,
    long BytesSent,
    bool SystemProxySet,
    bool DnsWorking,
    string? TunnelName,
    int? SocksPort,
    string? LastError
);

/// <summary>
/// Current status of the VPN connection.
/// </summary>
public record ConnectionStatus
{
    /// <summary>Whether the tunnel is currently connected.</summary>
    public bool Connected { get; init; }

    /// <summary>Node address of the active connection, or null if disconnected.</summary>
    public string? NodeAddress { get; init; }

    /// <summary>On-chain session ID of the active connection, or null if disconnected.</summary>
    public string? SessionId { get; init; }

    /// <summary>Service type of the active connection, or null if disconnected.</summary>
    public string? ServiceType { get; init; }

    /// <summary>Duration since the connection was established.</summary>
    public TimeSpan Uptime { get; init; }
}

// ─── Event Args ──────────────────────────────────────────────────────────────

/// <summary>
/// Event args for progress updates during the connection flow.
/// </summary>
public class ProgressEventArgs : EventArgs
{
    /// <summary>Current step identifier (e.g. "wallet", "balance", "subscribe", "handshake", "tunnel").</summary>
    public string Step { get; init; } = "";

    /// <summary>Human-readable detail message.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// Event args emitted when a VPN connection is established.
/// </summary>
public class ConnectionEventArgs : EventArgs
{
    /// <summary>Details of the established connection.</summary>
    public ConnectionResult Result { get; init; } = new();
}

/// <summary>
/// Event args emitted when the VPN is disconnected.
/// </summary>
public class DisconnectedEventArgs : EventArgs
{
    /// <summary>Reason for the disconnection (e.g. "user", "error", "dispose").</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Event args emitted when an error occurs during connection or tunnel operation.
/// </summary>
public class ErrorEventArgs : EventArgs
{
    /// <summary>The exception that occurred.</summary>
    public Exception Exception { get; init; } = null!;
}

// ─── VPN Client ──────────────────────────────────────────────────────────────

/// <summary>
/// High-level connection orchestrator for Sentinel dVPN.
/// Manages the full flow: wallet setup, chain queries, session creation,
/// V3 handshake, and tunnel installation (WireGuard or V2Ray).
/// </summary>
/// <remarks>
/// Equivalent to the JS SDK's <c>connectDirect()</c>, <c>connectAuto()</c>,
/// <c>quickConnect()</c>, and <c>disconnect()</c>.
/// </remarks>
public class SentinelVpnClient : IDisposable, IAsyncDisposable
{
    // ─── Constants ───────────────────────────────────────────────────

    /// <summary>Seconds to wait after session TX for chain propagation.</summary>
    private const int CHAIN_PROPAGATION_DELAY_MS = 5_000;

    /// <summary>Default local SOCKS5 port for V2Ray.</summary>
    private const int DEFAULT_SOCKS_PORT = 10808;

    // ─── Fields ──────────────────────────────────────────────────────

    /// <summary>
    /// Shared <see cref="HttpClient"/> for lightweight HTTP requests (e.g. IP verification).
    /// Reused across all instances to prevent socket exhaustion.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly SentinelWallet _wallet;
    private readonly SentinelVpnOptions _options;
    private readonly ISdkLogger _logger;
    private readonly ChainClient _chainClient;
    private readonly TransactionBuilder _txBuilder;
    private readonly NodeCache _nodeCache = new();
    private readonly CircuitBreaker _circuitBreaker = new();
    private readonly TofuTrustStore? _tofuStore;

    /// <summary>Prevents concurrent connect calls from racing.</summary>
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    /// <summary>Monitors system network changes and triggers disconnect on change.</summary>
    private readonly NetworkMonitor _networkMonitor = new();

    private readonly Task _initTask;

    private WireGuardTunnel? _wgTunnel;
    private V2RayProcess? _v2RayProcess;
    private bool _systemProxySet;
    private ConnectionResult? _activeConnection;
    private DateTime _connectedAt;
    private bool _disposed;
    private Task? _pendingEndSession;

    // ─── Events ──────────────────────────────────────────────────────

    /// <summary>Raised during each step of the connection flow.</summary>
    public event EventHandler<ProgressEventArgs>? Progress;

    /// <summary>Raised when a VPN connection is successfully established.</summary>
    public event EventHandler<ConnectionEventArgs>? Connected;

    /// <summary>Raised when the VPN is disconnected.</summary>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Raised when an error occurs during connection or tunnel operation.</summary>
    public event EventHandler<ErrorEventArgs>? Error;

    // ─── Constructor ─────────────────────────────────────────────────

    /// <summary>
    /// Create a new Sentinel VPN client with the given wallet and optional configuration.
    /// </summary>
    /// <param name="wallet">Wallet used for signing transactions and handshakes.</param>
    /// <param name="options">Optional configuration (endpoints, tunnel mode, etc.).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="wallet"/> is null.</exception>
    public SentinelVpnClient(SentinelWallet wallet, SentinelVpnOptions? options = null)
    {
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        _options = options ?? new SentinelVpnOptions();
        _logger = _options.Logger ?? new ConsoleSdkLogger();

        _chainClient = new ChainClient(_options.LcdUrls, _options.RpcUrls, _logger);
        _txBuilder = new TransactionBuilder(_wallet, _chainClient);
        _tofuStore = _options.TofuStore;

        if (_tofuStore is null)
        {
            _logger.Warn("No TofuTrustStore configured — handshakes will accept ALL TLS certificates. " +
                "Pass a TofuTrustStore via SentinelVpnOptions.TofuStore for MITM protection.");
        }

        // Fire-and-forget LCD endpoint health probing + fee grant detection
        _initTask = Task.Run(async () =>
        {
            try { await _chainClient.InitializeAsync(); }
            catch { /* Non-fatal — endpoint order stays as configured */ }

            // Fee grant: app sets the plan owner's address. The plan operator
            // is responsible for issuing fee grants to subscribers.
            // We just include it in TXs — if grant exists, gas is free.
            if (_options.FeeGranter != null)
            {
                _txBuilder.FeeGranter = _options.FeeGranter;
                _logger.Info($"Fee granter set: {_options.FeeGranter}");
            }
        });

        // Disconnect on network change to prevent stale tunnels
        _networkMonitor.NetworkChanged += OnNetworkChanged;
    }

    private async void OnNetworkChanged(object? sender, NetworkChangedEventArgs e)
    {
        if (_activeConnection is null) return;

        _logger.Info($"Network changed ({e.Reason}) — disconnecting stale tunnel.");

        try
        {
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Auto-disconnect on network change failed: {ex.Message}");
        }
    }

    // ─── Quick Connect (Static Convenience) ────────────────────────

    /// <summary>
    /// One-call convenience method: creates a wallet from mnemonic, builds a VPN client,
    /// connects to the best available node via <see cref="ConnectAutoAsync"/>, and returns
    /// a disposable handle for cleanup.
    /// </summary>
    /// <remarks>
    /// Equivalent to the JS SDK's <c>quickConnect()</c>. This is a static factory — it
    /// creates and owns the wallet and client internally. Dispose the returned
    /// <see cref="QuickConnectResult"/> to disconnect and release all resources.
    /// <para>
    /// Example usage:
    /// <code>
    /// await using var vpn = await SentinelVpnClient.QuickConnectAsync("word1 word2 ...");
    /// Console.WriteLine($"Connected: {vpn.Connection.NodeAddress}");
    /// // vpn is disconnected and cleaned up when disposed
    /// </code>
    /// </para>
    /// </remarks>
    /// <param name="mnemonic">BIP39 mnemonic phrase (12 or 24 words).</param>
    /// <param name="options">Optional connection preferences (countries, service type, endpoints, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="QuickConnectResult"/> with the connection details and a disposable cleanup handle.</returns>
    /// <exception cref="SentinelException">Thrown when the mnemonic is invalid, balance is insufficient, or no node can be reached.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public static async Task<QuickConnectResult> QuickConnectAsync(
        string mnemonic,
        QuickConnectOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mnemonic);

        var opts = options ?? new QuickConnectOptions();

        // ─── Step 1: Create wallet from mnemonic ───
        var wallet = SentinelWallet.FromMnemonic(mnemonic);

        // ─── Step 2: Create VPN client with options ───
        var vpnOptions = new SentinelVpnOptions
        {
            LcdUrls = opts.LcdUrls,
            RpcUrls = opts.RpcUrls,
            V2RayExePath = opts.V2RayExePath,
            Gigabytes = opts.Gigabytes,
            FullTunnel = opts.FullTunnel,
            SystemProxy = opts.SystemProxy,
            Logger = opts.Logger,
            FeeGranter = opts.FeeGranter,
        };

        var client = new SentinelVpnClient(wallet, vpnOptions);

        try
        {
            // ─── Step 3: Connect via auto-select ───
            var autoOpts = new ConnectAutoOptions
            {
                Countries = opts.Countries,
                ServiceType = opts.ServiceType,
                MaxAttempts = opts.MaxAttempts,
                NodePool = opts.NodePool,
            };

            var result = await client.ConnectAutoAsync(autoOpts, ct);

            return new QuickConnectResult(result, client);
        }
        catch
        {
            // If connection fails, dispose the client we created
            client.Dispose();
            throw;
        }
    }

    // ─── Connection ──────────────────────────────────────────────────

    /// <summary>
    /// Whether the client currently has an active VPN connection.
    /// </summary>
    public bool IsConnected => _activeConnection is not null;

    /// <summary>
    /// Connect to a specific node by address (direct pay-per-GB).
    /// </summary>
    /// <remarks>
    /// Flow:
    /// 1. Check wallet balance
    /// 2. Query node status to determine service type (WireGuard/V2Ray)
    /// 3. Create on-chain session via <c>MessageBuilder.StartSession()</c>
    /// 4. Wait for chain propagation (5 seconds)
    /// 5. Perform V3 handshake with the node
    /// 6. Install tunnel (WireGuard service or V2Ray SOCKS5 proxy)
    /// </remarks>
    /// <param name="nodeAddress">On-chain node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details including session ID, service type, and tunnel info.</returns>
    /// <exception cref="SentinelException">Thrown when balance is insufficient, node is unreachable, or tunnel fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<ConnectionResult> ConnectAsync(string nodeAddress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        _logger.Info($"Connecting to {nodeAddress}...");

        // Ensure LCD endpoints are sorted by latency before first use
        await _initTask.ConfigureAwait(false);

        // ─── Connection mutex — prevent concurrent connects ───
        if (!await _connectLock.WaitAsync(0, ct))
        {
            throw new SentinelException(
                ErrorCodes.ConnectionInProgress,
                "Connection already in progress"
            );
        }

        // Session ID tracked at method scope for poisoned-session marking in catch blocks.
        // Ported from js-sdk/node-connect.js line 993: let sessionId = null
        ulong sessionIdForCleanup = 0;

        try
        {
            if (_activeConnection is not null)
            {
                throw new SentinelException(
                    ErrorCodes.AlreadyConnected,
                    $"Already connected to {_activeConnection.NodeAddress}. Call DisconnectAsync() first."
                );
            }

            // ─── Step 0: Clean orphaned tunnels/processes from previous crashes ───
            // Ported from js-sdk/node-connect.js registerCleanupHandlers() → recoverOrphans()
            try
            {
                var pf = DependencyCheck.Preflight(new PreflightOptions { AutoClean = true });
                if (pf.Issues.Count > 0)
                {
                    foreach (var issue in pf.Issues)
                    {
                        if (issue.Severity == "error")
                            _logger.Error($"[preflight] {issue.Message}");
                        else
                            _logger.Warn($"[preflight] {issue.Message}");
                    }
                }
            }
            catch
            {
                // Best effort — preflight failures must not block connection
            }

            // ─── Step 1: Wallet setup ───
            EmitProgress("wallet", "Setting up wallet...");
            ct.ThrowIfCancellationRequested();

            // ─── Step 2: Check balance ───
            EmitProgress("balance", $"Checking balance for {_wallet.Address}...");
            var balance = await _chainClient.GetBalanceAsync(_wallet.Address, ct);
            ct.ThrowIfCancellationRequested();

            if (balance.Udvpn < 100_000)
            {
                throw new SentinelException(
                    "INSUFFICIENT_BALANCE",
                    $"Insufficient balance. Need at least 0.1 P2P for gas fees. Balance: {balance.Display}"
                );
            }

            EmitProgress("balance", $"Balance: {balance.Display}");

            // ─── Step 3: Query node status ───
            EmitProgress("node", $"Querying node {nodeAddress}...");
            var chainNode = await _chainClient.GetNodeAsync(nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            if (chainNode is null)
            {
                throw new SentinelException(
                    "NODE_NOT_FOUND",
                    $"Node {nodeAddress} not found on chain"
                );
            }

            if (chainNode.RemoteUrl is null)
            {
                throw new SentinelException(
                    "NODE_NO_URL",
                    $"Node {nodeAddress} has no remote URL"
                );
            }

            var nodeStatus = await NodeClient.GetStatusAsync(chainNode.RemoteUrl, _tofuStore, nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            var serviceType = nodeStatus.Type.ToLowerInvariant();
            EmitProgress("node", $"Node type: {serviceType}, peers: {nodeStatus.Peers}, location: {nodeStatus.Location.Country}");

            // ─── Pre-verify: node's address must match what we're paying for ───
            // Prevents wasting tokens when remote URL serves a different node.
            if (!string.IsNullOrEmpty(nodeStatus.Address) && nodeStatus.Address != nodeAddress)
            {
                throw new SentinelNodeException(
                    $"Node address mismatch: remote URL serves {nodeStatus.Address}, not {nodeAddress}. Tokens would be wasted — aborting before payment."
                );
            }

            // ─── Step 3a: Clock drift detection for V2Ray VMess ───
            // VMess AEAD is sensitive to clock drift >120s, causing silent connection drain.
            // VLess (proxy_protocol=1) is immune to clock drift — only VMess (2) fails.
            // Decision deferred to post-handshake: if VLess transports exist, strip VMess
            // and reorder; if VMess-only, throw CLOCK_DRIFT_TOO_HIGH.
            var extremeDrift = serviceType == "v2ray"
                && nodeStatus.ClockDriftSec.HasValue
                && Math.Abs(nodeStatus.ClockDriftSec.Value) > 120;

            // ─── Step 4: Validate V2Ray path if needed ───
            if (serviceType == "v2ray" && string.IsNullOrWhiteSpace(_options.V2RayExePath))
            {
                throw new SentinelException(
                    "V2RAY_PATH_REQUIRED",
                    "V2Ray node selected but V2RayExePath is not configured in SentinelVpnOptions"
                );
            }

            // ─── Fast Reconnect: Check saved credentials ───
            if (!_options.ForceNewSession)
            {
                var saved = CredentialStore.Load(nodeAddress);
                if (saved != null)
                {
                    _logger.Info($"Found saved credentials for {nodeAddress}, verifying session {saved.SessionId}...");
                    EmitProgress("cache", "Found cached credentials — verifying session...");

                    var cachedSessionId = await SessionManager.FindExistingSessionAsync(
                        _chainClient, _wallet.Address, nodeAddress, ct);

                    if (cachedSessionId.HasValue && cachedSessionId.Value.ToString() == saved.SessionId)
                    {
                        _logger.Info($"Session {saved.SessionId} still active — fast reconnecting (0 cost)");
                        EmitProgress("cache", "Session active — skipping payment and handshake");

                        // Go straight to tunnel setup with saved credentials
                        return await FastReconnectAsync(saved, nodeAddress, ct);
                    }
                    else
                    {
                        _logger.Info("Saved session expired — clearing credentials");
                        CredentialStore.Clear(nodeAddress);
                    }
                }
            }

            // ─── Step 5: Check for existing active session (skip payment) ───
            ulong sessionId;
            var reuseExisting = false;

            if (!_options.ForceNewSession)
            {
                EmitProgress("session", $"Checking for existing session with {nodeAddress}...");
                try
                {
                    var existingSessionId = await SessionManager.FindExistingSessionAsync(
                        _chainClient, _wallet.Address, nodeAddress, ct);

                    if (existingSessionId.HasValue)
                    {
                        // ── Poisoned session check ──
                        // Ported from js-sdk/node-connect.js line 998:
                        // if (sessionId && isSessionPoisoned(String(sessionId))) { sessionId = null; }
                        if (StateManager.IsSessionPoisoned(existingSessionId.Value.ToString()))
                        {
                            _logger.Warn($"Session {existingSessionId.Value} previously failed — skipping");
                            EmitProgress("session", $"Session {existingSessionId.Value} previously failed — creating new");
                            sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                        }
                        else
                        {
                            sessionId = existingSessionId.Value;
                            reuseExisting = true;
                            EmitProgress("session", $"Reusing existing session {sessionId} (no payment needed)");
                        }
                    }
                    else
                    {
                        sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Stale session or 404 on allocation query — fall through to create new
                    _logger.Warn($"Session reuse failed ({ex.Message}) — creating new session");
                    sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                }
            }
            else
            {
                sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
            }

            // Track session ID at method scope for catch-block poisoned-session marking
            sessionIdForCleanup = sessionId;

            // ─── Step 7: Chain propagation ───
            // NOTE: CreateNewSessionAsync already waits 5s for chain propagation before
            // querying the session ID. The handshake also has built-in chain-lag retry
            // (waits 10s and retries if the node returns "does not exist").
            // No additional delay needed here.

            // ─── Step 8: V3 handshake (with 409 retry) ───
            var handshakeType = serviceType == "wireguard"
                ? HandshakeType.WireGuard
                : HandshakeType.V2Ray;

            object handshakeResult;
            try
            {
                EmitProgress("handshake", $"Performing V3 handshake with {chainNode.RemoteUrl}...");
                handshakeResult = await Handshake.HandshakeAsync(
                    _wallet, chainNode.RemoteUrl, sessionId, handshakeType,
                    _tofuStore, nodeAddress, ct);
            }
            catch (SentinelHandshakeException ex) when (ex.Code == ErrorCodes.SessionExists)
            {
                // Session is poisoned — mark it and create a new one
                // Ported from js-sdk/node-connect.js line 1596:
                // markSessionPoisoned(String(sessionId), opts.nodeAddress, hsErr.message)
                StateManager.MarkSessionPoisoned(sessionId.ToString(), nodeAddress, ex.Message);
                _logger.Warn($"Retrying after session conflict (409) on {nodeAddress}");
                EmitProgress("handshake", "Session conflict (409). Creating new session and retrying...");
                sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                sessionIdForCleanup = sessionId; // Update for catch-block poisoning

                EmitProgress("propagation", "Waiting for chain propagation (5s)...");
                await Task.Delay(CHAIN_PROPAGATION_DELAY_MS, ct);

                EmitProgress("handshake", $"Retrying V3 handshake with session {sessionId}...");
                handshakeResult = await Handshake.HandshakeAsync(
                    _wallet, chainNode.RemoteUrl, sessionId, handshakeType,
                    _tofuStore, nodeAddress, ct);
            }

            ct.ThrowIfCancellationRequested();
            EmitProgress("handshake", "Handshake successful");

            // ─── Step 9: Install tunnel ───
            ConnectionResult result;

            if (handshakeResult is WireGuardHandshakeResult wgResult)
            {
                result = await InstallWireGuardTunnelAsync(wgResult, sessionId, nodeAddress, ct);
            }
            else if (handshakeResult is V2RayHandshakeResult v2Result)
            {
                result = await InstallV2RayTunnelAsync(
                    v2Result, sessionId, nodeAddress, chainNode.RemoteUrl,
                    extremeDrift, nodeStatus.ClockDriftSec, ct
                );
            }
            else
            {
                throw new SentinelException(
                    "UNKNOWN_SERVICE",
                    $"Unknown handshake result type: {handshakeResult.GetType().Name}"
                );
            }

            // ─── Step 11: Wait for WireGuard peer handshake, then verify ───
            await Task.Delay(5000, ct);
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(timeoutMs: 15000, ct: ct);
            result = result with { Verification = verification };

            if (verification.Working)
            {
                EmitProgress("verify", $"Tunnel verified, external IP: {verification.VpnIp}");
            }
            else
            {
                EmitProgress("verify", "Tunnel verification failed — IP check did not succeed");
            }

            // ─── Step 11: Save credentials for fast reconnect (AFTER tunnel verified) ───
            if (handshakeResult is WireGuardHandshakeResult wgHs)
            {
                CredentialStore.Save(nodeAddress, new SavedCredentials
                {
                    SessionId = sessionId.ToString(),
                    ServiceType = "wireguard",
                    NodeAddress = nodeAddress,
                    WgPrivateKey = Convert.ToBase64String(wgHs.ClientPrivateKey),
                    WgServerPubKey = wgHs.ServerPublicKey,
                    WgAssignedAddrs = wgHs.AssignedAddresses,
                    WgServerEndpoint = wgHs.ServerEndpoint,
                    SavedAt = DateTime.UtcNow.ToString("o"),
                });
            }
            else if (handshakeResult is V2RayHandshakeResult v2Hs)
            {
                var v2RayHost = new Uri(chainNode.RemoteUrl).Host;
                CredentialStore.Save(nodeAddress, new SavedCredentials
                {
                    SessionId = sessionId.ToString(),
                    ServiceType = "v2ray",
                    NodeAddress = nodeAddress,
                    V2RayUuid = v2Hs.Uuid,
                    V2RayTransport = v2Hs.Transport,
                    V2RayProtocol = v2Hs.ProxyProtocol,
                    V2RayTls = v2Hs.Tls,
                    V2RayPort = v2Hs.Port,
                    V2RayServerHost = v2RayHost,
                    SavedAt = DateTime.UtcNow.ToString("o"),
                });
            }

            // ─── Step 12: Finalize ───
            // Ported from js-sdk/node-connect.js line 1586:
            // markSessionActive(String(sessionId), opts.nodeAddress)
            StateManager.MarkSessionActive(sessionId.ToString(), nodeAddress);

            _activeConnection = result;
            _connectedAt = DateTime.UtcNow;

            EmitConnected(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SentinelException and not SentinelHandshakeException and not SentinelNodeException)
        {
            // Clear stale credentials on tunnel failure to prevent reuse of bad handshake data
            CredentialStore.Clear(nodeAddress);

            // Mark session as poisoned so it won't be reused
            // Ported from js-sdk/node-connect.js line 1596:
            // markSessionPoisoned(String(sessionId), opts.nodeAddress, hsErr.message)
            if (sessionIdForCleanup > 0)
            {
                StateManager.MarkSessionPoisoned(sessionIdForCleanup.ToString(), nodeAddress, ex.Message);
            }

            _logger.Error($"Connection failed to {nodeAddress}", ex);
            var wrapped = new SentinelException(
                "CONNECT_FAILED",
                $"Failed to connect to {nodeAddress}: {ex.Message}",
                ex
            );
            EmitError(wrapped);
            throw wrapped;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Auto-pick the best available node and connect.
    /// Queries online nodes, filters by country and service type, and attempts
    /// connection to up to <see cref="ConnectAutoOptions.MaxAttempts"/> nodes.
    /// </summary>
    /// <param name="options">Filter and retry options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details of the successful connection.</returns>
    /// <exception cref="SentinelException">Thrown when no suitable node can be connected after all attempts.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<ConnectionResult> ConnectAutoAsync(
        ConnectAutoOptions? options = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Ensure LCD endpoints are sorted by latency before first use
        await _initTask.ConfigureAwait(false);

        // ─── Connection mutex — prevent concurrent connects ───
        if (!await _connectLock.WaitAsync(0, ct))
        {
            throw new SentinelException(
                ErrorCodes.ConnectionInProgress,
                "Connection already in progress"
            );
        }

        var lockHeld = true;
        try
        {
            var autoOpts = options ?? new ConnectAutoOptions();
            var maxAttempts = Math.Max(1, autoOpts.MaxAttempts);
            var nodePool = autoOpts.NodePool;

            // ─── Step 1: Query online nodes (cached) ───
            EmitProgress("discovery", "Querying active nodes from chain...");
            var nodes = await _nodeCache.GetAsync(async () =>
                (IReadOnlyList<ChainNode>)await _chainClient.GetActiveNodesAsync(limit: 5000, ct: ct));
            ct.ThrowIfCancellationRequested();

            if (nodes.Count == 0)
            {
                throw new SentinelException("NO_NODES", "No active nodes found on chain");
            }

            EmitProgress("discovery", $"Found {nodes.Count} active nodes");

            // ─── Step 2: Filter candidates ───
            var candidates = new List<ChainNode>();

            foreach (var node in nodes)
            {
                if (node.RemoteUrl is null)
                {
                    continue;
                }

                // If NodePool is set, only include those specific addresses
                if (nodePool is not null && nodePool.Length > 0)
                {
                    var inPool = false;
                    foreach (var poolAddr in nodePool)
                    {
                        if (string.Equals(node.Address, poolAddr, StringComparison.OrdinalIgnoreCase))
                        {
                            inPool = true;
                            break;
                        }
                    }
                    if (!inPool) continue;
                }

                candidates.Add(node);
            }

            // ─── Step 3: Probe candidates in parallel ───
            var semaphore = new SemaphoreSlim(10); // max 10 parallel probes
            var probeTasks = candidates.Select(async node =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (_circuitBreaker.IsOpen(node.Address)) return (node, (NodeStatus?)null);
                    var status = await NodeClient.GetStatusAsync(node.RemoteUrl!, _tofuStore, node.Address, ct);
                    return (node, (NodeStatus?)status);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    _circuitBreaker.RecordFailure(node.Address);
                    return (node, (NodeStatus?)null);
                }
                finally { semaphore.Release(); }
            });
            var probed = await Task.WhenAll(probeTasks);

            // Apply country/service type/clock drift filters on probed results
            var filtered = new List<(ChainNode Node, NodeStatus Status)>();
            foreach (var (node, status) in probed)
            {
                if (status is null) continue;

                // Filter by service type
                if (autoOpts.ServiceType is not null &&
                    !status.Type.Equals(autoOpts.ServiceType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Filter by country — match against both country name and ISO alpha-2 code
                if (autoOpts.Countries is not null && autoOpts.Countries.Length > 0)
                {
                    var match = false;
                    foreach (var country in autoOpts.Countries)
                    {
                        if (status.Location.Country.Equals(country, StringComparison.OrdinalIgnoreCase)
                            || status.Location.CountryCode.Equals(country, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                            break;
                        }
                    }
                    if (!match) continue;
                }

                // Skip nodes with excessive clock drift (>120s)
                if (status.ClockDriftSec.HasValue && Math.Abs(status.ClockDriftSec.Value) > 120)
                {
                    continue;
                }

                filtered.Add((node, status));

                if (filtered.Count >= maxAttempts * 3)
                {
                    break; // Enough candidates
                }
            }

            if (filtered.Count == 0)
            {
                throw new SentinelException(
                    "NO_MATCHING_NODES",
                    "No nodes match the specified criteria (country, service type, node pool)"
                );
            }

            // ─── Step 4: Sort by fewest peers (least loaded) ───
            filtered.Sort((a, b) => a.Status.Peers.CompareTo(b.Status.Peers));

            // ─── Step 5: Try connecting to candidates ───
            // Release the lock before calling ConnectAsync (which acquires it)
            _connectLock.Release();
            lockHeld = false;

            var attempts = 0;
            Exception? lastException = null;

            foreach (var (node, status) in filtered)
            {
                if (attempts >= maxAttempts)
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();
                attempts++;

                EmitProgress("auto", $"Attempt {attempts}/{maxAttempts}: {node.Address} ({status.Type}, {status.Location.Country})");

                try
                {
                    var result = await ConnectAsync(node.Address, ct);
                    _circuitBreaker.Reset(node.Address);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Auto-connect attempt {attempts} failed for {node.Address}: {ex.Message}");
                    _circuitBreaker.RecordFailure(node.Address);
                    lastException = ex;
                    EmitProgress("auto", $"Attempt {attempts} failed: {ex.Message}");

                    // Clean up any partial state from the failed attempt
                    await CleanupTunnelsAsync();
                }
            }

            throw new SentinelException(
                ErrorCodes.AllNodesFailed,
                $"Failed to connect after {attempts} attempts. Last error: {lastException?.Message}",
                lastException!
            );
        }
        finally
        {
            // Only release if we still hold the lock (not yet released for ConnectAsync calls)
            if (lockHeld)
            {
                _connectLock.Release();
            }
        }
    }

    /// <summary>
    /// Connect to a node using an existing on-chain subscription.
    /// Skips the session creation TX and uses the existing subscription's session.
    /// </summary>
    /// <param name="subscriptionId">On-chain subscription ID.</param>
    /// <param name="nodeAddress">Node address to connect to (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details including session ID, service type, and tunnel info.</returns>
    /// <exception cref="SentinelException">Thrown when the subscription is invalid or connection fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<ConnectionResult> ConnectViaSubscriptionAsync(
        ulong subscriptionId,
        string nodeAddress,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        // Ensure LCD endpoints are sorted by latency before first use
        await _initTask.ConfigureAwait(false);

        // ─── Connection mutex — prevent concurrent connects ───
        if (!await _connectLock.WaitAsync(0, ct))
        {
            throw new SentinelException(
                ErrorCodes.ConnectionInProgress,
                "Connection already in progress"
            );
        }

        // Session ID tracked at method scope for catch-block poisoned-session marking
        ulong sessionIdForCleanup = 0;

        try
        {
            if (_activeConnection is not null)
            {
                throw new SentinelException(
                    ErrorCodes.AlreadyConnected,
                    $"Already connected to {_activeConnection.NodeAddress}. Call DisconnectAsync() first."
                );
            }

            // ─── Step 1: Wallet check ───
            EmitProgress("wallet", "Setting up wallet...");
            ct.ThrowIfCancellationRequested();

            // ─── Step 2: Query node ───
            EmitProgress("node", $"Querying node {nodeAddress}...");
            var chainNode = await _chainClient.GetNodeAsync(nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            if (chainNode is null)
            {
                throw new SentinelException("NODE_NOT_FOUND", $"Node {nodeAddress} not found on chain");
            }

            if (chainNode.RemoteUrl is null)
            {
                throw new SentinelException("NODE_NO_URL", $"Node {nodeAddress} has no remote URL");
            }

            var nodeStatus = await NodeClient.GetStatusAsync(chainNode.RemoteUrl, _tofuStore, nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            var serviceType = nodeStatus.Type.ToLowerInvariant();

            // ─── Step 2a: Clock drift detection ───
            var extremeDriftSub = serviceType == "v2ray"
                && nodeStatus.ClockDriftSec.HasValue
                && Math.Abs(nodeStatus.ClockDriftSec.Value) > 120;

            // ─── Step 3: Validate V2Ray path ───
            if (serviceType == "v2ray" && string.IsNullOrWhiteSpace(_options.V2RayExePath))
            {
                throw new SentinelException(
                    "V2RAY_PATH_REQUIRED",
                    "V2Ray node selected but V2RayExePath is not configured in SentinelVpnOptions"
                );
            }

            // ─── Step 4: Allocate session on existing subscription ───
            EmitProgress("subscribe", $"Allocating session on subscription {subscriptionId}...");

            var allocateMsg = MessageBuilder.SubStartSession(
                _wallet.Address,
                subscriptionId,
                nodeAddress
            );
            var txResult = await _txBuilder.BroadcastAsync(allocateMsg);
            ct.ThrowIfCancellationRequested();

            if (!txResult.Success)
            {
                throw new SentinelException(
                    "TX_FAILED",
                    $"Session allocation TX failed (code {txResult.Code}): {txResult.RawLog}"
                );
            }

            EmitProgress("subscribe", $"TX broadcast: {txResult.TxHash}");

            // ─── Step 5: Wait for chain propagation BEFORE querying session ───
            EmitProgress("propagation", "Waiting for chain propagation (5s)...");
            await Task.Delay(CHAIN_PROPAGATION_DELAY_MS, ct);

            var sessionId = await ExtractSessionId(txResult, ct);
            sessionIdForCleanup = sessionId; // Track for catch-block poisoning
            EmitProgress("subscribe", $"Session ID: {sessionId}");

            // ─── Step 6: V3 handshake ───
            EmitProgress("handshake", $"Performing V3 handshake with {chainNode.RemoteUrl}...");

            var handshakeType = serviceType == "wireguard"
                ? HandshakeType.WireGuard
                : HandshakeType.V2Ray;

            var handshakeResult = await Handshake.HandshakeAsync(
                _wallet,
                chainNode.RemoteUrl,
                sessionId,
                handshakeType,
                _tofuStore,
                nodeAddress,
                ct
            );
            ct.ThrowIfCancellationRequested();

            EmitProgress("handshake", "Handshake successful");

            // ─── Step 7: Install tunnel ───
            ConnectionResult result;

            if (handshakeResult is WireGuardHandshakeResult wgResult)
            {
                result = await InstallWireGuardTunnelAsync(wgResult, sessionId, nodeAddress, ct);
            }
            else if (handshakeResult is V2RayHandshakeResult v2Result)
            {
                result = await InstallV2RayTunnelAsync(
                    v2Result, sessionId, nodeAddress, chainNode.RemoteUrl,
                    extremeDriftSub, nodeStatus.ClockDriftSec, ct
                );
            }
            else
            {
                throw new SentinelException(
                    "UNKNOWN_SERVICE",
                    $"Unknown handshake result type: {handshakeResult.GetType().Name}"
                );
            }

            // ─── Step 8: Wait for WireGuard peer handshake, then verify ───
            await Task.Delay(5000, ct);
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(timeoutMs: 15000, ct: ct);
            result = result with { Verification = verification };

            if (verification.Working)
            {
                EmitProgress("verify", $"Tunnel verified, external IP: {verification.VpnIp}");
            }
            else
            {
                EmitProgress("verify", "Tunnel verification failed — IP check did not succeed");
            }

            // ─── Step 9: Finalize ───
            // Ported from js-sdk/node-connect.js line 1586:
            // markSessionActive(String(sessionId), opts.nodeAddress)
            StateManager.MarkSessionActive(sessionId.ToString(), nodeAddress);

            _activeConnection = result;
            _connectedAt = DateTime.UtcNow;

            EmitConnected(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SentinelException and not SentinelHandshakeException and not SentinelNodeException)
        {
            // Clear stale credentials on tunnel failure to prevent reuse of bad handshake data
            CredentialStore.Clear(nodeAddress);

            // Mark session as poisoned so it won't be reused
            // Ported from js-sdk/node-connect.js line 1596
            if (sessionIdForCleanup > 0)
            {
                StateManager.MarkSessionPoisoned(sessionIdForCleanup.ToString(), nodeAddress, ex.Message);
            }

            _logger.Error($"Connection failed via subscription {subscriptionId} to {nodeAddress}", ex);
            var wrapped = new SentinelException(
                "CONNECT_FAILED",
                $"Failed to connect via subscription {subscriptionId} to {nodeAddress}: {ex.Message}",
                ex
            );
            EmitError(wrapped);
            throw wrapped;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // ─── Disconnect ──────────────────────────────────────────────────

    /// <summary>
    /// Disconnect from the current node and clean up the tunnel.
    /// Stops V2Ray process or uninstalls WireGuard tunnel service.
    /// </summary>
    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await DisconnectInternalAsync("user");
    }

    /// <summary>
    /// Internal disconnect that skips the disposed check, used by Dispose/DisposeAsync.
    /// </summary>
    private async Task DisconnectInternalAsync(string reason)
    {
        if (_activeConnection is null)
        {
            return; // Nothing to disconnect
        }

        var sessionId = _activeConnection.SessionId;
        var nodeAddress = _activeConnection.NodeAddress;
        await CleanupTunnelsAsync();

        // End session on chain (best-effort, stored for DisposeAsync to await)
        if (ulong.TryParse(sessionId, out var sid) && sid > 0)
        {
            _pendingEndSession = Task.Run(async () =>
            {
                try
                {
                    var msg = MessageBuilder.EndSession(_wallet.Address, sid);
                    var tx = await _txBuilder.BroadcastAsync(msg);
                    _logger.Info($"Session {sid} ended on chain: TX {tx.TxHash} (code={tx.Code})");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to end session {sid} on chain: {ex.Message}");
                    // Non-fatal — session will expire naturally
                }
            });
        }

        _activeConnection = null;
        EmitDisconnected(reason);
    }

    // ─── Status ──────────────────────────────────────────────────────

    /// <summary>
    /// Get the current connection status, or null if not connected.
    /// </summary>
    /// <returns>Connection status with uptime, or null if disconnected.</returns>
    public ConnectionStatus? GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeConnection is null)
        {
            return null;
        }

        return new ConnectionStatus
        {
            Connected = true,
            NodeAddress = _activeConnection.NodeAddress,
            SessionId = _activeConnection.SessionId,
            ServiceType = _activeConnection.ServiceType,
            Uptime = DateTime.UtcNow - _connectedAt,
        };
    }

    // ─── Diagnostics ──────────────────────────────────────────────────

    /// <summary>
    /// Gather comprehensive diagnostic information about the current connection state.
    /// Checks WireGuard service status, peer handshake, traffic counters, system proxy,
    /// DNS resolution, and V2Ray SOCKS port. Never throws — all checks are best-effort.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConnectionDiagnostics"/> snapshot with all available information.</returns>
    public async Task<ConnectionDiagnostics> DiagnoseConnectionAsync(CancellationToken ct = default)
    {
        var serviceRunning = false;
        var interfaceExists = false;
        var peerHandshakeComplete = false;
        var bytesReceived = -1L;
        var bytesSent = -1L;
        var systemProxySet = false;
        var dnsWorking = false;
        string? tunnelName = null;
        int? socksPort = null;
        string? lastError = null;

        try
        {
            // ─── Determine tunnel type from active connection ───
            var isWireGuard = _wgTunnel is not null;
            var isV2Ray = _v2RayProcess is not null;

            // ─── WireGuard-specific checks ───
            if (isWireGuard)
            {
                tunnelName = _wgTunnel!.TunnelName;

                // Check WireGuard service status (Windows)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var serviceName = $"WireGuardTunnel${tunnelName}";
                        var psi = new ProcessStartInfo("sc.exe", $"query {serviceName}")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                        };
                        using var proc = Process.Start(psi);
                        if (proc is not null)
                        {
                            var output = await proc.StandardOutput.ReadToEndAsync(ct);
                            await proc.WaitForExitAsync(ct);
                            serviceRunning = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                            interfaceExists = serviceRunning || output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = $"sc.exe query failed: {ex.Message}";
                    }
                }
                else
                {
                    // Linux/macOS: check if interface exists via ip/ifconfig
                    interfaceExists = true; // Assume if _wgTunnel is not null
                }

                // Check wg show for peer handshake and transfer stats
                try
                {
                    var wgPsi = new ProcessStartInfo("wg", $"show {tunnelName}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var wgProc = Process.Start(wgPsi);
                    if (wgProc is not null)
                    {
                        var wgOutput = await wgProc.StandardOutput.ReadToEndAsync(ct);
                        await wgProc.WaitForExitAsync(ct);

                        if (wgProc.ExitCode == 0)
                        {
                            interfaceExists = true;

                            // Parse latest-handshake
                            foreach (var line in wgOutput.Split('\n'))
                            {
                                var trimmed = line.Trim();
                                if (trimmed.StartsWith("latest handshake:", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Any non-empty value means handshake completed
                                    peerHandshakeComplete = trimmed.Length > "latest handshake:".Length + 1;
                                }
                                else if (trimmed.StartsWith("transfer:", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Format: "transfer: X.XX KiB received, Y.YY KiB sent"
                                    ParseTransferLine(trimmed, out bytesReceived, out bytesSent);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // wg.exe may not be on PATH — non-fatal
                }
            }

            // ─── V2Ray-specific checks ───
            if (isV2Ray)
            {
                socksPort = _v2RayProcess!.SocksPort;

                // Check if SOCKS port is actually listening
                try
                {
                    using var tcpClient = new TcpClient();
                    await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort.Value, ct);
                    interfaceExists = true; // SOCKS port is reachable
                }
                catch
                {
                    interfaceExists = false;
                    lastError = $"V2Ray SOCKS port {socksPort} is not listening";
                }
            }

            // ─── System proxy check (Windows) ───
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
                    if (key is not null)
                    {
                        var proxyEnable = key.GetValue("ProxyEnable");
                        var proxyServer = key.GetValue("ProxyServer") as string;
                        key.Close();

                        systemProxySet = proxyEnable is int enabled && enabled == 1
                            && proxyServer is not null && proxyServer.Contains("socks", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    // Registry read failed — non-fatal
                }
            }
            else
            {
                // On non-Windows, trust our internal tracking
                systemProxySet = _systemProxySet;
            }

            // ─── DNS resolution check ───
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(5000);
                var addresses = await Dns.GetHostAddressesAsync("sentinel.co", cts.Token);
                dnsWorking = addresses.Length > 0;
            }
            catch
            {
                dnsWorking = false;
            }
        }
        catch (Exception ex)
        {
            lastError = $"Diagnostics failed: {ex.Message}";
        }

        return new ConnectionDiagnostics(
            ServiceRunning: serviceRunning,
            InterfaceExists: interfaceExists,
            PeerHandshakeComplete: peerHandshakeComplete,
            BytesReceived: bytesReceived,
            BytesSent: bytesSent,
            SystemProxySet: systemProxySet,
            DnsWorking: dnsWorking,
            TunnelName: tunnelName,
            SocksPort: socksPort,
            LastError: lastError
        );
    }

    /// <summary>
    /// Parse a WireGuard transfer line like "transfer: 1.23 KiB received, 4.56 MiB sent"
    /// into byte counts. Sets -1 if parsing fails.
    /// </summary>
    private static void ParseTransferLine(string line, out long received, out long sent)
    {
        received = -1;
        sent = -1;

        try
        {
            // Remove "transfer:" prefix
            var data = line.Substring(line.IndexOf(':') + 1).Trim();
            var parts = data.Split(',');

            if (parts.Length >= 2)
            {
                received = ParseByteValue(parts[0].Trim());
                sent = ParseByteValue(parts[1].Trim());
            }
        }
        catch
        {
            // Non-fatal — leave as -1
        }
    }

    /// <summary>
    /// Parse a value like "1.23 KiB received" or "4.56 MiB sent" to bytes.
    /// </summary>
    private static long ParseByteValue(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return -1;

        if (!double.TryParse(tokens[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var num))
        {
            return -1;
        }

        var unit = tokens[1].ToUpperInvariant();
        var multiplier = unit switch
        {
            "B" => 1L,
            "KIB" => 1024L,
            "MIB" => 1024L * 1024,
            "GIB" => 1024L * 1024 * 1024,
            "TIB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };

        return (long)(num * multiplier);
    }

    // ─── Post-Connection Verification ─────────────────────────────────

    /// <summary>
    /// Verify the VPN tunnel is working by checking the public IP via an external service.
    /// If the IP changed from the local IP, the tunnel is routing traffic correctly.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds for the IP check (default: 8000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ConnectionVerification"/> indicating whether the tunnel is working
    /// and the public IP seen through the tunnel.
    /// </returns>
    public async Task<ConnectionVerification> VerifyConnectionAsync(
        int timeoutMs = 8000,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            HttpClient httpClient;
            HttpClientHandler? proxyHandler = null;

            // For V2Ray connections, route the verification request through the SOCKS5 proxy
            // so we check the VPN IP, not the real IP.
            if (_v2RayProcess is not null && _v2RayProcess.SocksPort > 0)
            {
                var proxy = new WebProxy($"socks5://127.0.0.1:{_v2RayProcess.SocksPort}");
                // SOCKS5 proxy requires authentication — include credentials
                if (_v2RayProcess.SocksUser is not null && _v2RayProcess.SocksPass is not null)
                {
                    proxy.Credentials = new NetworkCredential(_v2RayProcess.SocksUser, _v2RayProcess.SocksPass);
                }
                proxyHandler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true,
                };
                httpClient = new HttpClient(proxyHandler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            }
            else
            {
                httpClient = SharedHttpClient;
            }

            try
            {
                var response = await httpClient.GetStringAsync(
                    "https://api.ipify.org?format=json", cts.Token);

                using var doc = JsonDocument.Parse(response);
                var ip = doc.RootElement.TryGetProperty("ip", out var ipProp)
                    ? ipProp.GetString()
                    : null;

                return new ConnectionVerification(true, ip);
            }
            finally
            {
                // Dispose the per-request client/handler if we created one for V2Ray
                if (proxyHandler is not null)
                {
                    httpClient.Dispose();
                    proxyHandler.Dispose();
                }
            }
        }
        catch
        {
            return new ConnectionVerification(false, null);
        }
    }

    // ─── Tunnel Installation ─────────────────────────────────────────

    /// <summary>
    /// Install a WireGuard tunnel from the handshake result.
    /// </summary>
    private async Task<ConnectionResult> InstallWireGuardTunnelAsync(
        WireGuardHandshakeResult wgResult,
        ulong sessionId,
        string nodeAddress,
        CancellationToken ct)
    {
        // Wait for node to register our peer before installing tunnel
        // The node needs 1-5s to add the peer to its WireGuard interface after handshake
        EmitProgress("tunnel", "Waiting for node to register peer...");
        await Task.Delay(5000, ct);

        EmitProgress("tunnel", "Installing WireGuard tunnel...");

        var config = new WireGuardConfig(
            ClientPrivateKey: wgResult.ClientPrivateKey,
            AssignedAddresses: wgResult.AssignedAddresses,
            ServerPublicKey: wgResult.ServerPublicKey,
            ServerEndpoint: wgResult.ServerEndpoint,
            FullTunnel: _options.FullTunnel
        ) { Dns = Constants.DnsPresets.Resolve(_options.Dns) };

        _wgTunnel = new WireGuardTunnel();
        await _wgTunnel.InstallAsync(config, ct);
        ct.ThrowIfCancellationRequested();

        // Extract the first assigned IPv4 address (strip CIDR)
        string? vpnIp = null;
        foreach (var addr in wgResult.AssignedAddresses)
        {
            if (addr.Contains('.'))
            {
                vpnIp = addr.Split('/')[0];
                break;
            }
        }

        EmitProgress("tunnel", $"WireGuard tunnel active, VPN IP: {vpnIp}");

        return new ConnectionResult
        {
            SessionId = sessionId.ToString(),
            NodeAddress = nodeAddress,
            ServiceType = "wireguard",
            VpnIp = vpnIp,
        };
    }

    /// <summary>
    /// Install a V2Ray tunnel from the handshake result.
    /// Matches JS SDK behavior: builds one V2Ray config per transport entry and tests
    /// each outbound sequentially until one connects. This is critical because V2Ray
    /// does not auto-fallback between outbounds via routing rules alone — the application
    /// layer must restart V2Ray with each individual outbound config.
    /// </summary>
    /// <remarks>
    /// When <paramref name="extremeDrift"/> is true:
    /// <list type="bullet">
    ///   <item>If the node has VLess transports (proxy_protocol=1), strips all VMess
    ///         outbounds and reorders VLess first. VLess is immune to clock drift.</item>
    ///   <item>If VMess-only (no VLess available), throws <c>CLOCK_DRIFT_TOO_HIGH</c>
    ///         because VMess AEAD rejects packets with &gt;120s drift.</item>
    /// </list>
    /// </remarks>
    private async Task<ConnectionResult> InstallV2RayTunnelAsync(
        V2RayHandshakeResult v2Result,
        ulong sessionId,
        string nodeAddress,
        string nodeUrl,
        bool extremeDrift,
        double? clockDriftSec,
        CancellationToken ct)
    {
        EmitProgress("tunnel", "Starting V2Ray process...");

        // Extract host from the node URL
        var uri = new Uri(nodeUrl);
        var serverHost = uri.Host;

        // ─── Build V2RayConfig list from all transport entries ───
        // Use incrementing SOCKS ports to avoid TIME_WAIT collisions on Windows.
        // After killing V2Ray, the previous port stays in TIME_WAIT (~120s on Windows).
        // Each outbound attempt gets its own port so fallback works immediately.
        var baseSocksPort = DEFAULT_SOCKS_PORT + Random.Shared.Next(0, 100);
        var configs = new List<V2RayConfig>(v2Result.AllEntries.Count);
        for (var idx = 0; idx < v2Result.AllEntries.Count; idx++)
        {
            var entry = v2Result.AllEntries[idx];
            var entryProtocol = entry.ProxyProtocol == 1 ? "vless" : "vmess";
            var entryTransport = MapTransportNumber(entry.Transport);
            configs.Add(new V2RayConfig(
                ServerHost: serverHost,
                Port: entry.Port,
                Protocol: entryProtocol,
                Transport: entryTransport,
                Tls: entry.Tls == 1,
                Uuid: v2Result.Uuid,
                LocalSocksPort: baseSocksPort + idx
            ));
        }

        // ─── Clock drift handling: strip VMess when drift >120s ───
        // VLess (proxy_protocol=1) is immune to clock drift; VMess (2) uses AEAD timestamps.
        if (extremeDrift)
        {
            var hasVless = configs.Any(c => c.Protocol == "vless");
            if (!hasVless)
            {
                throw new SentinelNodeException(
                    ErrorCodes.NodeClockDrift,
                    $"VMess-only node with clock drift {clockDriftSec:F0}s " +
                    "(AEAD tolerance +/-120s, no VLess available). " +
                    "Choose a different node."
                );
            }

            // Strip VMess outbounds and reorder VLess first
            var vlessCount = configs.Count(c => c.Protocol == "vless");
            configs = configs.Where(c => c.Protocol == "vless").ToList();
            _logger.Info($"Clock drift {clockDriftSec:F0}s: stripped VMess outbounds, {vlessCount} VLess remaining");
        }

        _logger.Info($"Testing {configs.Count} V2Ray transport(s) sequentially");

        // ─── Sequential transport fallback (matches JS SDK) ───
        // Try each outbound individually: write single-outbound config, start V2Ray,
        // test SOCKS5 connectivity. If it fails, kill V2Ray and try the next outbound.
        // NEVER use balancer/observatory — causes session poisoning.
        V2RayProcess? workingProcess = null;

        for (var i = 0; i < configs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var config = configs[i];
            var transportLabel = $"{config.Protocol}/{config.Transport}/{(config.Tls ? "tls" : "none")}";
            EmitProgress("tunnel", $"  Trying outbound {i + 1}/{configs.Count}: {transportLabel} (port {config.Port})");

            // Kill previous V2Ray process if any
            if (_v2RayProcess is not null && _v2RayProcess.IsRunning)
            {
                await _v2RayProcess.StopAsync(ct);
                _v2RayProcess.Dispose();
                _v2RayProcess = null;
                // Brief pause for port release
                await Task.Delay(1000, ct);
            }

            _v2RayProcess = new V2RayProcess(_options.V2RayExePath!);

            try
            {
                await _v2RayProcess.StartAsync(config, ct, _options.Dns, _options.SystemProxy);
            }
            catch (SentinelException ex)
            {
                _logger.Warn($"  {transportLabel}: V2Ray start failed — {ex.Message}");
                EmitProgress("tunnel", $"  {transportLabel}: failed ({ex.Code})");
                continue;
            }

            // V2Ray started and SOCKS5 port is accepting connections.
            // Test actual connectivity through the proxy.
            var connected = await TestSocksConnectivityAsync(
                _v2RayProcess.SocksPort,
                _v2RayProcess.SocksUser,
                _v2RayProcess.SocksPass,
                ct
            );

            if (connected)
            {
                _logger.Info($"  {transportLabel}: connected!");
                EmitProgress("tunnel", $"  {transportLabel}: connected!");
                workingProcess = _v2RayProcess;
                break;
            }

            _logger.Warn($"  {transportLabel}: SOCKS5 port open but no connectivity");
            EmitProgress("tunnel", $"  {transportLabel}: failed (no connectivity)");
        }

        if (workingProcess is null)
        {
            // All outbounds failed — clean up
            if (_v2RayProcess is not null)
            {
                await _v2RayProcess.StopAsync(ct);
                _v2RayProcess.Dispose();
                _v2RayProcess = null;
            }

            throw new TunnelException(
                "V2RAY_ALL_FAILED",
                $"All {configs.Count} V2Ray transport/protocol combinations failed on {nodeAddress}"
            );
        }

        ct.ThrowIfCancellationRequested();

        // Set system SOCKS5 proxy so all traffic routes through V2Ray
        if (_options.SystemProxy)
        {
            SystemProxy.Set(_v2RayProcess!.SocksPort);
            _systemProxySet = true;
            EmitProgress("tunnel", $"System proxy set to SOCKS5 127.0.0.1:{_v2RayProcess.SocksPort}");
        }

        EmitProgress("tunnel", $"V2Ray active on SOCKS5 port {_v2RayProcess!.SocksPort}");

        return new ConnectionResult
        {
            SessionId = sessionId.ToString(),
            NodeAddress = nodeAddress,
            ServiceType = "v2ray",
            SocksPort = _v2RayProcess.SocksPort,
            SocksUser = _v2RayProcess.SocksUser,
            SocksPass = _v2RayProcess.SocksPass,
        };
    }

    /// <summary>
    /// Test connectivity through a local SOCKS5 proxy by making an HTTP request
    /// to a reliable target. Matches JS SDK's per-outbound connectivity test.
    /// </summary>
    private static async Task<bool> TestSocksConnectivityAsync(
        int socksPort, string? socksUser, string? socksPass, CancellationToken ct)
    {
        var targets = new[] { "https://www.google.com", "https://www.cloudflare.com" };

        foreach (var target in targets)
        {
            try
            {
                var proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}");
                if (!string.IsNullOrEmpty(socksUser) && !string.IsNullOrEmpty(socksPass))
                {
                    proxy.Credentials = new NetworkCredential(socksUser, socksPass);
                }

                using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var response = await client.GetAsync(target, ct);
                // Any HTTP response (even 403, 301) means the tunnel is working
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try next target
            }
        }

        return false;
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Create a new on-chain session with the given node.
    /// Supports both pay-per-GB and pay-per-hour pricing.
    /// When <see cref="SentinelVpnOptions.PreferHourly"/> is true and the node's
    /// hourly price is cheaper than the gigabyte price, uses hourly session.
    /// Broadcasts the session TX and extracts the resulting session ID.
    /// </summary>
    private async Task<ulong> CreateNewSessionAsync(
        ChainNode chainNode, string nodeAddress, CancellationToken ct)
    {
        EmitProgress("subscribe", "Creating on-chain session...");

        // Determine max price from node's gigabyte prices
        PriceEntry? gbPrice = null;
        foreach (var price in chainNode.GigabytePrices)
        {
            if (price.Denom == Constants.Denom)
            {
                gbPrice = price;
                break;
            }
        }

        // Determine hourly price if available
        PriceEntry? hrPrice = null;
        foreach (var price in chainNode.HourlyPrices)
        {
            if (price.Denom == Constants.Denom)
            {
                hrPrice = price;
                break;
            }
        }

        // Determine pricing model: explicit Hours > PreferHourly > default GB
        long gigabytes = _options.Gigabytes;
        long hours = 0;
        PriceEntry? maxPrice = gbPrice;

        if (_options.Hours > 0)
        {
            // Explicit hours requested — use hourly pricing
            if (hrPrice == null)
                throw new SentinelNodeException($"Node {nodeAddress} has no hourly pricing — cannot use hours-based session");
            gigabytes = 0;
            hours = _options.Hours;
            maxPrice = hrPrice;
        }
        else if (_options.PreferHourly && hrPrice != null)
        {
            // PreferHourly = use hourly pricing if the node offers it.
            // No cross-unit comparison (GB vs hour prices are different units).
            gigabytes = 0;
            hours = 1;
            maxPrice = hrPrice;
        }

        var pricingMode = hours > 0 ? "hourly" : "per-GB";
        EmitProgress("subscribe", $"Broadcasting session TX ({pricingMode})...");

        var sessionMsg = MessageBuilder.StartSession(
            _wallet.Address,
            nodeAddress,
            gigabytes,
            maxPrice,
            hours
        );
        var txResult = await _txBuilder.BroadcastAsync(sessionMsg);
        ct.ThrowIfCancellationRequested();

        // ─── Code 105: NODE_INACTIVE retry ───
        // Ported from js-sdk broadcastWithInactiveRetry():
        // LCD may show node as active but the chain disagrees (propagation lag).
        // Wait 15s for LCD to sync, then retry once.
        if (!txResult.Success && txResult.Code == 105)
        {
            _logger.Warn("Node inactive on chain (code 105) — waiting 15s for LCD sync...");
            EmitProgress("subscribe", "Node inactive on chain — retrying in 15s...");
            await Task.Delay(15_000, ct);
            txResult = await _txBuilder.BroadcastAsync(sessionMsg);
            ct.ThrowIfCancellationRequested();

            if (!txResult.Success && txResult.Code == 105)
            {
                throw new SentinelException(
                    ErrorCodes.NodeInactive,
                    $"Node {nodeAddress} is inactive on chain after retry (code 105): {txResult.RawLog}"
                );
            }
        }

        if (!txResult.Success)
        {
            throw new SentinelException(
                ErrorCodes.TxFailed,
                $"Session TX failed (code {txResult.Code}): {txResult.RawLog}"
            );
        }

        EmitProgress("subscribe", $"TX broadcast: {txResult.TxHash}");

        // Wait for chain propagation before querying session
        EmitProgress("propagation", "Waiting for chain propagation (5s)...");
        await Task.Delay(CHAIN_PROPAGATION_DELAY_MS, ct);

        var sessionId = await ExtractSessionId(txResult, ct);
        EmitProgress("subscribe", $"Session ID: {sessionId}");

        return sessionId;
    }

    /// <summary>
    /// Extract the session ID from a broadcast TX result.
    /// Queries the chain for the wallet's active sessions and returns the most recent one.
    /// </summary>
    /// <param name="txResult">The broadcast result (used for error context).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The on-chain session ID.</returns>
    private async Task<ulong> ExtractSessionId(TxResult txResult, CancellationToken ct = default)
    {
        // Query active sessions for this wallet and take the latest
        var sessions = await _chainClient.QueryActiveSessionsForAddressAsync(_wallet.Address, ct);

        if (sessions.Count == 0)
        {
            throw new SentinelException(
                "SESSION_NOT_FOUND",
                $"No active session found after TX {txResult.TxHash}. The TX may still be processing."
            );
        }

        // Return the highest (most recent) session ID
        ulong maxId = 0;
        foreach (var session in sessions)
        {
            if (session.Id > maxId)
            {
                maxId = session.Id;
            }
        }

        return maxId;
    }

    /// <summary>
    /// Map a Sentinel transport number to the V2Ray transport name.
    /// 1=ds, 2=gun, 3=grpc, 4=http, 5=mkcp, 6=quic, 7=tcp, 8=websocket.
    /// CRITICAL: gun (2) and grpc (3) are DIFFERENT protocols.
    /// </summary>
    /// <param name="transport">Numeric transport identifier from handshake metadata.</param>
    /// <returns>Transport name string for V2Ray config.</returns>
    private static string MapTransportNumber(int transport)
    {
        return transport switch
        {
            1 => "ds",
            2 => "gun",
            3 => "grpc",
            4 => "http",
            5 => "mkcp",
            6 => "quic",
            7 => "tcp",
            8 => "websocket",
            _ => "tcp",
        };
    }

    /// <summary>
    /// Fast reconnect using saved credentials — skips payment AND handshake entirely.
    /// Builds the tunnel configuration from saved keys/metadata and installs the tunnel directly.
    /// </summary>
    /// <param name="saved">Previously saved handshake credentials.</param>
    /// <param name="nodeAddress">Node address being reconnected to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details of the fast-reconnected session.</returns>
    private async Task<ConnectionResult> FastReconnectAsync(
        SavedCredentials saved, string nodeAddress, CancellationToken ct)
    {
        ConnectionResult result;

        if (saved.ServiceType == "wireguard")
        {
            EmitProgress("tunnel", "Fast reconnect: installing WireGuard tunnel from saved credentials...");

            var config = new WireGuardConfig(
                ClientPrivateKey: Convert.FromBase64String(saved.WgPrivateKey!),
                AssignedAddresses: saved.WgAssignedAddrs!,
                ServerPublicKey: saved.WgServerPubKey!,
                ServerEndpoint: saved.WgServerEndpoint!,
                FullTunnel: _options.FullTunnel
            ) { Dns = Constants.DnsPresets.Resolve(_options.Dns) };

            _wgTunnel = new WireGuardTunnel();
            await _wgTunnel.InstallAsync(config, ct);
            ct.ThrowIfCancellationRequested();

            // Extract the first assigned IPv4 address (strip CIDR)
            string? vpnIp = null;
            foreach (var addr in saved.WgAssignedAddrs!)
            {
                if (addr.Contains('.'))
                {
                    vpnIp = addr.Split('/')[0];
                    break;
                }
            }

            EmitProgress("tunnel", $"WireGuard tunnel active (fast reconnect), VPN IP: {vpnIp}");

            // Verify tunnel
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(ct: ct);

            result = new ConnectionResult
            {
                SessionId = saved.SessionId,
                NodeAddress = nodeAddress,
                ServiceType = "wireguard",
                VpnIp = verification.Working ? verification.VpnIp : vpnIp,
                Verification = verification,
            };
        }
        else
        {
            // V2Ray fast reconnect
            EmitProgress("tunnel", "Fast reconnect: starting V2Ray from saved credentials...");

            if (string.IsNullOrWhiteSpace(_options.V2RayExePath))
            {
                throw new SentinelException(
                    "V2RAY_PATH_REQUIRED",
                    "V2Ray node selected but V2RayExePath is not configured in SentinelVpnOptions"
                );
            }

            var protocol = saved.V2RayProtocol == 1 ? "vless" : "vmess";
            var transport = MapTransportNumber(saved.V2RayTransport ?? 7);

            var v2Config = new V2RayConfig(
                ServerHost: saved.V2RayServerHost!,
                Port: saved.V2RayPort ?? 443,
                Protocol: protocol,
                Transport: transport,
                Tls: saved.V2RayTls == 1,
                Uuid: saved.V2RayUuid!,
                LocalSocksPort: DEFAULT_SOCKS_PORT
            );

            _v2RayProcess = new V2RayProcess(_options.V2RayExePath!);
            await _v2RayProcess.StartAsync(v2Config, ct, _options.Dns, _options.SystemProxy);
            ct.ThrowIfCancellationRequested();

            EmitProgress("tunnel", $"V2Ray active (fast reconnect) on SOCKS5 port {_v2RayProcess.SocksPort}");

            // Verify tunnel
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(ct: ct);

            result = new ConnectionResult
            {
                SessionId = saved.SessionId,
                NodeAddress = nodeAddress,
                ServiceType = "v2ray",
                SocksPort = _v2RayProcess.SocksPort,
                SocksUser = _v2RayProcess.SocksUser,
                SocksPass = _v2RayProcess.SocksPass,
                Verification = verification,
            };
        }

        if (result.Verification?.Working == true)
        {
            EmitProgress("verify", $"Tunnel verified (fast reconnect), external IP: {result.Verification.VpnIp}");
        }
        else
        {
            EmitProgress("verify", "Tunnel verification failed — IP check did not succeed");
        }

        _activeConnection = result;
        _connectedAt = DateTime.UtcNow;

        EmitConnected(result);
        return result;
    }

    /// <summary>
    /// Clean up any active tunnels (WireGuard service or V2Ray process).
    /// </summary>
    private async Task CleanupTunnelsAsync()
    {
        if (_wgTunnel is not null)
        {
            try
            {
                await _wgTunnel.UninstallAsync();
            }
            catch (Exception ex)
            {
                EmitError(new SentinelException("CLEANUP_WG", $"WireGuard cleanup failed: {ex.Message}", ex));
            }
            finally
            {
                _wgTunnel.Dispose();
                _wgTunnel = null;
            }
        }

        if (_v2RayProcess is not null)
        {
            try
            {
                await _v2RayProcess.StopAsync();
            }
            catch (Exception ex)
            {
                EmitError(new SentinelException("CLEANUP_V2RAY", $"V2Ray cleanup failed: {ex.Message}", ex));
            }
            finally
            {
                _v2RayProcess.Dispose();
                _v2RayProcess = null;
            }
        }

        // Clear system proxy if we set it
        if (_systemProxySet)
        {
            try { SystemProxy.Clear(); }
            catch { /* best effort */ }
            _systemProxySet = false;
        }
    }

    // ─── Event Emitters ──────────────────────────────────────────────

    /// <summary>Emit a progress event.</summary>
    private void EmitProgress(string step, string detail)
    {
        Progress?.Invoke(this, new ProgressEventArgs { Step = step, Detail = detail });
    }

    /// <summary>Emit a connected event.</summary>
    private void EmitConnected(ConnectionResult result)
    {
        Connected?.Invoke(this, new ConnectionEventArgs { Result = result });
    }

    /// <summary>Emit a disconnected event.</summary>
    private void EmitDisconnected(string reason)
    {
        Disconnected?.Invoke(this, new DisconnectedEventArgs { Reason = reason });
    }

    /// <summary>Emit an error event.</summary>
    private void EmitError(Exception ex)
    {
        Error?.Invoke(this, new ErrorEventArgs { Exception = ex });
    }

    // ─── IDisposable / IAsyncDisposable ─────────────────────────────

    /// <summary>
    /// Asynchronously dispose the VPN client, awaiting the pending EndSession TX
    /// before releasing all resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (_activeConnection is not null)
        {
            try
            {
                await DisconnectInternalAsync("dispose");
            }
            catch
            {
                // Suppress — disposal must not throw.
                _wgTunnel?.Dispose();
                _wgTunnel = null;
                _v2RayProcess?.Dispose();
                _v2RayProcess = null;
                _activeConnection = null;
                EmitDisconnected("dispose");
            }
        }

        // Await pending EndSession TX before disposing chain client
        if (_pendingEndSession is not null)
        {
            try { await _pendingEndSession; }
            catch { /* EndSession is best-effort */ }
        }

        _disposed = true;

        _networkMonitor.NetworkChanged -= OnNetworkChanged;
        _networkMonitor.Dispose();
        _chainClient.Dispose();
        _connectLock.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose the VPN client, disconnecting if still connected and releasing all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (_activeConnection is not null)
        {
            // Wrap in Task.Run to avoid sync-over-async deadlock when
            // Dispose is called from a UI thread with a SynchronizationContext.
            try
            {
                Task.Run(() => DisconnectInternalAsync("dispose")).GetAwaiter().GetResult();
            }
            catch
            {
                // Suppress — disposal must not throw.
                // Attempt direct cleanup as fallback.
                _wgTunnel?.Dispose();
                _wgTunnel = null;
                _v2RayProcess?.Dispose();
                _v2RayProcess = null;
                _activeConnection = null;
                EmitDisconnected("dispose");
            }
        }

        // Give pending EndSession TX a short window before killing chain client
        _pendingEndSession?.Wait(5000);

        _disposed = true;

        _networkMonitor.NetworkChanged -= OnNetworkChanged;
        _networkMonitor.Dispose();
        _chainClient.Dispose();
        _connectLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
