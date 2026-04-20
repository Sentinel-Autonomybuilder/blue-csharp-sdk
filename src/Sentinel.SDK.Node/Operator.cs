using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

/// <summary>
/// Operator entry point for Sentinel dVPN — node testers, plan managers, auditing tools.
///
/// <para>
/// This facade groups operator-side functionality: batch sessions, plan/provider/lease
/// management, fee grants, authz, network audit. These functions cost real P2P tokens
/// in bulk and are NOT intended for end-user VPN apps.
/// </para>
///
/// <para>
/// Mirrors the JavaScript SDK v34 <c>operator.js</c> entry point. When building a
/// consumer VPN app, use <see cref="Consumer"/> instead — accidentally calling
/// <c>BatchStartSessions</c> from a consumer app will drain the wallet.
/// </para>
///
/// <para>
/// The underlying message builders (<see cref="MessageBuilder"/>, <see cref="BatchBuilder"/>)
/// remain the source of truth; this class re-exports them under a focused surface so an
/// operator tool can be written against <c>Sentinel.SDK.Node.Operator</c> alone.
/// </para>
/// </summary>
public static class Operator
{
    // ─── Batch Session Operations ───────────────────────────────────────────

    /// <summary>
    /// Build a batch of subscription-based <c>MsgStartSession</c> messages (one per node)
    /// for a single broadcast. Use with <see cref="TransactionBuilder"/> to execute, then
    /// extract session IDs via <see cref="ExtractAllSessionIds"/>.
    /// </summary>
    public static SentinelMessage[] BuildBatchStartSessions(
        string fromAddress,
        (string NodeAddress, ulong SubscriptionId)[] entries)
    {
        return entries
            .Select(e => MessageBuilder.SubStartSession(fromAddress, e.SubscriptionId, e.NodeAddress))
            .ToArray();
    }

    /// <summary>
    /// Build a batch of pay-per-use <c>MsgStartSession</c> messages, one per node, each with
    /// its own gigabyte or hours budget. Useful for operator audit tooling.
    /// </summary>
    public static SentinelMessage[] BuildBatchStartSessionsDirect(
        string fromAddress,
        (string NodeAddress, long Gigabytes, long Hours, PriceEntry? MaxPrice)[] entries)
    {
        return entries
            .Select(e => MessageBuilder.StartSession(fromAddress, e.NodeAddress, e.Gigabytes, e.MaxPrice, e.Hours))
            .ToArray();
    }

    /// <summary>Extract all session IDs created by a batched <c>MsgStartSession</c> TX.</summary>
    public static ulong[] ExtractAllSessionIds(TxResult txResult, ChainClient client)
        => BatchBuilder.ExtractAllSessionIds(txResult, client);

    /// <summary>
    /// Extract the plan ID emitted by a <c>MsgCreatePlan</c> TX by reading its events.
    /// Polls the LCD every 2s up to <paramref name="timeoutMs"/>. Returns null on timeout.
    /// </summary>
    public static Task<long?> ExtractPlanIdFromTxAsync(IChainClient chain, string txHash, int timeoutMs = 20000, CancellationToken ct = default)
        => chain.ExtractPlanIdFromTxAsync(txHash, timeoutMs, ct);

    /// <summary>
    /// Extract the subscription ID emitted by a <c>MsgStartSubscription</c> TX by reading its events.
    /// Polls the LCD every 2s up to <paramref name="timeoutMs"/>. Returns null on timeout.
    /// </summary>
    public static Task<long?> ExtractSubscriptionIdFromTxAsync(IChainClient chain, string txHash, int timeoutMs = 20000, CancellationToken ct = default)
        => chain.ExtractSubscriptionIdFromTxAsync(txHash, timeoutMs, ct);

    /// <summary>
    /// Extract the session ID emitted by a <c>MsgStartSession</c> TX by reading its events.
    /// RPC-first with LCD fallback. Polls every 2s up to <paramref name="timeoutMs"/>. Returns null on timeout.
    /// </summary>
    public static Task<long?> ExtractSessionIdFromTxAsync(IChainClient chain, string txHash, int timeoutMs = 20000, CancellationToken ct = default)
        => chain.ExtractSessionIdFromTxAsync(txHash, timeoutMs, ct);

