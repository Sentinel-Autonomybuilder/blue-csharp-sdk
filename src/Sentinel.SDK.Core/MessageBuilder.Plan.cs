namespace Sentinel.SDK.Core;

using static ProtobufWriter;

// ─── Plan Messages ───

public static partial class MessageBuilder
{
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
}
