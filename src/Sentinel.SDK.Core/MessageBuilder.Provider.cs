namespace Sentinel.SDK.Core;

using static ProtobufWriter;

// ─── Provider Messages ───

public static partial class MessageBuilder
{
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
}
