using System.Text.Json;

namespace Sentinel.SDK.Core;

/// <summary>
/// ChainClient partial — fee grant queries, issued-grant lookups, expiry monitoring,
/// and operator workflow helpers (grantPlanSubscribers, renewExpiringGrants, monitorFeeGrants).
/// </summary>
public sealed partial class ChainClient
{
    // ─── Fee Grant Queries ───

    /// <summary>
    /// Query fee grants where the given address is the grantee.
    /// </summary>
    /// <param name="grantee">Grantee address (sent1...).</param>
    /// <returns>List of fee grants.</returns>
    public async Task<List<FeeGrant>> QueryFeeGrantsAsync(string grantee, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grantee);

        var path = $"/cosmos/feegrant/v1beta1/allowances/{grantee}";
        var items = await LcdPaginatedAsync(path, "allowances", ct);

        return items.Select(item =>
        {
            var granter = item.TryGetProperty("granter", out var g) ? g.GetString() ?? "" : "";
            var granteeAddr = item.TryGetProperty("grantee", out var ge) ? ge.GetString() ?? "" : "";
            var allowance = item.TryGetProperty("allowance", out var a)
                ? (object)a.ToString()
                : new object();
            return new FeeGrant(granter, granteeAddr, allowance);
        }).ToList();
    }

    // ─── Fee Grant Workflow ───

    /// <summary>
    /// Query fee grants ISSUED BY a granter address.
    /// Unlike <see cref="QueryFeeGrantsAsync"/> which queries grants received,
    /// this queries grants the address has given to others.
    /// </summary>
    /// <param name="granter">Granter address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of fee grants issued by the granter.</returns>
    public async Task<IReadOnlyList<FeeGrant>> QueryFeeGrantsIssuedAsync(string granter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(granter);

        var path = $"/cosmos/feegrant/v1beta1/issued/{granter}";
        var items = await LcdPaginatedAsync(path, "allowances", ct);

        return items.Select(item =>
        {
            var granterAddr = item.TryGetProperty("granter", out var g) ? g.GetString() ?? "" : "";
            var granteeAddr = item.TryGetProperty("grantee", out var ge) ? ge.GetString() ?? "" : "";
            var allowance = item.TryGetProperty("allowance", out var a)
                ? (object)a.ToString()
                : new object();
            return new FeeGrant(granterAddr, granteeAddr, allowance);
        }).ToList();
    }

    /// <summary>
    /// Get fee grants expiring within a given number of days.
    /// Inspects BasicAllowance, PeriodicAllowance, and AllowedMsgAllowance expiration fields.
    /// </summary>
    /// <param name="address">Address to query (granter or grantee).</param>
    /// <param name="withinDays">Number of days to look ahead for expiry (default: 7).</param>
    /// <param name="role">"grantee" to query grants received, "granter" to query grants issued (default: "grantee").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of grants expiring within the specified window.</returns>
    public async Task<IReadOnlyList<ExpiringGrant>> GetExpiringGrantsAsync(
        string address,
        int withinDays = 7,
        string role = "grantee",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        var grants = role == "grantee"
            ? await QueryFeeGrantsAsync(address, ct)
            : (await QueryFeeGrantsIssuedAsync(address, ct)).ToList();

        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(withinDays);
        var expiring = new List<ExpiringGrant>();

        foreach (var g in grants)
        {
            // Parse the allowance JSON to find expiration
            var expirationStr = ExtractGrantExpiration(g.Allowance?.ToString());
            if (string.IsNullOrEmpty(expirationStr))
            {
                continue;
            }

            if (!DateTime.TryParse(expirationStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var expiresAt))
            {
                continue;
            }

            if (expiresAt <= cutoff)
            {
                var daysLeft = Math.Max(0, (int)(expiresAt - now).TotalDays);
                expiring.Add(new ExpiringGrant(g.Granter, g.Grantee, expiresAt, daysLeft));
            }
        }

        return expiring;
    }

    // ─── Internal: Grant Expiration Parsing ───

    /// <summary>
    /// Extract expiration date from a fee grant allowance JSON string.
    /// Handles BasicAllowance, PeriodicAllowance, and AllowedMsgAllowance structures.
    /// </summary>
    private static string? ExtractGrantExpiration(string? allowanceJson)
    {
        if (string.IsNullOrEmpty(allowanceJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(allowanceJson);
            var root = doc.RootElement;

            // Direct expiration (BasicAllowance)
            if (root.TryGetProperty("expiration", out var exp) &&
                exp.ValueKind == JsonValueKind.String)
            {
                return exp.GetString();
            }

            // PeriodicAllowance: { basic: { expiration } }
            if (root.TryGetProperty("basic", out var basic) &&
                basic.TryGetProperty("expiration", out var basicExp) &&
                basicExp.ValueKind == JsonValueKind.String)
            {
                return basicExp.GetString();
            }

            // AllowedMsgAllowance: { allowance: { expiration } } or { allowance: { basic: { expiration } } }
            if (root.TryGetProperty("allowance", out var inner))
            {
                if (inner.TryGetProperty("expiration", out var innerExp) &&
                    innerExp.ValueKind == JsonValueKind.String)
                {
                    return innerExp.GetString();
                }

                if (inner.TryGetProperty("basic", out var innerBasic) &&
                    innerBasic.TryGetProperty("expiration", out var innerBasicExp) &&
                    innerBasicExp.ValueKind == JsonValueKind.String)
                {
                    return innerBasicExp.GetString();
                }
            }
        }
        catch
        {
            // Malformed JSON — no expiration extractable
        }

        return null;
    }

    // ─── Operator Workflow Functions ───

    /// <summary>
    /// Build fee grant messages for all subscribers of a plan.
    /// Queries all subscribers, filters out the granter and already-granted addresses,
    /// and returns batch MsgGrantAllowance messages ready for broadcast.
    /// </summary>
    /// <param name="planId">Plan ID whose subscribers should receive grants.</param>
    /// <param name="granterAddress">Granter address (sent1...) — plan operator who pays gas.</param>
    /// <param name="spendLimitUdvpn">Optional spend limit per grant in udvpn.</param>
    /// <param name="expiration">Optional expiry date for each grant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of grant messages (empty if all subscribers already have grants).</returns>
    public async Task<SentinelMessage[]> BuildGrantPlanSubscribersAsync(
        int planId,
        string granterAddress,
        long? spendLimitUdvpn = null,
        DateTime? expiration = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granterAddress);
        if (planId <= 0)
            throw new ArgumentOutOfRangeException(nameof(planId), "Must be > 0");

        // Get all subscribers, excluding the operator
        var subscribers = await QueryPlanSubscribersAsync(planId, excludeAddress: granterAddress, ct: ct);
        if (subscribers.Count == 0)
            return [];

        // Get existing grants issued by this granter to filter out already-granted
        var existingGrants = await QueryFeeGrantsIssuedAsync(granterAddress, ct);
        var alreadyGranted = new HashSet<string>(existingGrants.Select(g => g.Grantee));

        var messages = new List<SentinelMessage>();
        foreach (var sub in subscribers)
        {
            if (alreadyGranted.Contains(sub.Address))
                continue;

            messages.Add(MessageBuilder.GrantFeeAllowance(
                granterAddress, sub.Address, spendLimitUdvpn, expiration));
        }

        return messages.ToArray();
    }

    /// <summary>
    /// Build messages to renew fee grants expiring within a given window.
    /// For each expiring grant: revokes the old one, creates a new one with fresh expiration.
    /// </summary>
    /// <param name="granterAddress">Granter address (sent1...).</param>
    /// <param name="withinDays">Days ahead to check for expiring grants (default: 7).</param>
    /// <param name="newSpendLimitUdvpn">Optional spend limit for renewed grants.</param>
    /// <param name="newExpiration">Optional new expiration for renewed grants.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of revoke+grant message pairs (empty if nothing is expiring).</returns>
    public async Task<SentinelMessage[]> BuildRenewExpiringGrantsAsync(
        string granterAddress,
        int withinDays = 7,
        long? newSpendLimitUdvpn = null,
        DateTime? newExpiration = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granterAddress);

        var expiring = await GetExpiringGrantsAsync(granterAddress, withinDays, role: "granter", ct: ct);
        if (expiring.Count == 0)
            return [];

        var messages = new List<SentinelMessage>();
        foreach (var grant in expiring)
        {
            // Revoke old grant
            messages.Add(MessageBuilder.RevokeFeeAllowance(granterAddress, grant.Grantee));
            // Re-grant with fresh expiration
            messages.Add(MessageBuilder.GrantFeeAllowance(
                granterAddress, grant.Grantee, newSpendLimitUdvpn, newExpiration));
        }

        return messages.ToArray();
    }

    /// <summary>
    /// Find an existing active session between a wallet and a specific node.
    /// Prevents double-allocation by checking before starting a new session.
    /// </summary>
    /// <param name="walletAddress">Account address (sent1...).</param>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The existing session, or null if none found.</returns>
    public async Task<ChainSession?> FindExistingSessionAsync(
        string walletAddress,
        string nodeAddress,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        var sessions = await GetSessionsAsync(walletAddress, status: "1", ct: ct);
        return sessions.FirstOrDefault(s =>
            string.Equals(s.NodeAddress, nodeAddress, StringComparison.OrdinalIgnoreCase));
    }
}
