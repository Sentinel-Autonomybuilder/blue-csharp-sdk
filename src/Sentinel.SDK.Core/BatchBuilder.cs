using System.Text.Json;

namespace Sentinel.SDK.Core;

/// <summary>
/// Convenience builders for common batch operations.
/// Each method returns an array of <see cref="SentinelMessage"/> objects
/// ready for <see cref="TransactionBuilder.BroadcastAsync"/>.
/// </summary>
public static class BatchBuilder
{
    /// <summary>
    /// Build batch MsgStartSession messages for multiple nodes.
    /// Each entry becomes a separate MsgStartSessionRequest in the batch TX.
    /// </summary>
    /// <param name="from">Account address (sent1...).</param>
    /// <param name="nodes">Array of (NodeAddress, Gigabytes, MaxPrice) tuples.</param>
    /// <returns>Array of encoded messages for broadcast.</returns>
    public static SentinelMessage[] StartSessions(
        string from,
        (string NodeAddress, int Gigabytes, PriceEntry MaxPrice)[] nodes)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Length == 0)
        {
            throw new SentinelException("BATCH_EMPTY", "At least one node is required for batch start sessions.");
        }

        var messages = new SentinelMessage[nodes.Length];

        for (var i = 0; i < nodes.Length; i++)
        {
            var (nodeAddress, gigabytes, maxPrice) = nodes[i];
            messages[i] = MessageBuilder.StartSession(
                from,
                nodeAddress,
                gigabytes > 0 ? gigabytes : 1,
                maxPrice);
        }

        return messages;
    }

    /// <summary>
    /// Build batch MsgSend messages for token distribution.
    /// Each recipient gets a separate MsgSend in the batch TX.
    /// </summary>
    /// <param name="from">Sender address (sent1...).</param>
    /// <param name="recipients">Array of (Address, AmountUdvpn) tuples.</param>
    /// <returns>Array of encoded messages for broadcast.</returns>
    public static SentinelMessage[] SendBatch(
        string from,
        (string Address, long AmountUdvpn)[] recipients)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(recipients);

        if (recipients.Length == 0)
        {
            throw new SentinelException("BATCH_EMPTY", "At least one recipient is required for batch send.");
        }

        var messages = new SentinelMessage[recipients.Length];

        for (var i = 0; i < recipients.Length; i++)
        {
            var (address, amountUdvpn) = recipients[i];
            messages[i] = MessageBuilder.Send(from, address, amountUdvpn);
        }

        return messages;
    }

    /// <summary>
    /// Build batch MsgLinkNode messages for linking multiple nodes to a plan.
    /// Each node address becomes a separate MsgLinkNodeRequest.
    /// </summary>
    /// <param name="provAddress">Provider address (sentprov1...).</param>
    /// <param name="planId">Plan ID on chain.</param>
    /// <param name="nodeAddresses">Node addresses to link (sentnode1...).</param>
    /// <returns>Array of encoded messages for broadcast.</returns>
    public static SentinelMessage[] LinkNodes(
        string provAddress,
        ulong planId,
        string[] nodeAddresses)
    {
        ArgumentNullException.ThrowIfNull(provAddress);
        ArgumentNullException.ThrowIfNull(nodeAddresses);

        if (nodeAddresses.Length == 0)
        {
            throw new SentinelException("BATCH_EMPTY", "At least one node address is required for batch link.");
        }

        var messages = new SentinelMessage[nodeAddresses.Length];

        for (var i = 0; i < nodeAddresses.Length; i++)
        {
            messages[i] = MessageBuilder.LinkNode(provAddress, planId, nodeAddresses[i]);
        }

        return messages;
    }

    /// <summary>
    /// Extract ALL session IDs from a batch TX result containing multiple MsgStartSession responses.
    /// Queries the transaction by hash and parses session IDs from the event log.
    /// </summary>
    /// <param name="txResult">The broadcast transaction result.</param>
    /// <param name="client">Chain client used to query the full TX events.</param>
    /// <returns>Array of extracted session IDs (deduplicated).</returns>
    public static ulong[] ExtractAllSessionIds(TxResult txResult, ChainClient client)
    {
        ArgumentNullException.ThrowIfNull(txResult);
        ArgumentNullException.ThrowIfNull(client);

        // The RawLog may contain session IDs in the event attributes.
        // Parse the raw log JSON to extract session_id values from events.
        var ids = new List<ulong>();
        var seen = new HashSet<ulong>();

        if (string.IsNullOrEmpty(txResult.RawLog))
        {
            return Array.Empty<ulong>();
        }

        try
        {
            // RawLog for batch TXs is a JSON array of message results, each with events
            var doc = JsonDocument.Parse(txResult.RawLog);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var msgResult in doc.RootElement.EnumerateArray())
                {
                    ExtractSessionIdsFromEvents(msgResult, ids, seen);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                ExtractSessionIdsFromEvents(doc.RootElement, ids, seen);
            }
        }
        catch
        {
            // RawLog might not be valid JSON (e.g. error messages).
            // Try to extract numeric IDs from common patterns.
            ExtractSessionIdsFromText(txResult.RawLog, ids, seen);
        }

        return ids.ToArray();
    }

    // ─── Internal: Session ID Extraction ───

    /// <summary>
    /// Extract session IDs from a JSON element containing events.
    /// Looks for event types containing "session" with attributes named
    /// "session_id", "SessionID", or "id".
    /// </summary>
    private static void ExtractSessionIdsFromEvents(
        JsonElement element,
        List<ulong> ids,
        HashSet<ulong> seen)
    {
        if (!element.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var evt in events.EnumerateArray())
        {
            var evtType = evt.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            if (!evtType.Contains("session", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!evt.TryGetProperty("attributes", out var attrs) ||
                attrs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var attr in attrs.EnumerateArray())
            {
                var key = attr.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";

                if (key is not ("session_id" or "SessionID" or "id"))
                {
                    continue;
                }

                var value = attr.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                // Strip surrounding quotes if present (from base64-decoded values)
                value = value.Trim('"');

                if (ulong.TryParse(value, out var id) && id > 0 && seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }
    }

    /// <summary>
    /// Fallback: extract session IDs from raw text using pattern matching.
    /// Looks for patterns like session_id: "123" or session_id:123.
    /// </summary>
    private static void ExtractSessionIdsFromText(
        string text,
        List<ulong> ids,
        HashSet<ulong> seen)
    {
        // Simple regex-free pattern matching for session_id values
        var searchPatterns = new[] { "session_id", "SessionID" };

        foreach (var pattern in searchPatterns)
        {
            var idx = 0;
            while ((idx = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                idx += pattern.Length;

                // Skip delimiters: colon, quotes, spaces
                while (idx < text.Length && (text[idx] == ':' || text[idx] == '"' || text[idx] == ' ' || text[idx] == '\\'))
                {
                    idx++;
                }

                // Extract numeric value
                var start = idx;
                while (idx < text.Length && char.IsDigit(text[idx]))
                {
                    idx++;
                }

                if (idx > start)
                {
                    var numStr = text[start..idx];
                    if (ulong.TryParse(numStr, out var id) && id > 0 && seen.Add(id))
                    {
                        ids.Add(id);
                    }
                }
            }
        }
    }
}
