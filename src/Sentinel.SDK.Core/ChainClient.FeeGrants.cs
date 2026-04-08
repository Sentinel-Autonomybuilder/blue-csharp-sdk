using System.Text.Json;

namespace Sentinel.SDK.Core;

/// <summary>
/// ChainClient partial — fee grant queries, issued-grant lookups, and expiry monitoring.
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
}
