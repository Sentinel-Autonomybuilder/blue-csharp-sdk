using System.Globalization;

namespace Sentinel.SDK.Core;

// All protobuf wire-format primitives are in ProtobufWriter.
using static ProtobufWriter;

/// <summary>Record representing a chain message ready for broadcast.</summary>
/// <param name="TypeUrl">Fully-qualified protobuf type URL (e.g. /sentinel.node.v3.MsgStartSessionRequest).</param>
/// <param name="Value">Protobuf wire-format encoded message bytes.</param>
public record SentinelMessage(string TypeUrl, byte[] Value);

/// <summary>
/// Builders for all 19 Sentinel chain message types.
/// Each method encodes protobuf fields using manual wire-format encoding
/// and returns a <see cref="SentinelMessage"/> ready for
/// <see cref="TransactionBuilder.BroadcastAsync"/>.
/// </summary>
public static class MessageBuilder
{
    // ─── Node Session ────────────────────────────────────────────────

    /// <summary>
    /// Start a direct pay-per-GB or pay-per-hour session on a node.
    /// Proto: sentinel.node.v3.MsgStartSessionRequest
    /// Fields: from(1), node_address(2), gigabytes(3:int64), hours(4:int64), max_price(5:Price).
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <param name="gigabytes">Bandwidth in GB (default: 1). Set to 0 when using hourly pricing.</param>
    /// <param name="maxPrice">Optional maximum price (per-GB or per-hour depending on session type).</param>
    /// <param name="hours">Hours to purchase (default: 0). Set to 1+ for hourly sessions (gigabytes must be 0).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage StartSession(
        string from,
        string nodeAddress,
        long gigabytes = 1,
        PriceEntry? maxPrice = null,
        long hours = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);
        if (gigabytes < 0 || gigabytes > 100)
            throw new ArgumentOutOfRangeException(nameof(gigabytes), "Must be 0-100");
        if (hours < 0)
            throw new ArgumentOutOfRangeException(nameof(hours), "Must be >= 0");
        if (gigabytes == 0 && hours == 0)
            throw new ArgumentException("Either gigabytes or hours must be > 0");

        using var s = new MemoryStream();

        // field 1: from (string)
        WriteStringField(s, 1, from);

        // field 2: node_address (string)
        WriteStringField(s, 2, nodeAddress);

        // field 3: gigabytes (int64)
        if (gigabytes != 0)
        {
            WriteVarintField(s, 3, (ulong)gigabytes);
        }

        // field 4: hours (int64)
        if (hours != 0)
        {
            WriteVarintField(s, 4, (ulong)hours);
        }

        // field 5: max_price (embedded Price)
        if (maxPrice != null)
        {
            WriteEmbeddedField(s, 5, EncodePrice(maxPrice));
        }

        return new SentinelMessage(
            "/sentinel.node.v3.MsgStartSessionRequest",
            s.ToArray());
    }

    /// <summary>
    /// Cancel/end an active session.
    /// Proto: sentinel.session.v3.MsgCancelSessionRequest
    /// NOTE: v3 renamed MsgEndSession to MsgCancelSession and removed the rating field.
    /// Fields: from(1), id(2:uint64).
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="sessionId">Session ID on chain.</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage EndSession(string from, ulong sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (sessionId == 0)
            throw new ArgumentOutOfRangeException(nameof(sessionId), "Must be > 0");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, sessionId);

        return new SentinelMessage(
            "/sentinel.session.v3.MsgCancelSessionRequest",
            s.ToArray());
    }

    // ─── Subscription ────────────────────────────────────────────────

    /// <summary>
    /// Subscribe to a plan (without starting a session).
    /// Proto: sentinel.subscription.v3.MsgStartSubscriptionRequest
    /// Fields: from(1), id(2:uint64), denom(3).
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="planId">Plan ID to subscribe to.</param>
    /// <param name="denom">Payment denomination (default: "udvpn").</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage StartSubscription(
        string from,
        ulong planId,
        string denom = "udvpn",
        int renewalPricePolicy = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (planId == 0)
            throw new ArgumentOutOfRangeException(nameof(planId), "Must be > 0");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, planId);
        WriteStringField(s, 3, denom);

        // field 4: renewal_price_policy (int64, only if non-zero — matches JS SDK)
        if (renewalPricePolicy != 0)
        {
            WriteVarintField(s, 4, (ulong)renewalPricePolicy);
        }

        return new SentinelMessage(
            "/sentinel.subscription.v3.MsgStartSubscriptionRequest",
            s.ToArray());
    }

    /// <summary>
    /// Start session on an existing subscription.
    /// Proto: sentinel.subscription.v3.MsgStartSessionRequest
    /// Fields: from(1), id(2:uint64), node_address(3).
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="subscriptionId">Subscription ID on chain.</param>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage SubStartSession(
        string from,
        ulong subscriptionId,
        string nodeAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (subscriptionId == 0)
            throw new ArgumentOutOfRangeException(nameof(subscriptionId), "Must be > 0");
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, subscriptionId);
        WriteStringField(s, 3, nodeAddress);

        return new SentinelMessage(
            "/sentinel.subscription.v3.MsgStartSessionRequest",
            s.ToArray());
    }

    // ─── Plan ────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribe to plan AND start session in one TX.
    /// Proto: sentinel.plan.v3.MsgStartSessionRequest
    /// Fields: from(1), id(2:uint64), denom(3), renewal_price_policy(4:int64), node_address(5).
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="planId">Plan ID to subscribe to.</param>
    /// <param name="denom">Payment denomination (default: "udvpn").</param>
    /// <param name="nodeAddress">Optional node address to start session on.</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage PlanStartSession(
        string from,
        ulong planId,
        string denom = "udvpn",
        string? nodeAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (planId == 0)
            throw new ArgumentOutOfRangeException(nameof(planId), "Must be > 0");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, planId);
        WriteStringField(s, 3, denom);

        // field 4: renewal_price_policy — omitted (0 = unspecified)

        if (!string.IsNullOrEmpty(nodeAddress))
        {
            WriteStringField(s, 5, nodeAddress);
        }

        return new SentinelMessage(
            "/sentinel.plan.v3.MsgStartSessionRequest",
            s.ToArray());
    }

    /// <summary>
    /// Create a new subscription plan (starts INACTIVE).
    /// Proto: sentinel.plan.v3.MsgCreatePlanRequest
    /// Fields: from(1), bytes(2:string), duration(3:Duration), prices(4:Price[], repeated), is_private(5:bool).
    /// </summary>
    /// <param name="from">Provider address (sentprov1...).</param>
    /// <param name="bytes">Total bandwidth as string (e.g. "10000000000").</param>
    /// <param name="durationSeconds">Plan validity in seconds.</param>
    /// <param name="prices">Subscription cost entries.</param>
    /// <param name="isPrivate">Whether the plan is private (default: false).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage CreatePlan(
        string from,
        string bytes,
        long durationSeconds,
        PriceEntry[] prices,
        bool isPrivate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(bytes);
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Must be > 0");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteStringField(s, 2, bytes);

        // field 3: duration (embedded google.protobuf.Duration)
        var durationBytes = EncodeDuration(durationSeconds);
        WriteEmbeddedField(s, 3, durationBytes);

        // field 4: prices (repeated Price)
        foreach (var price in prices)
        {
            WriteEmbeddedField(s, 4, EncodePrice(price));
        }

        // field 5: is_private (bool = varint, only if true)
        if (isPrivate)
        {
            WriteVarintField(s, 5, 1);
        }

        return new SentinelMessage(
            "/sentinel.plan.v3.MsgCreatePlanRequest",
            s.ToArray());
    }

    /// <summary>
    /// Activate or deactivate a plan.
    /// Proto: sentinel.plan.v3.MsgUpdatePlanStatusRequest
    /// Fields: from(1), id(2:uint64), status(3:enum).
    /// Status: 1=active, 2=inactive_pending, 3=inactive.
    /// </summary>
    /// <param name="from">Provider address (sentprov1...).</param>
    /// <param name="planId">Plan ID on chain.</param>
    /// <param name="status">Target status (1=active, 2=inactive_pending, 3=inactive).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage UpdatePlanStatus(
        string from,
        ulong planId,
        int status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (planId == 0)
            throw new ArgumentOutOfRangeException(nameof(planId), "Must be > 0");
        if (status < 1 || status > 3)
            throw new ArgumentOutOfRangeException(nameof(status), "Must be 1-3");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, planId);
        WriteVarintField(s, 3, (ulong)status);

        return new SentinelMessage(
            "/sentinel.plan.v3.MsgUpdatePlanStatusRequest",
            s.ToArray());
    }

    /// <summary>
    /// Link a node to a plan.
    /// Proto: sentinel.plan.v3.MsgLinkNodeRequest
    /// Fields: from(1), id(2:uint64), node_address(3).
    /// </summary>
    /// <param name="from">Provider address (sentprov1...).</param>
    /// <param name="planId">Plan ID on chain.</param>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage LinkNode(
        string from,
        ulong planId,
        string nodeAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (planId == 0)
            throw new ArgumentOutOfRangeException(nameof(planId), "Must be > 0");
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, planId);
        WriteStringField(s, 3, nodeAddress);

        return new SentinelMessage(
            "/sentinel.plan.v3.MsgLinkNodeRequest",
            s.ToArray());
    }

    /// <summary>
    /// Unlink a node from a plan.
    /// Proto: sentinel.plan.v3.MsgUnlinkNodeRequest
    /// Fields: from(1), id(2:uint64), node_address(3).
    /// </summary>
    /// <param name="from">Provider address (sentprov1...).</param>
    /// <param name="planId">Plan ID on chain.</param>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage UnlinkNode(
        string from,
        ulong planId,
        string nodeAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (planId == 0)
            throw new ArgumentOutOfRangeException(nameof(planId), "Must be > 0");
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, planId);
        WriteStringField(s, 3, nodeAddress);

        return new SentinelMessage(
            "/sentinel.plan.v3.MsgUnlinkNodeRequest",
            s.ToArray());
    }

    // ─── Provider ────────────────────────────────────────────────────

    /// <summary>
    /// Register as a dVPN provider.
    /// Proto: sentinel.provider.v3.MsgRegisterProviderRequest
    /// Fields: from(1), name(2), identity(3), website(4), description(5).
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="name">Provider display name.</param>
    /// <param name="identity">Optional identity string.</param>
    /// <param name="website">Optional website URL.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage RegisterProvider(
        string from,
        string name,
        string? identity = null,
        string? website = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteStringField(s, 2, name);

        if (!string.IsNullOrEmpty(identity))
        {
            WriteStringField(s, 3, identity);
        }

        if (!string.IsNullOrEmpty(website))
        {
            WriteStringField(s, 4, website);
        }

        if (!string.IsNullOrEmpty(description))
        {
            WriteStringField(s, 5, description);
        }

        return new SentinelMessage(
            "/sentinel.provider.v3.MsgRegisterProviderRequest",
            s.ToArray());
    }

    /// <summary>
    /// Update provider details.
    /// Proto: sentinel.provider.v3.MsgUpdateProviderDetailsRequest
    /// Fields: from(1), name(2), identity(3), website(4), description(5).
    /// </summary>
    /// <param name="from">Provider address (sentprov1...).</param>
    /// <param name="name">Optional new display name.</param>
    /// <param name="identity">Optional new identity string.</param>
    /// <param name="website">Optional new website URL.</param>
    /// <param name="description">Optional new description.</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage UpdateProviderDetails(
        string from,
        string? name = null,
        string? identity = null,
        string? website = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);

        if (!string.IsNullOrEmpty(name))
        {
            WriteStringField(s, 2, name);
        }

        if (!string.IsNullOrEmpty(identity))
        {
            WriteStringField(s, 3, identity);
        }

        if (!string.IsNullOrEmpty(website))
        {
            WriteStringField(s, 4, website);
        }

        if (!string.IsNullOrEmpty(description))
        {
            WriteStringField(s, 5, description);
        }

        return new SentinelMessage(
            "/sentinel.provider.v3.MsgUpdateProviderDetailsRequest",
            s.ToArray());
    }

    /// <summary>
    /// Activate or deactivate a provider.
    /// Proto: sentinel.provider.v3.MsgUpdateProviderStatusRequest
    /// Fields: from(1), status(2:enum).
    /// Status: 1=active, 2=inactive_pending, 3=inactive.
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="status">Target status (1=active, 2=inactive_pending, 3=inactive).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage UpdateProviderStatus(
        string from,
        int status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (status < 1 || status > 3)
            throw new ArgumentOutOfRangeException(nameof(status), "Must be 1-3");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, (ulong)status);

        return new SentinelMessage(
            "/sentinel.provider.v3.MsgUpdateProviderStatusRequest",
            s.ToArray());
    }

    // ─── Lease ───────────────────────────────────────────────────────

    /// <summary>
    /// Lease a node from its operator.
    /// Proto: sentinel.lease.v1.MsgStartLeaseRequest
    /// Fields: from(1), node_address(2), hours(3:int64), max_price(4:Price).
    /// </summary>
    /// <param name="from">Provider address (sentprov1...).</param>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <param name="hours">Lease duration in hours.</param>
    /// <param name="maxPrice">Optional max hourly price (must match node's hourly_prices).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage StartLease(
        string from,
        string nodeAddress,
        long hours,
        PriceEntry? maxPrice = null,
        int renewalPricePolicy = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);
        if (hours <= 0)
            throw new ArgumentOutOfRangeException(nameof(hours), "Must be > 0");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteStringField(s, 2, nodeAddress);
        WriteVarintField(s, 3, (ulong)hours);

        if (maxPrice != null)
        {
            WriteEmbeddedField(s, 4, EncodePrice(maxPrice));
        }

        // field 5: renewal_price_policy (int64, only if non-zero — matches JS SDK)
        if (renewalPricePolicy != 0)
        {
            WriteVarintField(s, 5, (ulong)renewalPricePolicy);
        }

        return new SentinelMessage(
            "/sentinel.lease.v1.MsgStartLeaseRequest",
            s.ToArray());
    }

    /// <summary>
    /// End an active lease.
    /// Proto: sentinel.lease.v1.MsgEndLeaseRequest
    /// Fields: from(1), id(2:uint64).
    /// </summary>
    /// <param name="from">Provider address (sentprov1...).</param>
    /// <param name="leaseId">Lease ID on chain.</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage EndLease(string from, ulong leaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        if (leaseId == 0)
            throw new ArgumentOutOfRangeException(nameof(leaseId), "Must be > 0");

        using var s = new MemoryStream();

        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, leaseId);

        return new SentinelMessage(
            "/sentinel.lease.v1.MsgEndLeaseRequest",
            s.ToArray());
    }

    // ─── Subscription Management (v3 — from sentinel-go-sdk) ─────────

    /// <summary>Cancel a subscription. Proto: sentinel.subscription.v3.MsgCancelSubscriptionRequest</summary>
    public static SentinelMessage CancelSubscription(string from, ulong subscriptionId)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, subscriptionId);
        return new SentinelMessage("/sentinel.subscription.v3.MsgCancelSubscriptionRequest", s.ToArray());
    }

    /// <summary>Renew an expiring subscription. Proto: sentinel.subscription.v3.MsgRenewSubscriptionRequest</summary>
    public static SentinelMessage RenewSubscription(string from, ulong subscriptionId, string denom = "udvpn")
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, subscriptionId);
        WriteStringField(s, 3, denom);
        return new SentinelMessage("/sentinel.subscription.v3.MsgRenewSubscriptionRequest", s.ToArray());
    }

    /// <summary>Share bandwidth with another address. Proto: sentinel.subscription.v3.MsgShareSubscriptionRequest</summary>
    public static SentinelMessage ShareSubscription(string from, ulong subscriptionId, string accAddress, long bytes)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, subscriptionId);
        WriteStringField(s, 3, accAddress);
        WriteVarintField(s, 4, (ulong)bytes);
        return new SentinelMessage("/sentinel.subscription.v3.MsgShareSubscriptionRequest", s.ToArray());
    }

    /// <summary>Update subscription renewal policy. Proto: sentinel.subscription.v3.MsgUpdateSubscriptionRequest</summary>
    public static SentinelMessage UpdateSubscription(string from, ulong subscriptionId, int renewalPricePolicy)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, subscriptionId);
        WriteVarintField(s, 3, (ulong)renewalPricePolicy);
        return new SentinelMessage("/sentinel.subscription.v3.MsgUpdateSubscriptionRequest", s.ToArray());
    }

    // ─── Session Management (v3) ────────────────────────────────────

    /// <summary>Report bandwidth usage. Proto: sentinel.session.v3.MsgUpdateSessionRequest</summary>
    public static SentinelMessage UpdateSession(string from, ulong sessionId, long downloadBytes, long uploadBytes)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, sessionId);
        WriteVarintField(s, 3, (ulong)downloadBytes);
        WriteVarintField(s, 4, (ulong)uploadBytes);
        return new SentinelMessage("/sentinel.session.v3.MsgUpdateSessionRequest", s.ToArray());
    }

    // ─── Node Operator (v3 — for operators, NOT consumer apps) ──────

    /// <summary>Register a new node. Proto: sentinel.node.v3.MsgRegisterNodeRequest</summary>
    public static SentinelMessage RegisterNode(string from, PriceEntry[] gigabytePrices, PriceEntry[] hourlyPrices, string[] remoteAddrs)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        foreach (var p in gigabytePrices) WriteEmbeddedField(s, 2, EncodePrice(p));
        foreach (var p in hourlyPrices) WriteEmbeddedField(s, 3, EncodePrice(p));
        foreach (var addr in remoteAddrs) WriteStringField(s, 4, addr);
        return new SentinelMessage("/sentinel.node.v3.MsgRegisterNodeRequest", s.ToArray());
    }

    /// <summary>Update node details. Proto: sentinel.node.v3.MsgUpdateNodeDetailsRequest</summary>
    public static SentinelMessage UpdateNodeDetails(string from, PriceEntry[] gigabytePrices, PriceEntry[] hourlyPrices, string[] remoteAddrs)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        foreach (var p in gigabytePrices) WriteEmbeddedField(s, 2, EncodePrice(p));
        foreach (var p in hourlyPrices) WriteEmbeddedField(s, 3, EncodePrice(p));
        foreach (var addr in remoteAddrs) WriteStringField(s, 4, addr);
        return new SentinelMessage("/sentinel.node.v3.MsgUpdateNodeDetailsRequest", s.ToArray());
    }

    /// <summary>Activate/deactivate node. Proto: sentinel.node.v3.MsgUpdateNodeStatusRequest</summary>
    public static SentinelMessage UpdateNodeStatus(string from, int status)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, (ulong)status);
        return new SentinelMessage("/sentinel.node.v3.MsgUpdateNodeStatusRequest", s.ToArray());
    }

    // ─── Plan Details Update (v3 — NEW) ─────────────────────────────

    /// <summary>Update plan details without recreating. Proto: sentinel.plan.v3.MsgUpdatePlanDetailsRequest</summary>
    public static SentinelMessage UpdatePlanDetails(string from, ulong planId, string? bytes = null, long? durationSeconds = null, PriceEntry[]? prices = null)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, from);
        WriteVarintField(s, 2, planId);
        if (bytes != null) WriteStringField(s, 3, bytes);
        if (durationSeconds.HasValue) WriteEmbeddedField(s, 4, EncodeDuration(durationSeconds.Value));
        if (prices != null) foreach (var p in prices) WriteEmbeddedField(s, 5, EncodePrice(p));
        return new SentinelMessage("/sentinel.plan.v3.MsgUpdatePlanDetailsRequest", s.ToArray());
    }

    // ─── Cosmos Bank ─────────────────────────────────────────────────

    /// <summary>
    /// Send tokens to an address.
    /// Proto: cosmos.bank.v1beta1.MsgSend
    /// Fields: from_address(1), to_address(2), amount(3:repeated Coin).
    /// </summary>
    /// <param name="from">Sender address (sent1...).</param>
    /// <param name="to">Recipient address (sent1...).</param>
    /// <param name="amountUdvpn">Amount in micro-denomination (udvpn).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage Send(
        string from,
        string to,
        long amountUdvpn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        if (amountUdvpn <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountUdvpn), "Must be > 0");

        using var s = new MemoryStream();

        // field 1: from_address (string)
        WriteStringField(s, 1, from);

        // field 2: to_address (string)
        WriteStringField(s, 2, to);

        // field 3: amount (repeated Coin — single entry)
        var coinBytes = EncodeCoin(Constants.Denom, amountUdvpn.ToString(CultureInfo.InvariantCulture));
        WriteEmbeddedField(s, 3, coinBytes);

        return new SentinelMessage(
            "/cosmos.bank.v1beta1.MsgSend",
            s.ToArray());
    }

    // ─── Fee Grant ───────────────────────────────────────────────────

    /// <summary>
    /// Grant fee allowance (granter pays gas for grantee).
    /// Proto: cosmos.feegrant.v1beta1.MsgGrantAllowance
    /// Fields: granter(1), grantee(2), allowance(3:Any wrapping BasicAllowance).
    /// </summary>
    /// <param name="granter">Address paying fees (sent1...).</param>
    /// <param name="grantee">Address receiving fee grant (sent1...).</param>
    /// <param name="spendLimitUdvpn">Optional maximum spend in udvpn.</param>
    /// <param name="expiration">Optional expiry date/time (UTC).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage GrantFeeAllowance(
        string granter,
        string grantee,
        long? spendLimitUdvpn = null,
        DateTime? expiration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granter);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee);

        using var s = new MemoryStream();

        // field 1: granter (string)
        WriteStringField(s, 1, granter);

        // field 2: grantee (string)
        WriteStringField(s, 2, grantee);

        // field 3: allowance (Any wrapping BasicAllowance)
        var basicAllowanceBytes = EncodeBasicAllowance(spendLimitUdvpn, expiration);
        var anyBytes = EncodeAny(
            "/cosmos.feegrant.v1beta1.BasicAllowance",
            basicAllowanceBytes);
        WriteEmbeddedField(s, 3, anyBytes);

        return new SentinelMessage(
            "/cosmos.feegrant.v1beta1.MsgGrantAllowance",
            s.ToArray());
    }

    /// <summary>
    /// Revoke a fee grant.
    /// Proto: cosmos.feegrant.v1beta1.MsgRevokeAllowance
    /// Fields: granter(1), grantee(2).
    /// </summary>
    /// <param name="granter">Address that granted fees (sent1...).</param>
    /// <param name="grantee">Address whose grant is being revoked (sent1...).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage RevokeFeeAllowance(
        string granter,
        string grantee)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granter);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee);

        using var s = new MemoryStream();

        WriteStringField(s, 1, granter);
        WriteStringField(s, 2, grantee);

        return new SentinelMessage(
            "/cosmos.feegrant.v1beta1.MsgRevokeAllowance",
            s.ToArray());
    }

    // ─── Authz (cosmos.authz.v1beta1) ─────────────────────────────────

    /// <summary>
    /// Grant authorization for a grantee to execute a specific message type on behalf of the granter.
    /// Proto: cosmos.authz.v1beta1.MsgGrant
    /// Fields: granter(1), grantee(2), grant(3:Grant).
    /// Grant contains: authorization(1:Any wrapping GenericAuthorization), expiration(2:Timestamp).
    /// </summary>
    /// <param name="granter">Address granting permission (sent1...).</param>
    /// <param name="grantee">Address receiving permission (sent1...).</param>
    /// <param name="msgTypeUrl">Message type URL to authorize (e.g. "/sentinel.node.v3.MsgStartSessionRequest").</param>
    /// <param name="expiration">Optional expiry date/time (UTC). Null = no expiry.</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage AuthzGrant(
        string granter,
        string grantee,
        string msgTypeUrl,
        DateTime? expiration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granter);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee);
        ArgumentException.ThrowIfNullOrWhiteSpace(msgTypeUrl);

        using var s = new MemoryStream();

        // field 1: granter (string)
        WriteStringField(s, 1, granter);

        // field 2: grantee (string)
        WriteStringField(s, 2, grantee);

        // field 3: grant (embedded Grant message)
        var grantBytes = EncodeGrant(msgTypeUrl, expiration);
        WriteEmbeddedField(s, 3, grantBytes);

        return new SentinelMessage(
            "/cosmos.authz.v1beta1.MsgGrant",
            s.ToArray());
    }

    /// <summary>
    /// Revoke a previously granted authorization.
    /// Proto: cosmos.authz.v1beta1.MsgRevoke
    /// Fields: granter(1), grantee(2), msg_type_url(3).
    /// </summary>
    /// <param name="granter">Address that granted permission (sent1...).</param>
    /// <param name="grantee">Address whose permission is being revoked (sent1...).</param>
    /// <param name="msgTypeUrl">Message type URL to revoke.</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage AuthzRevoke(
        string granter,
        string grantee,
        string msgTypeUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granter);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee);
        ArgumentException.ThrowIfNullOrWhiteSpace(msgTypeUrl);

        using var s = new MemoryStream();

        WriteStringField(s, 1, granter);
        WriteStringField(s, 2, grantee);
        WriteStringField(s, 3, msgTypeUrl);

        return new SentinelMessage(
            "/cosmos.authz.v1beta1.MsgRevoke",
            s.ToArray());
    }

    /// <summary>
    /// Execute messages on behalf of a granter using a previously granted authorization.
    /// Proto: cosmos.authz.v1beta1.MsgExec
    /// Fields: grantee(1), msgs(2:repeated Any).
    /// </summary>
    /// <param name="grantee">Address executing on behalf of granter (sent1...).</param>
    /// <param name="innerMsgs">Pre-built messages to execute (wrapped as Any).</param>
    /// <returns>Encoded message for broadcast.</returns>
    public static SentinelMessage AuthzExec(
        string grantee,
        SentinelMessage[] innerMsgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee);
        ArgumentNullException.ThrowIfNull(innerMsgs);
        if (innerMsgs.Length == 0)
            throw new ArgumentException("At least one inner message is required.", nameof(innerMsgs));

        using var s = new MemoryStream();

        // field 1: grantee (string)
        WriteStringField(s, 1, grantee);

        // field 2: msgs (repeated Any)
        foreach (var msg in innerMsgs)
        {
            var anyBytes = EncodeAny(msg.TypeUrl, msg.Value);
            WriteEmbeddedField(s, 2, anyBytes);
        }

        return new SentinelMessage(
            "/cosmos.authz.v1beta1.MsgExec",
            s.ToArray());
    }

    // ─── Protobuf Sub-Message Encoders ───────────────────────────────

    /// <summary>
    /// Encode a <see cref="PriceEntry"/> as sentinel.types.v1.Price.
    /// Fields: denom(1:string), base_value(2:string), quote_value(3:string).
    /// CRITICAL: base_value is sdk.Dec on chain — must be scaled by 10^18 before encoding.
    /// The chain stores Dec values as big integers with 18 decimal places of precision.
    /// Example: "0.003000000000000000" → "3000000000000000"
    /// Example: "40152030" → "40152030000000000000000000"
    /// </summary>
    private static byte[] EncodePrice(PriceEntry price)
    {
        using var s = new MemoryStream();

        WriteStringField(s, 1, price.Denom);
        WriteStringField(s, 2, DecToScaledInt(price.BaseValue));
        WriteStringField(s, 3, price.QuoteValue);

        return s.ToArray();
    }

    /// <summary>
    /// Convert an sdk.Dec string to its scaled big integer representation (multiply by 10^18).
    /// The Cosmos SDK stores Dec values as integers with 18 decimal places of precision.
    /// This matches the JS SDK's decToScaledInt() function exactly.
    /// </summary>
    /// <param name="decStr">Decimal string from the chain (e.g. "0.003000000000000000" or "40152030").</param>
    /// <returns>Scaled integer string (e.g. "3000000000000000" or "40152030000000000000000000").</returns>
    public static string DecToScaledInt(string decStr)
    {
        if (string.IsNullOrWhiteSpace(decStr))
            return "0";

        var s = decStr.Trim();
        if (s == "undefined" || s == "null" || s.Length == 0)
            return "0";

        var dotIdx = s.IndexOf('.');
        if (dotIdx == -1)
        {
            // Integer — multiply by 10^18
            return s + new string('0', 18);
        }

        var intPart = s[..dotIdx];
        var fracPart = s[(dotIdx + 1)..];

        // Pad or trim fractional part to exactly 18 digits
        var frac18 = fracPart.Length >= 18
            ? fracPart[..18]
            : fracPart + new string('0', 18 - fracPart.Length);

        var combined = (string.IsNullOrEmpty(intPart) || intPart == "0")
            ? frac18
            : intPart + frac18;

        // Remove leading zeros (but keep at least one digit)
        var trimmed = combined.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>
    /// Encode a google.protobuf.Duration.
    /// Fields: seconds(1:int64), nanos(2:int32).
    /// </summary>
    private static byte[] EncodeDuration(long seconds, int nanos = 0)
    {
        using var s = new MemoryStream();

        if (seconds != 0)
        {
            WriteVarintField(s, 1, (ulong)seconds);
        }

        if (nanos != 0)
        {
            WriteVarintField(s, 2, (ulong)nanos);
        }

        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.base.v1beta1.Coin.
    /// Fields: denom(1:string), amount(2:string).
    /// </summary>
    private static byte[] EncodeCoin(string denom, string amount)
    {
        using var s = new MemoryStream();

        WriteStringField(s, 1, denom);
        WriteStringField(s, 2, amount);

        return s.ToArray();
    }

    /// <summary>
    /// Encode a google.protobuf.Any.
    /// Fields: type_url(1:string), value(2:bytes).
    /// </summary>
    private static byte[] EncodeAny(string typeUrl, byte[] value)
    {
        using var s = new MemoryStream();

        WriteStringField(s, 1, typeUrl);
        WriteBytesField(s, 2, value);

        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.feegrant.v1beta1.BasicAllowance.
    /// Fields: spend_limit(1:repeated Coin), expiration(2:Timestamp).
    /// </summary>
    private static byte[] EncodeBasicAllowance(long? spendLimitUdvpn, DateTime? expiration)
    {
        using var s = new MemoryStream();

        // field 1: spend_limit (repeated Coin — single entry if provided)
        if (spendLimitUdvpn.HasValue)
        {
            var coinBytes = EncodeCoin(Constants.Denom, spendLimitUdvpn.Value.ToString(CultureInfo.InvariantCulture));
            WriteEmbeddedField(s, 1, coinBytes);
        }

        // field 2: expiration (google.protobuf.Timestamp)
        if (expiration.HasValue)
        {
            var timestampBytes = EncodeTimestamp(expiration.Value);
            WriteEmbeddedField(s, 2, timestampBytes);
        }

        return s.ToArray();
    }

    /// <summary>
    /// Encode a google.protobuf.Timestamp.
    /// Fields: seconds(1:int64), nanos(2:int32).
    /// Converts UTC DateTime to Unix epoch seconds.
    /// </summary>
    private static byte[] EncodeTimestamp(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seconds = (long)(utc - epoch).TotalSeconds;

        using var s = new MemoryStream();

        if (seconds != 0)
        {
            WriteVarintField(s, 1, (ulong)seconds);
        }

        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.authz.v1beta1.GenericAuthorization.
    /// Fields: msg(1:string).
    /// </summary>
    private static byte[] EncodeGenericAuthorization(string msgTypeUrl)
    {
        using var s = new MemoryStream();
        WriteStringField(s, 1, msgTypeUrl);
        return s.ToArray();
    }

    /// <summary>
    /// Encode a cosmos.authz.v1beta1.Grant.
    /// Fields: authorization(1:Any wrapping GenericAuthorization), expiration(2:Timestamp).
    /// </summary>
    private static byte[] EncodeGrant(string msgTypeUrl, DateTime? expiration)
    {
        using var s = new MemoryStream();

        // field 1: authorization (Any wrapping GenericAuthorization)
        var authBytes = EncodeGenericAuthorization(msgTypeUrl);
        var anyBytes = EncodeAny(
            "/cosmos.authz.v1beta1.GenericAuthorization",
            authBytes);
        WriteEmbeddedField(s, 1, anyBytes);

        // field 2: expiration (google.protobuf.Timestamp, optional)
        if (expiration.HasValue)
        {
            var timestampBytes = EncodeTimestamp(expiration.Value);
            WriteEmbeddedField(s, 2, timestampBytes);
        }

        return s.ToArray();
    }

}
