using System.Text.Json;

namespace Sentinel.SDK.Core;

/// <summary>
/// Typed event parsers for Sentinel chain transaction events.
/// Replaces string-matching with structured parsers that guarantee field access.
/// Matches the JS SDK's protocol/events.js pattern.
/// </summary>
public static class EventParser
{
    // ─── Event Type Constants ──────────────────────────────────────

    /// <summary>Sentinel chain event type URLs.</summary>
    public static class EventTypes
    {
        public const string NodeCreateSession = "sentinel.node.v3.EventCreateSession";
        public const string NodePay = "sentinel.node.v3.EventPay";
        public const string NodeRefund = "sentinel.node.v3.EventRefund";
        public const string NodeUpdateStatus = "sentinel.node.v3.EventUpdateStatus";
        public const string SessionEnd = "sentinel.session.v3.EventEnd";
        public const string SessionUpdateDetails = "sentinel.session.v3.EventUpdateDetails";
        public const string SubscriptionCreate = "sentinel.subscription.v3.EventCreate";
        public const string SubscriptionCreateSession = "sentinel.subscription.v3.EventCreateSession";
        public const string SubscriptionPay = "sentinel.subscription.v3.EventPay";
        public const string SubscriptionEnd = "sentinel.subscription.v3.EventEnd";
        public const string LeaseCreate = "sentinel.lease.v1.EventCreate";
        public const string LeaseEnd = "sentinel.lease.v1.EventEnd";
    }

    // ─── Parsed Event Records ──────────────────────────────────────

    /// <summary>Parsed sentinel.node.v3.EventCreateSession.</summary>
    public record SessionCreatedEvent(
        long SessionId,
        string AccAddress,
        string NodeAddress,
        string MaxBytes,
        string MaxDuration
    );

    /// <summary>Parsed sentinel.node.v3.EventPay.</summary>
    public record SessionPayEvent(
        long SessionId,
        string AccAddress,
        string NodeAddress,
        string Payment,
        string StakingReward
    );

    /// <summary>Parsed sentinel.session.v3.EventEnd.</summary>
    public record SessionEndEvent(
        long SessionId,
        string AccAddress,
        string NodeAddress
    );

    /// <summary>Parsed sentinel.subscription.v3.EventCreateSession.</summary>
    public record SubscriptionSessionEvent(
        long SessionId,
        long SubscriptionId,
        string AccAddress,
        string NodeAddress
    );

    /// <summary>Parsed sentinel.subscription.v3.EventCreate.</summary>
    public record SubscriptionCreatedEvent(
        long SubscriptionId,
        long PlanId,
        string AccAddress
    );

    /// <summary>Parsed sentinel.lease.v1.EventCreate.</summary>
    public record LeaseCreatedEvent(
        long LeaseId,
        string NodeAddress,
        string ProvAddress,
        int MaxHours
    );

    /// <summary>Parsed sentinel.node.v3.EventRefund.</summary>
    public record SessionRefundEvent(long SessionId, string AccAddress, string Amount);

    /// <summary>Parsed sentinel.node.v3.EventUpdateStatus.</summary>
    public record NodeStatusEvent(string Address, int Status);

    /// <summary>Parsed sentinel.session.v3.EventUpdateDetails (bandwidth report).</summary>
    public record SessionDetailsEvent(long SessionId, string AccAddress, string NodeAddress,
        string DownloadBytes, string UploadBytes, string Duration);

    /// <summary>Parsed sentinel.subscription.v3.EventPay.</summary>
    public record SubscriptionPayEvent(long SubscriptionId, long PlanId, string AccAddress,
        string ProvAddress, string Payment, string StakingReward);

    /// <summary>Parsed sentinel.subscription.v3.EventEnd.</summary>
    public record SubscriptionEndEvent(long SubscriptionId, long PlanId, string AccAddress);

    /// <summary>Parsed sentinel.lease.v1.EventEnd.</summary>
    public record LeaseEndEvent(long LeaseId, string NodeAddress, string ProvAddress);

    // ─── Parsing Methods ───────────────────────────────────────────

    /// <summary>
    /// Extract session ID from a transaction result's event log.
    /// Searches both node.v3.EventCreateSession and subscription.v3.EventCreateSession.
    /// </summary>
    /// <param name="txEventsJson">The raw events JSON from a broadcast TX result.</param>
    /// <returns>Session ID, or null if not found.</returns>
    public static long? ExtractSessionId(string? txEventsJson)
    {
        if (string.IsNullOrEmpty(txEventsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(txEventsJson);
            var root = doc.RootElement;

            // Handle both array of events and nested log formats
            var events = root.ValueKind == JsonValueKind.Array ? root : default;

            if (events.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in events.EnumerateArray())
                {
                    var sessionId = TryExtractSessionIdFromEvent(ev);
                    if (sessionId.HasValue) return sessionId;
                }
            }
        }
        catch
        {
            // Fallback: try to find session_id pattern in raw string
            return ExtractSessionIdFromRaw(txEventsJson);
        }

        return ExtractSessionIdFromRaw(txEventsJson);
    }

