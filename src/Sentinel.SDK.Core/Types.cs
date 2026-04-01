namespace Sentinel.SDK.Core;

// ─── Balance ───

/// <summary>
/// Represents a wallet balance with micro-denomination, decimal, and display values.
/// </summary>
/// <param name="Udvpn">Balance in micro-denomination (1 P2P = 1,000,000 udvpn).</param>
/// <param name="P2P">Balance in whole P2P tokens.</param>
/// <param name="Display">Human-readable display string (e.g. "123.45 P2P").</param>
public record Balance(long Udvpn, decimal P2P, string Display);

// ─── Chain Node ───

/// <summary>
/// A dVPN node registered on the Sentinel chain.
/// </summary>
/// <param name="Address">Node address (sentnode1...).</param>
/// <param name="RemoteAddrs">Advertised remote addresses for direct connection.</param>
/// <param name="RemoteUrl">Primary remote URL for the node's API.</param>
/// <param name="GigabytePrices">Per-gigabyte pricing entries.</param>
/// <param name="HourlyPrices">Per-hour pricing entries.</param>
/// <param name="Status">Node status (1 = active, 2 = inactive).</param>
public record ChainNode(
    string Address,
    string[] RemoteAddrs,
    string? RemoteUrl,
    PriceEntry[] GigabytePrices,
    PriceEntry[] HourlyPrices,
    int Status
);

// ─── Price Entry ───

/// <summary>
/// A pricing entry with denomination and value information.
/// </summary>
/// <param name="Denom">Token denomination (e.g. "udvpn", "uatom").</param>
/// <param name="BaseValue">Raw on-chain value string.</param>
/// <param name="QuoteValue">Quoted value string (may include decimal precision).</param>
public record PriceEntry(string Denom, string BaseValue, string QuoteValue)
{
    /// <summary>Parsed udvpn amount from QuoteValue (or BaseValue fallback). 0 if parse fails.</summary>
    public long UdvpnAmount => long.TryParse(QuoteValue, out var v) ? v
        : long.TryParse(BaseValue?.Split('.')[0], out var b) ? b : 0;

    /// <summary>Formatted P2P display string (e.g. "0.04 P2P").</summary>
    public string DisplayPrice => $"{UdvpnAmount / 1_000_000.0:F2} P2P";
}

// ─── Subscription ───

/// <summary>
/// A subscription linking an account to a node or plan.
/// </summary>
/// <param name="Id">Subscription ID on chain.</param>
/// <param name="AccAddress">Account address of the subscriber (sent1...).</param>
/// <param name="PlanId">Plan ID if subscription is plan-based, "0" otherwise.</param>
/// <param name="Price">Price entry if pay-as-you-go, null if plan-based.</param>
/// <param name="Status">Subscription status string.</param>
/// <param name="StartAt">ISO timestamp when the subscription started.</param>
/// <param name="InactiveAt">ISO timestamp when the subscription becomes inactive.</param>
public record Subscription(
    string Id,
    string AccAddress,
    string PlanId,
    PriceEntry? Price,
    string Status,
    string StartAt,
    string InactiveAt
);

// ─── Chain Session ───

/// <summary>
/// An active or historical bandwidth session on a node.
/// </summary>
/// <param name="Id">Session ID on chain.</param>
/// <param name="AccAddress">Account address of the session owner.</param>
/// <param name="NodeAddress">Node address hosting the session.</param>
/// <param name="DownloadBytes">Total bytes downloaded as string.</param>
/// <param name="UploadBytes">Total bytes uploaded as string.</param>
/// <param name="MaxBytes">Maximum allowed bytes as string.</param>
/// <param name="Duration">Session duration elapsed (e.g. "44.728960452s").</param>
/// <param name="MaxDuration">Maximum session duration ("0s" for GB-based sessions).</param>
/// <param name="Status">Session status string.</param>
/// <param name="InactiveAt">When the session becomes inactive (ISO 8601).</param>
/// <param name="StartAt">When the session started (ISO 8601).</param>
public record ChainSession(
    string Id,
    string AccAddress,
    string NodeAddress,
    string DownloadBytes,
    string UploadBytes,
    string MaxBytes,
    string? Duration,
    string? MaxDuration,
    string Status,
    string? InactiveAt,
    string? StartAt
);

// ─── Discovered Plan ───

/// <summary>
/// A discovered subscription plan from the chain.
/// </summary>
/// <param name="Id">Plan ID.</param>
/// <param name="Subscribers">Number of active subscribers.</param>
/// <param name="NodeCount">Number of nodes in the plan.</param>
/// <param name="Price">Plan price entry, null if free or unknown.</param>
public record DiscoveredPlan(int Id, int Subscribers, int NodeCount, PriceEntry? Price);

// ─── Fee Grant ───

/// <summary>
/// A fee grant allowing one account to pay fees on behalf of another.
/// </summary>
/// <param name="Granter">Address of the granter (fee payer).</param>
/// <param name="Grantee">Address of the grantee (fee beneficiary).</param>
/// <param name="Allowance">Raw allowance object from the chain response.</param>
public record FeeGrant(string Granter, string Grantee, object Allowance);

// ─── Transaction Result ───

/// <summary>
/// Result of a broadcast transaction.
/// </summary>
/// <param name="TxHash">Transaction hash (hex, uppercase).</param>
/// <param name="Code">Result code (0 = success).</param>
/// <param name="RawLog">Raw log string from the chain.</param>
/// <param name="Success">True if Code == 0.</param>
public record TxResult(string TxHash, int Code, string RawLog, bool Success);

// ─── Expiring Grant ───

