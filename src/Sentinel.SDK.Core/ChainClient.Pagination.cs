using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sentinel.SDK.Core;

/// <summary>
/// ChainClient partial — LCD pagination helpers including broken-pagination fallback.
/// </summary>
public sealed partial class ChainClient
{
    // ─── Internal: Paginated LCD Query ───

    /// <summary>
    /// Fetch all pages from a paginated LCD endpoint.
    /// Handles broken Sentinel pagination: if next_key is null but items == limit,
    /// falls back to a single large request with limit=5000.
    /// Requests count_total on the first page and warns if reported total != actual count.
    /// </summary>
    /// <param name="path">Base LCD path (may include query params).</param>
    /// <param name="itemsKey">JSON key containing the array of items.</param>
    /// <returns>All items across all pages.</returns>
    private async Task<List<JsonElement>> LcdPaginatedAsync(string path, string itemsKey, CancellationToken ct = default)
    {
        var allItems = new List<JsonElement>();
        string? nextKey = null;
        var seenKeys = new HashSet<string>();
        var maxPages = 50; // Safety limit to prevent infinite loops
        ulong? reportedTotal = null;

        // Detect the per-page limit from the path, default 100
        var pageLimit = 100;
        var limitMatch = Regex.Match(path, @"pagination\.limit=(\d+)");
        if (limitMatch.Success)
        {
            int.TryParse(limitMatch.Groups[1].Value, out pageLimit);
        }

        for (var page = 0; page < maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var paginatedPath = path;
            var separator = path.Contains('?') ? '&' : '?';

            if (nextKey != null)
            {
                paginatedPath = $"{path}{separator}pagination.key={Uri.EscapeDataString(nextKey)}";
            }
            else if (page == 0)
            {
                // First request: ask for count_total
                paginatedPath = $"{path}{separator}pagination.count_total=true";
            }

            var json = await LcdGetAsync(paginatedPath, ct);

            // Extract items for this page
            var pageItems = new List<JsonElement>();
            if (json.TryGetProperty(itemsKey, out var itemsArray) &&
                itemsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsArray.EnumerateArray())
                {
                    pageItems.Add(item.Clone());
                }
            }

            allItems.AddRange(pageItems);

            // Parse pagination metadata
            string? nextKeyStr = null;
            if (json.TryGetProperty("pagination", out var pagination))
            {
                // Capture reported total on first page
                if (page == 0 &&
                    pagination.TryGetProperty("total", out var totalProp))
                {
                    var totalStr = totalProp.ValueKind == JsonValueKind.String
                        ? totalProp.GetString()
                        : totalProp.ToString();
                    if (ulong.TryParse(totalStr, out var total))
                    {
                        reportedTotal = total;
                    }
                }

                if (pagination.TryGetProperty("next_key", out var nextKeyProp) &&
                    nextKeyProp.ValueKind == JsonValueKind.String)
                {
                    nextKeyStr = nextKeyProp.GetString();
                }
            }

            if (!string.IsNullOrEmpty(nextKeyStr))
            {
                // Guard against broken pagination returning the same key
                if (!seenKeys.Add(nextKeyStr!))
                {
                    break; // Already seen this key — pagination is looping
                }

                nextKey = nextKeyStr;
            }
            else
            {
                // next_key is null — check for broken pagination:
                // if items returned == limit, pagination may be silently broken
                if (pageItems.Count >= pageLimit && page == 0)
                {
                    // Broken pagination detected: got exactly limit items but no next_key.
                    // Fall back to a single large request with limit=5000.
                    _logger?.Warn(
                        $"Broken pagination detected on {path}: " +
                        $"got {pageItems.Count} items (== limit) but no next_key. " +
                        "Falling back to limit=5000 single request.");

                    var fallbackSep = path.Contains('?') ? '&' : '?';
                    var fallbackPath = $"{path}{fallbackSep}pagination.limit=5000";
                    var fallbackJson = await LcdGetAsync(fallbackPath, ct);

                    allItems.Clear();
                    if (fallbackJson.TryGetProperty(itemsKey, out var fallbackArr) &&
                        fallbackArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in fallbackArr.EnumerateArray())
                        {
                            allItems.Add(item.Clone());
                        }
                    }
                }

                break; // No more pages
            }
        }

        // Warn if reported total doesn't match actual count
        var chainTotal = reportedTotal.HasValue ? (int?)reportedTotal.Value : null;
        if (chainTotal.HasValue && allItems.Count != chainTotal.Value)
            _logger?.Warn($"Pagination mismatch on {path}: got {allItems.Count}, chain reports {chainTotal.Value}");

        return allItems;
    }
}
