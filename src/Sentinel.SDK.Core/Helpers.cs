using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sentinel.SDK.Core;

// ─── Display Helpers ───

/// <summary>
/// Utility methods for formatting Sentinel chain data into human-readable strings.
/// </summary>
public static class Helpers
{
    /// <summary>
    /// Format a micro-denomination amount as a P2P display string.
    /// </summary>
    /// <param name="udvpn">Amount in udvpn (1 P2P = 1,000,000 udvpn).</param>
    /// <param name="decimals">Number of decimal places (default 2).</param>
    /// <returns>Formatted string like "1.23 P2P".</returns>
    public static string FormatP2P(long udvpn, int decimals = 2) =>
        $"{(udvpn / 1_000_000m).ToString($"F{decimals}", CultureInfo.InvariantCulture)} P2P";

    /// <summary>
    /// Truncate a bech32 address for display (e.g. "sent12e03w...fjhzg").
    /// </summary>
    /// <param name="addr">Full bech32 address.</param>
    /// <param name="prefix">Number of characters to show at the start.</param>
    /// <param name="suffix">Number of characters to show at the end.</param>
    /// <returns>Truncated address string.</returns>
    public static string ShortAddress(string addr, int prefix = 12, int suffix = 6)
    {
        if (string.IsNullOrEmpty(addr))
            return "";

        if (addr.Length <= prefix + suffix + 3)
            return addr;

        return $"{addr[..prefix]}...{addr[^suffix..]}";
    }

    /// <summary>
    /// Format a byte count into a human-readable string (e.g. "1.5 GB", "340 MB").
    /// </summary>
    /// <param name="bytes">Number of bytes.</param>
    /// <returns>Formatted string with appropriate unit.</returns>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;

        return bytes switch
        {
            >= 1_099_511_627_776L => $"{bytes / 1_099_511_627_776.0:F1} TB",
            >= 1_073_741_824L => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576L => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024L => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }

    /// <summary>
    /// Format an ISO timestamp into a relative expiry string (e.g. "23d left", "expired").
    /// </summary>
    /// <param name="isoTimestamp">ISO 8601 timestamp string.</param>
    /// <returns>Relative time string.</returns>
    public static string FormatExpiry(string isoTimestamp)
    {
        if (string.IsNullOrEmpty(isoTimestamp))
            return "unknown";

        if (!DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
            return "unknown";

        var remaining = expiry - DateTimeOffset.UtcNow;

        if (remaining.TotalSeconds <= 0)
            return "expired";

        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d left";

        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h left";

        return $"{(int)remaining.TotalMinutes}m left";
    }

    /// <summary>
    /// Format a TimeSpan as a compact uptime string (e.g. "2h 15m").
    /// </summary>
    /// <param name="uptime">Duration to format.</param>
    /// <returns>Compact duration string.</returns>
    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h";

        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";

        return $"{(int)uptime.TotalMinutes}m";
    }

    // ─── Serialization ───

    /// <summary>
    /// Serialize an object for JSON APIs, converting ulong/long session IDs to strings.
    /// Prevents the BigInt serialization crash that can occur with large numeric IDs.
    /// Equivalent to the JS SDK's <c>serializeResult()</c>.
    /// </summary>
    /// <param name="result">Object to serialize (typically a ConnectionResult or similar).</param>
    /// <returns>JSON-safe dictionary with numeric IDs converted to strings, or the original value if not an object.</returns>
    /// <example>
    ///   var conn = await client.ConnectAsync(nodeAddr);
    ///   var safe = Helpers.SerializeResult(conn);
    ///   return JsonSerializer.Serialize(safe); // Safe for API responses
    /// </example>
    public static object? SerializeResult(object? result)
    {
        if (result is null)
            return null;

        var type = result.GetType();

        // Primitives — pass through
        if (type.IsPrimitive || result is string || result is decimal)
            return result;

        // Serialize to JSON and re-parse, converting large numbers to strings
        var json = JsonSerializer.Serialize(result);
        using var doc = JsonDocument.Parse(json);

