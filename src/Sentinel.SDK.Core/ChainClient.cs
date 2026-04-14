using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.SDK.Core;

/// <summary>
/// HTTP client for Sentinel chain LCD (REST) and RPC queries.
/// Handles endpoint failover, pagination, and retry logic.
/// </summary>
public sealed partial class ChainClient : IChainClient, IDisposable
{
    private string[] _lcdUrls;
    private readonly string[] _rpcUrls;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _publicHttpClient; // CA-validated for LCD/RPC
    private readonly ISdkLogger? _logger;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);
    private readonly RpcClient _rpcClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Create a new chain client with LCD and RPC endpoint lists.
    /// </summary>
    /// <param name="lcdUrls">LCD REST API endpoints (tried in order on failure).</param>
    /// <param name="rpcUrls">RPC endpoints (tried in order on failure).</param>
    /// <param name="logger">Optional SDK logger for diagnostics.</param>
    public ChainClient(string[]? lcdUrls = null, string[]? rpcUrls = null, ISdkLogger? logger = null)
    {
        _logger = logger;
        _lcdUrls = lcdUrls ?? Constants.DefaultLcdUrls;
        _rpcUrls = rpcUrls ?? Constants.DefaultRpcUrls;

        if (_lcdUrls.Length == 0)
        {
            throw new SentinelException("CLIENT_NO_LCD", "At least one LCD URL is required.");
        }

        if (_rpcUrls.Length == 0)
        {
            throw new SentinelException("CLIENT_NO_RPC", "At least one RPC URL is required.");
        }

        var handler = new HttpClientHandler
        {
            // Accept self-signed certs (node APIs often use self-signed TLS)
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = _timeout,
        };

        // Public HTTP client with default TLS validation for LCD/RPC endpoints
        _publicHttpClient = new HttpClient(new HttpClientHandler
        {
            // Default TLS validation — verifies CA-signed certs
        })
        {
            Timeout = _timeout,
        };

        // RPC client for protobuf/ABCI queries (~10x faster than LCD)
        _rpcClient = new RpcClient(_rpcUrls, _logger);
    }

    // ─── Initialization ───

    /// <summary>
    /// Probe all LCD endpoints and reorder by response time (fastest first).
    /// Call this before the first connection to ensure optimal endpoint ordering.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var results = await CheckEndpointHealthAsync(timeoutMs: 3000, ct);
        var sorted = results.Where(r => r.LatencyMs.HasValue).OrderBy(r => r.LatencyMs).ToList();
        if (sorted.Count > 0)
        {
            _lcdUrls = sorted.Select(r => r.Url).Concat(
                results.Where(r => !r.LatencyMs.HasValue).Select(r => r.Url)
            ).ToArray();
        }
    }

    // ─── IDisposable ───

    public void Dispose()
    {
        _httpClient.Dispose();
        _publicHttpClient.Dispose();
        _rpcClient.Dispose();
    }
}
