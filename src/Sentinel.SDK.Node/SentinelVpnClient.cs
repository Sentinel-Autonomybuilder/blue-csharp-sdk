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
            // Wrapper disposal = app shutdown: settle the session on-chain.
            await client.DisconnectAndEndSessionAsync();
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

// ─── VPN Client (Core) ──────────────────────────────────────────────────────

/// <summary>
/// High-level connection orchestrator for Sentinel dVPN.
/// Manages the full flow: wallet setup, chain queries, session creation,
/// V3 handshake, and tunnel installation (WireGuard or V2Ray).
/// </summary>
/// <remarks>
/// <para>
/// Equivalent to the JS SDK's <c>connectDirect()</c>, <c>connectAuto()</c>,
/// <c>quickConnect()</c>, and <c>disconnect()</c>.
/// </para>
/// <para>
/// This class is split into partial class files for maintainability:
/// <list type="bullet">
///   <item><c>SentinelVpnClient.cs</c> — Core declaration, constructor, fields, events, IDisposable</item>
///   <item><c>SentinelVpnClient.Connect.cs</c> — ConnectAsync, ConnectAutoAsync, ConnectViaSubscriptionAsync</item>
///   <item><c>SentinelVpnClient.Disconnect.cs</c> — DisconnectAsync, cleanup, session end TX</item>
///   <item><c>SentinelVpnClient.Tunnel.cs</c> — WireGuard/V2Ray tunnel setup, handshake orchestration</item>
///   <item><c>SentinelVpnClient.Status.cs</c> — GetStatus, VerifyConnection, DiagnoseConnection, QuickConnect</item>
///   <item><c>SentinelVpnClient.Session.cs</c> — Session creation, ID extraction, transport mapping</item>
/// </list>
/// </para>
/// </remarks>
public partial class SentinelVpnClient : IDisposable, IAsyncDisposable
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

    // ─── Properties ─────────────────────────────────────────────────

    /// <summary>
    /// Whether the client currently has an active VPN connection.
    /// </summary>
    public bool IsConnected => _activeConnection is not null;

    // ─── Network Change Handler ─────────────────────────────────────

    private async void OnNetworkChanged(object? sender, NetworkChangedEventArgs e)
    {
        if (_activeConnection is null) return;

        _logger.Info($"Network changed ({e.Reason}) — tearing down stale tunnel (session preserved for reuse).");

        try
        {
            // Soft disconnect: user flipped networks, they didn't quit.
            // Preserve the on-chain session so ConnectAsync can reuse it.
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Auto-disconnect on network change failed: {ex.Message}");
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
                // App shutdown: settle the session on-chain so the deposit refunds.
                await DisconnectInternalAsync("dispose", endSession: true);
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
                Task.Run(() => DisconnectInternalAsync("dispose", endSession: true)).GetAwaiter().GetResult();
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
