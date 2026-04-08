namespace Sentinel.SDK.Core;

using static ProtobufWriter;

// ─── Session Messages ───

public static partial class MessageBuilder
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
}
