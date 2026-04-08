namespace Sentinel.SDK.Core;

using static ProtobufWriter;

// ─── Subscription Messages ───

public static partial class MessageBuilder
{
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
}
