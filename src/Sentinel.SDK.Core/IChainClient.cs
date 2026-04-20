namespace Sentinel.SDK.Core;

// ─── Session Query Types ───

/// <summary>
/// Status of an on-chain session.
/// </summary>
public enum SessionStatus
{
    /// <summary>Session is currently active.</summary>
    Active,

    /// <summary>Session is inactive or ended.</summary>
    Inactive,
}

/// <summary>
/// An active session as returned by chain queries.
/// </summary>
/// <param name="Id">On-chain session ID.</param>
/// <param name="NodeAddress">Node address hosting the session (sentnode1...).</param>
/// <param name="Status">Current session status.</param>
public record ActiveSession(ulong Id, string NodeAddress, SessionStatus Status);

/// <summary>
/// Raw allocation data from an on-chain session query.
/// </summary>
/// <param name="MaxBytes">Total bytes allocated.</param>
/// <param name="UsedBytes">Bytes consumed so far.</param>
public record RawSessionAllocation(long MaxBytes, long UsedBytes);

// ─── Chain Client Interface ───

/// <summary>
/// Interface for querying the Sentinel blockchain (LCD/RPC).
/// </summary>
public interface IChainClient
{
    /// <summary>
    /// Get the udvpn balance for an address.
    /// </summary>
    /// <param name="address">Bech32 account address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Balance with micro-denomination, decimal, and display values.</returns>
    Task<Balance> GetBalanceAsync(string address, CancellationToken ct = default);

    /// <summary>
    /// Get active nodes registered on the chain.
    /// </summary>
    /// <param name="limit">Maximum number of nodes to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of active chain nodes.</returns>
    Task<List<ChainNode>> GetActiveNodesAsync(int limit = 500, CancellationToken ct = default);

    /// <summary>
    /// Get a single node by its sentnode address.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The node, or null if not found.</returns>
    Task<ChainNode?> GetNodeAsync(string nodeAddress, CancellationToken ct = default);

    /// <summary>
    /// Get subscriptions for an account address.
    /// </summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of subscriptions.</returns>
    Task<List<Subscription>> GetSubscriptionsAsync(string address, CancellationToken ct = default);

    /// <summary>
    /// Get sessions for an account address.
    /// </summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="status">Session status filter (1 = active).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of sessions.</returns>
    Task<List<ChainSession>> GetSessionsAsync(string address, string status = "1", CancellationToken ct = default);

    /// <summary>
    /// Get nodes assigned to a plan.
    /// Uses limit=5000 because Sentinel pagination is broken for plan nodes.
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of nodes in the plan.</returns>
    Task<List<ChainNode>> GetPlanNodesAsync(int planId, CancellationToken ct = default);

    /// <summary>
    /// Discover subscription plans by probing IDs from 1 to maxId.
    /// </summary>
    /// <param name="maxId">Maximum plan ID to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered plans that exist on chain.</returns>
    Task<List<DiscoveredPlan>> DiscoverPlansAsync(int maxId = 100, CancellationToken ct = default);

    /// <summary>
    /// Query fee grants where the given address is the grantee.
    /// </summary>
    /// <param name="grantee">Grantee address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of fee grants.</returns>
    Task<List<FeeGrant>> QueryFeeGrantsAsync(string grantee, CancellationToken ct = default);

    /// <summary>
    /// Query all active sessions for a given wallet address.
    /// </summary>
    /// <param name="walletAddress">Bech32 wallet address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of active sessions.</returns>
    Task<IReadOnlyList<ActiveSession>> QueryActiveSessionsForAddressAsync(string walletAddress, CancellationToken ct = default);

    /// <summary>
    /// Query bandwidth allocation for a specific session.
    /// </summary>
    /// <param name="sessionId">On-chain session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Allocation details, or null if no allocation exists.</returns>
    Task<RawSessionAllocation?> QuerySessionAllocationAsync(ulong sessionId, CancellationToken ct = default);

    /// <summary>
    /// Query nodes assigned to a specific plan ID, using a single large request (pagination broken).
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of node addresses in the plan.</returns>
    Task<List<ChainNode>> QueryPlanNodesAsync(int planId, CancellationToken ct = default);

