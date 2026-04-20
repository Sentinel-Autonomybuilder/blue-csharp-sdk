using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

/// <summary>
/// Consumer entry point for Sentinel dVPN apps — VPN clients, white-label apps.
///
/// <para>
/// This facade exposes the connection, wallet, security, and settings surface
/// meant for end-user VPN applications. One session, one node, one user.
/// </para>
///
/// <para>
/// Mirrors the JavaScript SDK v34 <c>consumer.js</c> entry point. When building
/// a consumer VPN app, use this class instead of <see cref="Operator"/> —
/// operator-side functions (batch sessions, plan/provider/lease management,
/// network audit) can drain a wallet in bulk and are NOT for end users.
/// </para>
///
/// <para>
/// Usage:
/// <code>
/// using Sentinel.SDK.Node;
///
/// using var wallet = SentinelWallet.FromMnemonic(mnemonic);
/// using var vpn = Consumer.CreateClient(wallet, new SentinelVpnOptions { FullTunnel = true });
/// var conn = await vpn.ConnectAutoAsync(new ConnectAutoOptions { Countries = new[] { "DE" } });
/// </code>
/// </para>
/// </summary>
public static class Consumer
{
    // ─── Client factory ─────────────────────────────────────────────────────

    /// <summary>
    /// Create a new <see cref="SentinelVpnClient"/> for consumer VPN use.
    /// The returned client owns the tunnel lifecycle (Connect, Disconnect, Status).
    /// </summary>
    public static SentinelVpnClient CreateClient(SentinelWallet wallet, SentinelVpnOptions? options = null)
        => new(wallet, options ?? new SentinelVpnOptions());

    // ─── Node discovery ─────────────────────────────────────────────────────

    /// <summary>Query all currently-active nodes from the Sentinel chain.</summary>
    public static Task<List<ChainNode>> ListNodesAsync(IChainClient chain, int limit = 500, CancellationToken ct = default)
        => chain.GetActiveNodesAsync(limit, ct);

    /// <summary>Fetch a single node by its <c>sentnode1...</c> address.</summary>
    public static Task<ChainNode?> GetNodeAsync(IChainClient chain, string nodeAddress, CancellationToken ct = default)
        => chain.GetNodeAsync(nodeAddress, ct);

    /// <summary>Return a high-level network overview (totals, country breakdown, avg price).</summary>
    public static Task<NetworkOverview> GetNetworkOverviewAsync(IChainClient chain, CancellationToken ct = default)
        => chain.GetNetworkOverviewAsync(ct);

    // ─── Wallet ─────────────────────────────────────────────────────────────

    /// <summary>Generate a new random 24-word BIP39 wallet.</summary>
    public static SentinelWallet CreateWallet() => SentinelWallet.Generate();

    /// <summary>Import a wallet from an existing BIP39 mnemonic.</summary>
    public static SentinelWallet ImportWallet(string mnemonic) => SentinelWallet.FromMnemonic(mnemonic);

    /// <summary>Query a wallet's P2P balance.</summary>
    public static Task<Balance> GetBalanceAsync(IChainClient chain, string address, CancellationToken ct = default)
        => chain.GetBalanceAsync(address, ct);

    /// <summary>Validate a BIP39 mnemonic without creating a wallet.</summary>
    public static bool IsMnemonicValid(string mnemonic) => SentinelWallet.IsMnemonicValid(mnemonic);

    // ─── Session query ──────────────────────────────────────────────────────

    /// <summary>Find any existing active session between this wallet and a specific node.</summary>
    public static Task<ChainSession?> FindExistingSessionAsync(IChainClient chain, string walletAddress, string nodeAddress, CancellationToken ct = default)
        => chain.FindExistingSessionAsync(walletAddress, nodeAddress, ct);

    /// <summary>Get all of the wallet's active sessions.</summary>
    public static Task<IReadOnlyList<ActiveSession>> GetActiveSessionsAsync(IChainClient chain, string walletAddress, CancellationToken ct = default)
        => chain.QueryActiveSessionsForAddressAsync(walletAddress, ct);

    /// <summary>Get bandwidth allocation (used/max) for a specific session.</summary>
    public static Task<RawSessionAllocation?> GetSessionAllocationAsync(IChainClient chain, ulong sessionId, CancellationToken ct = default)
        => chain.QuerySessionAllocationAsync(sessionId, ct);

    // ─── Subscription query ────────────────────────────────────────────────

    /// <summary>Get all subscriptions owned by an account.</summary>
    public static Task<List<Subscription>> GetSubscriptionsAsync(IChainClient chain, string address, CancellationToken ct = default)
        => chain.GetSubscriptionsAsync(address, ct);

    /// <summary>Get nodes available through the wallet's active subscriptions.</summary>
    public static Task<IReadOnlyList<ChainNode>> GetAvailableNodesAsync(IChainClient chain, string walletAddress, CancellationToken ct = default)
        => chain.GetAvailableNodesAsync(walletAddress, ct);

    // ─── Pricing ────────────────────────────────────────────────────────────

    /// <summary>Get standardized prices for a node (gigabyte + hourly, formatted P2P).</summary>
    public static Task<NodePrices> GetNodePricesAsync(IChainClient chain, string nodeAddress, CancellationToken ct = default)
        => chain.GetNodePricesAsync(nodeAddress, ct);

    // ─── Settings ───────────────────────────────────────────────────────────

    /// <summary>Load persisted VPN settings (polling intervals, DNS preset, UI prefs).</summary>
    public static VpnSettings LoadSettings(string? path = null) => VpnSettings.Load(path);

    /// <summary>Save VPN settings to disk.</summary>
    public static void SaveSettings(VpnSettings settings, string? path = null) => settings.Save(path);

    /// <summary>Reset settings to defaults.</summary>
    public static VpnSettings ResetSettings() => new();
}