/// <summary>
/// A fee grant that is expiring soon or has already expired.
/// </summary>
/// <param name="Granter">Address of the granter (fee payer).</param>
/// <param name="Grantee">Address of the grantee (fee beneficiary).</param>
/// <param name="ExpiresAt">UTC expiration date, or null if no expiry set.</param>
/// <param name="DaysLeft">Days remaining until expiry, or null if no expiry set.</param>
public record ExpiringGrant(string Granter, string Grantee, DateTime? ExpiresAt, int? DaysLeft);

// ─── Provider ───

/// <summary>
/// A dVPN provider registered on the Sentinel chain.
/// </summary>
/// <param name="Address">Provider address (sentprov1...).</param>
/// <param name="Name">Provider display name.</param>
/// <param name="Identity">Provider identity string.</param>
/// <param name="Website">Provider website URL.</param>
/// <param name="Description">Provider description text.</param>
/// <param name="Status">Provider status (1 = active, 2 = inactive).</param>
public record Provider(string Address, string Name, string Identity, string Website, string Description, int Status);

// ─── Plan Subscriber ───

/// <summary>
/// A subscriber entry for a subscription plan.
/// </summary>
/// <param name="Address">Subscriber account address (sent1...).</param>
/// <param name="Status">Subscription status (e.g. active, inactive).</param>
/// <param name="Id">Subscription ID on chain.</param>
public record PlanSubscriber(string Address, int Status, string Id);

// ─── Plan Stats ───

/// <summary>
/// Aggregated statistics for a subscription plan.
/// </summary>
/// <param name="SubscriberCount">Number of subscribers excluding the plan owner.</param>
/// <param name="TotalOnChain">Total subscriber count as reported by the chain, or null if unavailable.</param>
/// <param name="OwnerSubscribed">Whether the plan owner has a subscription to their own plan.</param>
public record PlanStats(int SubscriberCount, int? TotalOnChain, bool OwnerSubscribed);

// ─── Session Cost ───

/// <summary>
/// Estimated cost to start a session with a node.
/// </summary>
/// <param name="Udvpn">Bandwidth cost in micro-denomination (udvpn).</param>
/// <param name="P2P">Bandwidth cost in whole P2P tokens.</param>
/// <param name="GasUdvpn">Estimated gas cost in udvpn.</param>
/// <param name="TotalUdvpn">Total estimated cost (bandwidth + gas) in udvpn.</param>
public record SessionCost(long Udvpn, decimal P2P, long GasUdvpn, long TotalUdvpn);

// ─── Batch Fee ───

/// <summary>
/// Estimated fee for a batch of messages.
/// </summary>
/// <param name="Gas">Total gas units required.</param>
/// <param name="Amount">Fee amount in udvpn.</param>
/// <param name="GasString">Gas as a string (for TX construction).</param>
/// <param name="AmountString">Amount as a string (for TX construction).</param>
public record BatchFee(long Gas, long Amount, string GasString, string AmountString);

// ─── Endpoint Health ───

/// <summary>
/// Health check result for a single LCD or RPC endpoint.
/// </summary>
/// <param name="Url">Endpoint URL that was checked.</param>
/// <param name="Name">Human-readable name or hostname of the endpoint.</param>
/// <param name="LatencyMs">Response latency in milliseconds, or null if the endpoint is unreachable.</param>
public record EndpointHealth(string Url, string Name, int? LatencyMs);

// ─── Node Prices ───

/// <summary>
/// Formatted price details for a single pricing category (per-GB or per-hour).
/// </summary>
/// <param name="Udvpn">Price in micro-denomination (udvpn).</param>
/// <param name="P2P">Price in whole P2P tokens.</param>
/// <param name="Raw">Raw PriceEntry from the chain, or null if not available.</param>
public record PriceDetail(long Udvpn, decimal P2P, PriceEntry? Raw);

/// <summary>
/// Standardized node prices for both gigabyte and hourly pricing.
/// Matches the JS SDK's <c>getNodePrices()</c> return format.
/// </summary>
/// <param name="Gigabyte">Per-gigabyte pricing in P2P.</param>
/// <param name="Hourly">Per-hour pricing in P2P.</param>
/// <param name="Denom">Display denomination ("P2P").</param>
/// <param name="NodeAddress">Node address queried (sentnode1...).</param>
public record NodePrices(PriceDetail Gigabyte, PriceDetail Hourly, string Denom, string NodeAddress);

// ─── Authz Grant ───

/// <summary>
/// An authz grant allowing one account to execute messages on behalf of another.
/// </summary>
/// <param name="Granter">Address of the granter (sent1...).</param>
/// <param name="Grantee">Address of the grantee (sent1...).</param>
/// <param name="MsgTypeUrl">Protobuf message type URL this grant authorizes.</param>
/// <param name="Expiration">ISO 8601 expiration timestamp, or null if no expiry.</param>
public record AuthzGrant(string Granter, string Grantee, string MsgTypeUrl, string? Expiration);

// ─── Network Overview ───

/// <summary>
/// High-level overview of the Sentinel dVPN network.
/// </summary>
/// <param name="TotalNodes">Total number of active nodes on chain.</param>
/// <param name="ByCountry">Node counts grouped by two-letter country code (best-effort from remote URLs).</param>
/// <param name="AverageGbPrice">Average per-GB price in P2P tokens across all nodes with udvpn pricing.</param>
public record NetworkOverview(int TotalNodes, Dictionary<string, int> ByCountry, decimal AverageGbPrice);

// NOTE: SentinelException and all derived exception classes are now in SentinelErrors.cs