    /// <summary>
    /// Check whether an address has an active subscription for a given plan.
    /// </summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="planId">Plan ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if an active subscription exists for this plan.</returns>
    Task<bool> HasActiveSubscriptionAsync(string address, int planId, CancellationToken ct = default);

    /// <summary>
    /// Get all nodes available to a wallet through its active subscriptions.
    /// Queries the wallet's subscriptions, extracts plan IDs, fetches plan nodes,
    /// and returns a deduplicated list.
    /// </summary>
    /// <param name="walletAddress">Bech32 wallet address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deduplicated list of nodes available through the wallet's subscriptions.</returns>
    Task<IReadOnlyList<ChainNode>> GetAvailableNodesAsync(string walletAddress, CancellationToken ct = default);

    // ─── Fee Grant Workflow ───

    /// <summary>
    /// Query fee grants ISSUED BY a granter address.
    /// Unlike <see cref="QueryFeeGrantsAsync"/> which queries grants received,
    /// this queries grants the address has given to others.
    /// </summary>
    /// <param name="granter">Granter address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of fee grants issued by the granter.</returns>
    Task<IReadOnlyList<FeeGrant>> QueryFeeGrantsIssuedAsync(string granter, CancellationToken ct = default);

    /// <summary>
    /// Get fee grants expiring within a given number of days.
    /// Inspects BasicAllowance, PeriodicAllowance, and AllowedMsgAllowance expiration fields.
    /// </summary>
    /// <param name="address">Address to query (granter or grantee).</param>
    /// <param name="withinDays">Number of days to look ahead for expiry (default: 7).</param>
    /// <param name="role">"grantee" to query grants received, "granter" to query grants issued (default: "grantee").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of grants expiring within the specified window.</returns>
    Task<IReadOnlyList<ExpiringGrant>> GetExpiringGrantsAsync(string address, int withinDays = 7, string role = "grantee", CancellationToken ct = default);

    // ─── Operator Workflow ───

    /// <summary>
    /// Build fee grant messages for all subscribers of a plan.
    /// </summary>
    Task<SentinelMessage[]> BuildGrantPlanSubscribersAsync(int planId, string granterAddress, long? spendLimitUdvpn = null, DateTime? expiration = null, CancellationToken ct = default);

    /// <summary>
    /// Build messages to renew fee grants expiring within a given window.
    /// </summary>
    Task<SentinelMessage[]> BuildRenewExpiringGrantsAsync(string granterAddress, int withinDays = 7, long? newSpendLimitUdvpn = null, DateTime? newExpiration = null, CancellationToken ct = default);

    /// <summary>
    /// Find an existing active session between a wallet and a specific node.
    /// </summary>
    Task<ChainSession?> FindExistingSessionAsync(string walletAddress, string nodeAddress, CancellationToken ct = default);

    // ─── Provider ───

    /// <summary>
    /// Get a provider by its sentprov address.
    /// </summary>
    /// <param name="provAddress">Provider address (sentprov1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The provider, or null if not found.</returns>
    Task<Provider?> GetProviderByAddressAsync(string provAddress, CancellationToken ct = default);

    // ─── Query Helpers ───

    /// <summary>
    /// Get a single subscription by ID.
    /// </summary>
    /// <param name="id">Subscription ID on chain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subscription, or null if not found.</returns>
    Task<Subscription?> GetSubscriptionAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Query all subscribers of a plan.
    /// Optionally exclude an address (e.g. the plan owner) from the results.
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="excludeAddress">Optional address to exclude from results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of plan subscribers.</returns>
    Task<IReadOnlyList<PlanSubscriber>> QueryPlanSubscribersAsync(int planId, string? excludeAddress = null, CancellationToken ct = default);

    /// <summary>
    /// Get plan statistics with the owner filtered from subscriber counts.
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="ownerAddress">Plan owner's sent1... address (filtered from counts).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Plan statistics.</returns>
    Task<PlanStats> GetPlanStatsAsync(int planId, string ownerAddress, CancellationToken ct = default);