        return ConvertElement(doc.RootElement);
    }

    /// <summary>
    /// Recursively convert JsonElement, turning large integer values to strings
    /// to prevent BigInt-style serialization issues in downstream consumers.
    /// </summary>
    private static object? ConvertElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    dict[prop.Name] = ConvertElement(prop.Value);
                }
                return dict;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ConvertElement(item));
                }
                return list;

            case JsonValueKind.Number:
                // Convert large integers (session IDs, etc.) to strings
                if (element.TryGetInt64(out var longVal))
                {
                    return longVal > int.MaxValue || longVal < int.MinValue
                        ? longVal.ToString(CultureInfo.InvariantCulture)
                        : (object)longVal;
                }
                if (element.TryGetUInt64(out var ulongVal))
                {
                    return ulongVal.ToString(CultureInfo.InvariantCulture);
                }
                if (element.TryGetDouble(out var dblVal))
                {
                    return dblVal;
                }
                return element.GetRawText();

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            default:
                return null;
        }
    }

    // ─── Chain Duration Parsing ───

    /// <summary>
    /// Parse a Sentinel chain duration string like "557817.72s" into structured data.
    /// </summary>
    /// <param name="durationStr">Duration string from the chain (e.g. "557817.72s").</param>
    /// <returns>Tuple with total seconds, hours, minutes, and a formatted display string.</returns>
    public static (double Seconds, int Hours, int Minutes, string Formatted) ParseChainDuration(string durationStr)
    {
        if (string.IsNullOrEmpty(durationStr))
            return (0, 0, 0, "0m");

        // Strip trailing 's' if present
        var cleaned = durationStr.TrimEnd('s', 'S');

        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var totalSeconds))
            return (0, 0, 0, "0m");

        var span = TimeSpan.FromSeconds(totalSeconds);
        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;

        var formatted = hours > 0
            ? $"{hours}h {minutes}m"
            : $"{minutes}m";

        return (totalSeconds, hours, minutes, formatted);
    }

    // ─── Node Filtering ───

    /// <summary>
    /// Filter nodes by country (from remote URL hostname), service type, and max price.
    /// </summary>
    /// <param name="nodes">Source collection of chain nodes.</param>
    /// <param name="country">Two-letter country code to match in remote URL (case-insensitive), or null to skip.</param>
    /// <param name="serviceType">Service type filter: "wireguard" or "v2ray" (case-insensitive), or null to skip.</param>
    /// <param name="maxPriceUdvpn">Maximum per-GB price in udvpn, or null to skip.</param>
    /// <returns>Filtered list of nodes matching all specified criteria.</returns>
    public static List<ChainNode> FilterNodes(
        IEnumerable<ChainNode> nodes,
        string? country = null,
        string? serviceType = null,
        long? maxPriceUdvpn = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        IEnumerable<ChainNode> result = nodes;

        if (!string.IsNullOrEmpty(country))
        {
            var cc = country.ToUpperInvariant();
            result = result.Where(n =>
                n.RemoteUrl != null &&
                n.RemoteUrl.Contains(cc, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(serviceType))
        {
            var st = serviceType.ToLowerInvariant();
            result = st switch
            {
                "wireguard" or "wg" => result.Where(n =>
                    n.RemoteUrl != null && n.RemoteUrl.Contains(":8585", StringComparison.Ordinal)),
                "v2ray" => result.Where(n =>
                    n.RemoteUrl != null && !n.RemoteUrl.Contains(":8585", StringComparison.Ordinal)),
                _ => result,
            };
        }

        if (maxPriceUdvpn.HasValue)
        {
            var maxPrice = maxPriceUdvpn.Value;
            result = result.Where(n =>
            {
                var gbPrice = n.GigabytePrices
                    .FirstOrDefault(p => p.Denom == Constants.Denom);
                if (gbPrice == null) return false;

                if (long.TryParse(
                        gbPrice.BaseValue.Split('.')[0],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var priceVal))
                {
                    return priceVal <= maxPrice;
                }
                return false;
            });
        }

        return result.ToList();
    }

    // ─── Port Check ───

    /// <summary>
    /// Check if a TCP port is free for use on localhost.
    /// </summary>
    /// <param name="port">TCP port number to check.</param>
    /// <returns>True if the port is available, false if already in use.</returns>
    public static async Task<bool> CheckPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            await Task.CompletedTask;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // ─── Chain Error Parsing ───

    /// <summary>
    /// Parse a chain error raw log into a user-friendly message.
    /// Maps common Sentinel/Cosmos error patterns to readable strings.
    /// </summary>
    /// <param name="rawLog">Raw log string from a failed transaction.</param>
    /// <returns>User-friendly error message.</returns>
    public static string ParseChainError(string rawLog)
    {
        if (string.IsNullOrEmpty(rawLog))
            return "Transaction failed with no error details.";

        // Sequence mismatch
        if (rawLog.Contains("account sequence mismatch", StringComparison.OrdinalIgnoreCase))
            return "Account sequence mismatch. Retry the transaction.";

        // Insufficient funds
        if (rawLog.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase))
            return "Insufficient P2P balance to complete this transaction.";

        // Out of gas
        if (rawLog.Contains("out of gas", StringComparison.OrdinalIgnoreCase))
            return "Transaction ran out of gas. Increase the gas limit.";

        // Node not found
        if (rawLog.Contains("node not found", StringComparison.OrdinalIgnoreCase))
            return "Node not found on chain. It may have been deregistered.";

        // Session already active
        if (rawLog.Contains("active session already exists", StringComparison.OrdinalIgnoreCase))
            return "An active session already exists for this node. End it first.";

        // Subscription not found
        if (rawLog.Contains("subscription not found", StringComparison.OrdinalIgnoreCase))
            return "Subscription not found. It may have expired.";

        // Plan not found
        if (rawLog.Contains("plan not found", StringComparison.OrdinalIgnoreCase))
            return "Plan not found on chain.";

        // Unauthorized
        if (rawLog.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return "Unauthorized. Check your permissions or authz grants.";

        // Plan/lease/provider operations
        if (rawLog.Contains("duplicate node for plan", StringComparison.OrdinalIgnoreCase))
            return "Node is already in this plan.";
        if (rawLog.Contains("duplicate provider", StringComparison.OrdinalIgnoreCase))
            return "Provider already registered — use Update.";
        if (rawLog.Contains("lease") && rawLog.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "No active lease for this node.";
        if (rawLog.Contains("lease") && rawLog.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "Lease already exists for this node.";
        if (rawLog.Contains("invalid price", StringComparison.OrdinalIgnoreCase))
            return "Price mismatch — node may have changed rates.";
        if (rawLog.Contains("invalid status inactive", StringComparison.OrdinalIgnoreCase))
            return "Plan is inactive — activate first.";
        if (rawLog.Contains("active session already exists", StringComparison.OrdinalIgnoreCase))
            return "Session already exists for this node.";
        if (rawLog.Contains("maximum peer limit", StringComparison.OrdinalIgnoreCase))
            return "Node is full — maximum peer limit reached.";
        if (rawLog.Contains("node address mismatch", StringComparison.OrdinalIgnoreCase))
            return "Node address mismatch — wrong node at this URL.";

        // Generic: truncate and return
        return rawLog.Length > 200
            ? $"Transaction failed: {rawLog[..200]}..."
            : $"Transaction failed: {rawLog}";
    }

    // ─── User-Facing Error Messages ───
    // CANONICAL location: ErrorSeverity.UserMessage() in SentinelErrors.cs
    // Convenience wrappers kept here for backwards compatibility with existing code.

    /// <summary>
    /// Map an SDK error code to a user-friendly message.
    /// Delegates to <see cref="ErrorSeverity.UserMessage(string)"/> — the single source of truth.
    /// </summary>
    [Obsolete("Use ErrorSeverity.UserMessage(code) directly — single source of truth in SentinelErrors.cs")]
    public static string UserMessage(string? code) =>
        code is null ? "An unexpected error occurred." : ErrorSeverity.UserMessage(code);

    /// <summary>
    /// Map a SentinelException to a user-friendly message.
    /// Delegates to <see cref="ErrorSeverity.UserMessage(string)"/> — the single source of truth.
    /// </summary>
    [Obsolete("Use ErrorSeverity.UserMessage(ex.Code) directly — single source of truth in SentinelErrors.cs")]
    public static string UserMessage(SentinelException? ex) =>
        ex is null ? "An unexpected error occurred." : ErrorSeverity.UserMessage(ex.Code);

    // ─── Session Cost Estimation ───

    /// <summary>
    /// Estimate session cost for a given node, pricing model, and amount.
    /// </summary>
    /// <param name="node">Chain node with pricing data.</param>
    /// <param name="model">"gb" or "hour".</param>
    /// <param name="amount">Number of GB or hours.</param>
    /// <returns>Estimated cost in udvpn and formatted P2P string.</returns>
    public static (long CostUdvpn, string CostDisplay, string Model, int Amount, string Unit) EstimateSessionPrice(
        ChainNode node, string model, int amount)
    {
        var prices = model == "hour" ? node.HourlyPrices : node.GigabytePrices;
        var entry = prices.FirstOrDefault(p => p.Denom == Constants.Denom);
        if (entry is null) return (0, "N/A", model, amount, model == "hour" ? "hours" : "GB");

        var unitPrice = entry.UdvpnAmount;
        var total = unitPrice * amount;
        return (total, FormatP2P(total), model, amount, model == "hour" ? "hours" : "GB");
    }

    // ─── Session Allocation Computation ───

    /// <summary>
    /// Compute session usage stats from chain session data.
    /// Works for both GB-based and hourly sessions.
    /// </summary>
    public static (long UsedBytes, long MaxBytes, long RemainingBytes, double UsedPercent,
        string UsedDisplay, string MaxDisplay, string RemainingDisplay,
        bool IsGbBased, bool IsHourlyBased) ComputeSessionAllocation(ChainSession session)
    {
        var dl = long.TryParse(session.DownloadBytes, out var d) ? d : 0;
        var ul = long.TryParse(session.UploadBytes, out var u) ? u : 0;
        var max = long.TryParse(session.MaxBytes, out var m) ? m : 0;

        var used = dl + ul;
        var remaining = Math.Max(0, max - used);
        var percent = max > 0 ? Math.Round((double)used / max * 100, 1) : 0;

        var isHourly = session.MaxDuration != null && session.MaxDuration != "0s" && session.MaxDuration != "0";
        return (used, max, remaining, percent,
            FormatBytes(used), FormatBytes(max), FormatBytes(remaining),
            !isHourly, isHourly);
    }

    // ─── Bandwidth Conversion ───

    /// <summary>
    /// Convert bytes transferred over a time period to megabits per second.
    /// </summary>
    /// <param name="bytes">Total bytes transferred.</param>
    /// <param name="seconds">Duration of the transfer in seconds.</param>
    /// <returns>Speed in Mbps, or 0 if seconds is zero or negative.</returns>
    public static double BytesToMbps(long bytes, double seconds)
    {
        if (seconds <= 0 || bytes <= 0)
            return 0.0;

        return (bytes * 8.0) / (seconds * 1_000_000.0);
    }

    // ─── CIDR Validation ───

    /// <summary>
    /// Check if a string is a valid CIDR notation (e.g. "10.8.0.1/32", "fd00::/64").
    /// Validates both the IP address and the prefix length.
    /// </summary>
    /// <param name="cidr">CIDR string to validate.</param>
    /// <returns>True if the CIDR notation is valid.</returns>
    public static bool ValidateCIDR(string cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out var ip))
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix))
            return false;

        // IPv4: 0-32, IPv6: 0-128
        var maxPrefix = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix >= 0 && prefix <= maxPrefix;
    }
}
