using System.Globalization;

namespace Sentinel.SDK.Core;

using static ProtobufWriter;

// ─── Bank, Fee Grant, Authz, and Lease Messages ───

public static partial class MessageBuilder
{
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
}
