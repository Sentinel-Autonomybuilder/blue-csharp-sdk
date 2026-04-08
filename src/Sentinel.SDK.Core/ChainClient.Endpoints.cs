using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sentinel.SDK.Core;

/// <summary>
/// ChainClient partial — LCD GET with failover, broadcast via failover, and endpoint health checks.
/// </summary>
public sealed partial class ChainClient
{
    // ─── Internal: LCD GET with Failover ───

    /// <summary>
    /// Execute an LCD GET request with timeout, retry, and endpoint failover.
    /// </summary>
    private async Task<JsonElement> LcdGetAsync(string path, CancellationToken ct = default)
    {
        Exception? lastException = null;

        foreach (var baseUrl in _lcdUrls)
        {
            try
            {
                var url = baseUrl.TrimEnd('/') + path;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                var response = await _publicHttpClient.GetAsync(url, cts.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new SentinelException("CLIENT_HTTP_404",
                        $"Resource not found: {path}");
                }

                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (SentinelException)
            {
                throw; // Don't retry on known errors like 404
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Caller cancelled — propagate immediately
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastException = ex;
                // Try next endpoint
            }
        }

        throw new SentinelException("CLIENT_ALL_ENDPOINTS_FAILED",
            $"All LCD endpoints failed for {path}: {lastException?.Message}", lastException!);
    }

    // ─── Health ───

    /// <summary>
    /// Check the health of all configured LCD endpoints by measuring response latency.
    /// Each endpoint is probed with a lightweight balance query.
    /// </summary>
    /// <param name="timeoutMs">Per-endpoint timeout in milliseconds (default: 5000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health results for each configured LCD endpoint.</returns>
    public async Task<IReadOnlyList<EndpointHealth>> CheckEndpointHealthAsync(
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        var results = new List<EndpointHealth>();

        foreach (var baseUrl in _lcdUrls)
        {
            ct.ThrowIfCancellationRequested();

            var name = "unknown";
            try
            {
                var uri = new Uri(baseUrl);
                name = uri.Host;
            }
            catch
            {
                name = baseUrl;
            }

            try
            {
                var url = baseUrl.TrimEnd('/') + "/cosmos/base/tendermint/v1beta1/syncing";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                var sw = Stopwatch.StartNew();
                var response = await _publicHttpClient.GetAsync(url, cts.Token);
                sw.Stop();

                response.EnsureSuccessStatusCode();

                results.Add(new EndpointHealth(baseUrl, name, (int)sw.ElapsedMilliseconds));
            }
            catch
            {
                results.Add(new EndpointHealth(baseUrl, name, null));
            }
        }

        return results;
    }
}