    // ─── Pricing ───

    /// <summary>
    /// Get standardized prices for a node — abstracts LCD price parsing entirely.
    /// Queries the node and returns formatted gigabyte and hourly prices.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Formatted node prices with P2P and udvpn values.</returns>
    Task<NodePrices> GetNodePricesAsync(string nodeAddress, CancellationToken ct = default);

    // ─── Authz ───

    /// <summary>
    /// Query authz grants between two addresses.
    /// </summary>
    /// <param name="granter">Granter address (sent1...).</param>
    /// <param name="grantee">Grantee address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of authz grants from granter to grantee.</returns>
    Task<IReadOnlyList<AuthzGrant>> QueryAuthzGrantsAsync(string granter, string grantee, CancellationToken ct = default);

    // ─── TX Event Extraction ───

    /// <summary>
    /// Extract the plan ID created by a <c>MsgCreatePlan</c> transaction.
    /// Queries the TX by hash, parses <c>sentinel.plan.v3.EventCreate</c>, and returns the new plan ID.
    /// Use this instead of <see cref="DiscoverPlansAsync"/> — a freshly-created plan with no
    /// subscribers or linked nodes will NOT appear in discovery.
    /// Polls briefly for LCD propagation.
    /// </summary>
    /// <param name="txHash">Transaction hash of the <c>MsgCreatePlan</c> broadcast.</param>
    /// <param name="timeoutMs">How long to wait for the TX to appear in LCD (default: 20000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plan ID, or null if the event wasn't found within the timeout.</returns>
    Task<long?> ExtractPlanIdFromTxAsync(string txHash, int timeoutMs = 20000, CancellationToken ct = default);

    /// <summary>
    /// Extract the subscription ID created by a <c>MsgStartSubscription</c> transaction.
    /// Queries the TX by hash, parses <c>sentinel.subscription.v3.EventCreate</c>.
    /// Polls briefly for LCD propagation.
    /// </summary>
    /// <param name="txHash">Transaction hash of the <c>MsgStartSubscription</c> broadcast.</param>
    /// <param name="timeoutMs">How long to wait for the TX to appear in LCD (default: 20000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subscription ID, or null if not found within the timeout.</returns>
    Task<long?> ExtractSubscriptionIdFromTxAsync(string txHash, int timeoutMs = 20000, CancellationToken ct = default);

    /// <summary>
    /// Extract the session ID created by a <c>MsgStartSession</c> transaction (pay-per-use or
    /// subscription-based). Queries the TX by hash and parses the session-create events
    /// (<c>sentinel.node.v3.EventCreateSession</c> or <c>sentinel.subscription.v3.EventCreateSession</c>).
    /// Polls briefly for LCD propagation — the deterministic replacement for
    /// <c>QueryActiveSessionsForAddressAsync</c>-based session lookups that fail on LCD lag.
    /// </summary>
    /// <param name="txHash">Transaction hash of the <c>MsgStartSession</c> broadcast.</param>
    /// <param name="timeoutMs">How long to wait for the TX to appear in LCD (default: 20000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session ID, or null if not found within the timeout.</returns>
    Task<long?> ExtractSessionIdFromTxAsync(string txHash, int timeoutMs = 20000, CancellationToken ct = default);

    // ─── Network Overview ───

    /// <summary>
    /// Get a high-level overview of the Sentinel network (total nodes, by country, average price).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Network overview with aggregated statistics.</returns>
    Task<NetworkOverview> GetNetworkOverviewAsync(CancellationToken ct = default);

    // ─── Health ───

    /// <summary>
    /// Check the health of all configured LCD endpoints by measuring response latency.
    /// Each endpoint is probed with a lightweight query.
    /// </summary>
    /// <param name="timeoutMs">Per-endpoint timeout in milliseconds (default: 5000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health results for each configured LCD endpoint.</returns>
    Task<IReadOnlyList<EndpointHealth>> CheckEndpointHealthAsync(int timeoutMs = 5000, CancellationToken ct = default);
}