    /// <summary>
    /// Parse a specific event type from TX result events JSON.
    /// </summary>
    /// <param name="txEventsJson">Raw events JSON.</param>
    /// <param name="eventType">Event type URL to search for (use EventTypes constants).</param>
    /// <returns>Dictionary of attribute key→value pairs, or null if event not found.</returns>
    public static Dictionary<string, string>? FindEvent(string? txEventsJson, string eventType)
    {
        if (string.IsNullOrEmpty(txEventsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(txEventsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                if (!ev.TryGetProperty("type", out var typeProp)) continue;
                if (typeProp.GetString() != eventType) continue;

                var attrs = new Dictionary<string, string>();
                if (ev.TryGetProperty("attributes", out var attrArray) && attrArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var attr in attrArray.EnumerateArray())
                    {
                        var key = DecodeAttr(attr, "key");
                        var value = DecodeAttr(attr, "value");
                        if (key != null) attrs[key] = value ?? "";
                    }
                }
                return attrs;
            }
        }
        catch { }

        return null;
    }

    /// <summary>Parse a SessionCreatedEvent from TX result events.</summary>
    public static SessionCreatedEvent? ParseSessionCreated(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.NodeCreateSession);
        if (attrs is null) return null;

        return new SessionCreatedEvent(
            SessionId: ParseLong(attrs.GetValueOrDefault("session_id") ?? attrs.GetValueOrDefault("id")),
            AccAddress: attrs.GetValueOrDefault("acc_address") ?? attrs.GetValueOrDefault("address") ?? "",
            NodeAddress: attrs.GetValueOrDefault("node_address") ?? "",
            MaxBytes: attrs.GetValueOrDefault("max_bytes") ?? "0",
            MaxDuration: attrs.GetValueOrDefault("max_duration") ?? "0"
        );
    }

    /// <summary>Parse a SessionPayEvent from TX result events.</summary>
    public static SessionPayEvent? ParseSessionPay(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.NodePay);
        if (attrs is null) return null;

        return new SessionPayEvent(
            SessionId: ParseLong(attrs.GetValueOrDefault("session_id")),
            AccAddress: attrs.GetValueOrDefault("acc_address") ?? "",
            NodeAddress: attrs.GetValueOrDefault("node_address") ?? "",
            Payment: attrs.GetValueOrDefault("payment") ?? "0",
            StakingReward: attrs.GetValueOrDefault("staking_reward") ?? "0"
        );
    }

    /// <summary>Parse a SubscriptionSessionEvent from TX result events.</summary>
    public static SubscriptionSessionEvent? ParseSubscriptionSession(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.SubscriptionCreateSession);
        if (attrs is null) return null;

        return new SubscriptionSessionEvent(
            SessionId: ParseLong(attrs.GetValueOrDefault("session_id") ?? attrs.GetValueOrDefault("id")),
            SubscriptionId: ParseLong(attrs.GetValueOrDefault("subscription_id")),
            AccAddress: attrs.GetValueOrDefault("acc_address") ?? "",
            NodeAddress: attrs.GetValueOrDefault("node_address") ?? ""
        );
    }

    /// <summary>Parse a SessionRefundEvent from TX result events.</summary>
    public static SessionRefundEvent? ParseSessionRefund(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.NodeRefund);
        if (attrs is null) return null;
        return new SessionRefundEvent(
            SessionId: ParseLong(attrs.GetValueOrDefault("session_id")),
            AccAddress: attrs.GetValueOrDefault("acc_address") ?? "",
            Amount: attrs.GetValueOrDefault("amount") ?? attrs.GetValueOrDefault("value") ?? "0"
        );
    }

    /// <summary>Parse a NodeStatusEvent from TX result events.</summary>
    public static NodeStatusEvent? ParseNodeStatus(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.NodeUpdateStatus);
        if (attrs is null) return null;
        return new NodeStatusEvent(
            Address: attrs.GetValueOrDefault("address") ?? "",
            Status: int.TryParse(attrs.GetValueOrDefault("status"), out var s) ? s : 0
        );
    }

    /// <summary>Parse a SessionDetailsEvent (bandwidth update) from TX result events.</summary>
    public static SessionDetailsEvent? ParseSessionDetails(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.SessionUpdateDetails);
        if (attrs is null) return null;
        return new SessionDetailsEvent(
            SessionId: ParseLong(attrs.GetValueOrDefault("session_id") ?? attrs.GetValueOrDefault("id")),
            AccAddress: attrs.GetValueOrDefault("acc_address") ?? "",
            NodeAddress: attrs.GetValueOrDefault("node_address") ?? "",
            DownloadBytes: attrs.GetValueOrDefault("download_bytes") ?? "0",
            UploadBytes: attrs.GetValueOrDefault("upload_bytes") ?? "0",
            Duration: attrs.GetValueOrDefault("duration") ?? "0"
        );
    }

    /// <summary>Parse a SubscriptionPayEvent from TX result events.</summary>
    public static SubscriptionPayEvent? ParseSubscriptionPay(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.SubscriptionPay);
        if (attrs is null) return null;
        return new SubscriptionPayEvent(
            SubscriptionId: ParseLong(attrs.GetValueOrDefault("subscription_id") ?? attrs.GetValueOrDefault("id")),
            PlanId: ParseLong(attrs.GetValueOrDefault("plan_id")),
            AccAddress: attrs.GetValueOrDefault("acc_address") ?? "",
            ProvAddress: attrs.GetValueOrDefault("prov_address") ?? "",
            Payment: attrs.GetValueOrDefault("payment") ?? "0",
            StakingReward: attrs.GetValueOrDefault("staking_reward") ?? "0"
        );
    }

    /// <summary>Parse a SubscriptionEndEvent from TX result events.</summary>
    public static SubscriptionEndEvent? ParseSubscriptionEnd(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.SubscriptionEnd);
        if (attrs is null) return null;
        return new SubscriptionEndEvent(
            SubscriptionId: ParseLong(attrs.GetValueOrDefault("subscription_id") ?? attrs.GetValueOrDefault("id")),
            PlanId: ParseLong(attrs.GetValueOrDefault("plan_id")),
            AccAddress: attrs.GetValueOrDefault("acc_address") ?? ""
        );
    }

    /// <summary>Parse a LeaseEndEvent from TX result events.</summary>
    public static LeaseEndEvent? ParseLeaseEnd(string? txEventsJson)
    {
        var attrs = FindEvent(txEventsJson, EventTypes.LeaseEnd);
        if (attrs is null) return null;
        return new LeaseEndEvent(
            LeaseId: ParseLong(attrs.GetValueOrDefault("lease_id") ?? attrs.GetValueOrDefault("id")),
            NodeAddress: attrs.GetValueOrDefault("node_address") ?? "",
            ProvAddress: attrs.GetValueOrDefault("prov_address") ?? ""
        );
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private static long? TryExtractSessionIdFromEvent(JsonElement ev)
    {
        if (!ev.TryGetProperty("type", out var typeProp)) return null;
        var type = typeProp.GetString();
        if (type is null) return null;

        if (!type.Contains("Session", StringComparison.OrdinalIgnoreCase)) return null;

        if (!ev.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array) return null;

        foreach (var attr in attrs.EnumerateArray())
        {
            var key = DecodeAttr(attr, "key");
            if (key is "session_id" or "SessionID" or "id")
            {
                var val = DecodeAttr(attr, "value");
                if (val != null && long.TryParse(val.Trim('"'), out var id) && id > 0) return id;
            }
        }

        return null;
    }

    private static long? ExtractSessionIdFromRaw(string raw)
    {
        // Fallback: regex-like search for session_id in raw JSON string
        var patterns = new[] { "\"session_id\":\"", "\"session_id\": \"", "\"SessionID\":\"" };
        foreach (var pattern in patterns)
        {
            var idx = raw.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) continue;
            var start = idx + pattern.Length;
            var end = raw.IndexOf('"', start);
            if (end <= start) continue;
            var val = raw[start..end];
            if (long.TryParse(val, out var id) && id > 0) return id;
        }

        return null;
    }

    private static string? DecodeAttr(JsonElement attr, string prop)
    {
        if (!attr.TryGetProperty(prop, out var value)) return null;
        var str = value.GetString();
        if (str is null) return null;

        // Try base64 decode (older CosmJS format)
        str = str.Trim('"');
        try
        {
            var bytes = Convert.FromBase64String(str);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            // If decoded looks like a readable string, use it
            if (decoded.All(c => c >= 32 && c < 127)) return decoded.Trim('"');
        }
        catch { }

        return str;
    }

    private static long ParseLong(string? val)
    {
        if (string.IsNullOrEmpty(val)) return 0;
        return long.TryParse(val.Trim('"'), out var v) ? v : 0;
    }
}