    /// <summary>Build a batch of <c>MsgSend</c> transfers to many recipients.</summary>
    public static SentinelMessage[] BuildBatchSend(string fromAddress, (string ToAddress, long Udvpn)[] transfers)
        => transfers.Select(t => MessageBuilder.Send(fromAddress, t.ToAddress, t.Udvpn)).ToArray();

    /// <summary>Build a batch of <c>MsgLinkNode</c> messages to link multiple nodes to a plan.</summary>
    public static SentinelMessage[] BuildBatchLink(string providerAddress, ulong planId, string[] nodeAddresses)
        => nodeAddresses.Select(n => MessageBuilder.LinkNode(providerAddress, planId, n)).ToArray();

    // ─── Plan Management ────────────────────────────────────────────────────

    /// <inheritdoc cref="MessageBuilder.CreatePlan"/>
    public static SentinelMessage EncodeCreatePlan(string from, string bytes, long durationSeconds, PriceEntry[] prices, bool isPrivate = false)
        => MessageBuilder.CreatePlan(from, bytes, durationSeconds, prices, isPrivate);

    /// <inheritdoc cref="MessageBuilder.UpdatePlanStatus"/>
    public static SentinelMessage EncodeUpdatePlanStatus(string from, ulong planId, int status)
        => MessageBuilder.UpdatePlanStatus(from, planId, status);

    /// <inheritdoc cref="MessageBuilder.LinkNode"/>
    public static SentinelMessage EncodeLinkNode(string from, ulong planId, string nodeAddress)
        => MessageBuilder.LinkNode(from, planId, nodeAddress);

    /// <inheritdoc cref="MessageBuilder.UnlinkNode"/>
    public static SentinelMessage EncodeUnlinkNode(string from, ulong planId, string nodeAddress)
        => MessageBuilder.UnlinkNode(from, planId, nodeAddress);

    /// <inheritdoc cref="MessageBuilder.PlanStartSession"/>
    public static SentinelMessage EncodePlanStartSession(string from, ulong planId, string denom = "udvpn", string? nodeAddress = null)
        => MessageBuilder.PlanStartSession(from, planId, denom, nodeAddress);

    /// <inheritdoc cref="MessageBuilder.SubStartSession"/>
    public static SentinelMessage EncodeSubStartSession(string from, ulong subscriptionId, string nodeAddress)
        => MessageBuilder.SubStartSession(from, subscriptionId, nodeAddress);

    // ─── Provider Management ────────────────────────────────────────────────

    /// <inheritdoc cref="MessageBuilder.RegisterProvider"/>
    public static SentinelMessage EncodeRegisterProvider(string from, string name, string? identity = null, string? website = null, string? description = null)
        => MessageBuilder.RegisterProvider(from, name, identity, website, description);

    /// <inheritdoc cref="MessageBuilder.UpdateProviderDetails"/>
    public static SentinelMessage EncodeUpdateProviderDetails(string from, string? name = null, string? identity = null, string? website = null, string? description = null)
        => MessageBuilder.UpdateProviderDetails(from, name, identity, website, description);

    /// <inheritdoc cref="MessageBuilder.UpdateProviderStatus"/>
    public static SentinelMessage EncodeUpdateProviderStatus(string from, int status)
        => MessageBuilder.UpdateProviderStatus(from, status);

    // ─── Lease Management ───────────────────────────────────────────────────

    /// <inheritdoc cref="MessageBuilder.StartLease"/>
    public static SentinelMessage EncodeStartLease(string from, string nodeAddress, long hours, PriceEntry? maxPrice = null, int renewalPricePolicy = 0)
        => MessageBuilder.StartLease(from, nodeAddress, hours, maxPrice, renewalPricePolicy);

    /// <inheritdoc cref="MessageBuilder.EndLease"/>
    public static SentinelMessage EncodeEndLease(string from, ulong leaseId)
        => MessageBuilder.EndLease(from, leaseId);

    // ─── Fee Grants ─────────────────────────────────────────────────────────

    /// <inheritdoc cref="MessageBuilder.GrantFeeAllowance"/>
    public static SentinelMessage EncodeGrantFeeAllowance(string granter, string grantee, long spendLimitUdvpn, DateTime? expiration = null)
        => MessageBuilder.GrantFeeAllowance(granter, grantee, spendLimitUdvpn, expiration);

    /// <summary>Query fee grants issued BY the granter address.</summary>
    public static Task<IReadOnlyList<FeeGrant>> QueryFeeGrantsIssuedAsync(IChainClient chain, string granter, CancellationToken ct = default)
        => chain.QueryFeeGrantsIssuedAsync(granter, ct);

    /// <summary>Query fee grants RECEIVED by the grantee address.</summary>
    public static Task<List<FeeGrant>> QueryFeeGrantsAsync(IChainClient chain, string grantee, CancellationToken ct = default)
        => chain.QueryFeeGrantsAsync(grantee, ct);

    /// <summary>Return grants expiring within the window.</summary>
    public static Task<IReadOnlyList<ExpiringGrant>> GetExpiringGrantsAsync(IChainClient chain, string address, int withinDays = 7, string role = "grantee", CancellationToken ct = default)
        => chain.GetExpiringGrantsAsync(address, withinDays, role, ct);

    /// <summary>Build fee-grant messages for every subscriber of a plan (operator workflow).</summary>
    public static Task<SentinelMessage[]> BuildGrantPlanSubscribersAsync(IChainClient chain, int planId, string granterAddress, long? spendLimitUdvpn = null, DateTime? expiration = null, CancellationToken ct = default)
        => chain.BuildGrantPlanSubscribersAsync(planId, granterAddress, spendLimitUdvpn, expiration, ct);

    /// <summary>Build messages to renew grants expiring within the window.</summary>
    public static Task<SentinelMessage[]> BuildRenewExpiringGrantsAsync(IChainClient chain, string granterAddress, int withinDays = 7, long? newSpendLimitUdvpn = null, DateTime? newExpiration = null, CancellationToken ct = default)
        => chain.BuildRenewExpiringGrantsAsync(granterAddress, withinDays, newSpendLimitUdvpn, newExpiration, ct);

    // ─── Authz ──────────────────────────────────────────────────────────────

    /// <summary>Query authz grants between two addresses.</summary>
    public static Task<IReadOnlyList<AuthzGrant>> QueryAuthzGrantsAsync(IChainClient chain, string granter, string grantee, CancellationToken ct = default)
        => chain.QueryAuthzGrantsAsync(granter, grantee, ct);

    // ─── Plan Discovery ─────────────────────────────────────────────────────

    /// <summary>Discover active plans by probing IDs up to <paramref name="maxId"/>.</summary>
    public static Task<List<DiscoveredPlan>> DiscoverPlansAsync(IChainClient chain, int maxId = 100, CancellationToken ct = default)
        => chain.DiscoverPlansAsync(maxId, ct);

    /// <summary>Query subscribers of a plan, optionally excluding the owner.</summary>
    public static Task<IReadOnlyList<PlanSubscriber>> QueryPlanSubscribersAsync(IChainClient chain, int planId, string? excludeAddress = null, CancellationToken ct = default)
        => chain.QueryPlanSubscribersAsync(planId, excludeAddress, ct);

    /// <summary>Get plan statistics with the owner filtered from subscriber counts.</summary>
    public static Task<PlanStats> GetPlanStatsAsync(IChainClient chain, int planId, string ownerAddress, CancellationToken ct = default)
        => chain.GetPlanStatsAsync(planId, ownerAddress, ct);

    /// <summary>Nodes assigned to a plan.</summary>
    public static Task<List<ChainNode>> QueryPlanNodesAsync(IChainClient chain, int planId, CancellationToken ct = default)
        => chain.QueryPlanNodesAsync(planId, ct);

    // ─── Network Audit ──────────────────────────────────────────────────────

    /// <summary>Create a <see cref="NodeTester"/> configured with an adapter to the app's own connect/disconnect.</summary>
    public static NodeTester CreateNodeTester(INodeTestAdapter adapter) => new(adapter);

    /// <summary>High-level network overview (totals, country breakdown, avg price).</summary>
    public static Task<NetworkOverview> GetNetworkOverviewAsync(IChainClient chain, CancellationToken ct = default)
        => chain.GetNetworkOverviewAsync(ct);

    /// <summary>Health-check every configured LCD endpoint.</summary>
    public static Task<IReadOnlyList<EndpointHealth>> CheckEndpointHealthAsync(IChainClient chain, int timeoutMs = 5000, CancellationToken ct = default)
        => chain.CheckEndpointHealthAsync(timeoutMs, ct);
}
