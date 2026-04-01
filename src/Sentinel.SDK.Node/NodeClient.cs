using System.Text.Json;
using System.Text.Json.Serialization;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Node Status Models ───

/// <summary>
/// Geographic location reported by a Sentinel node.
/// </summary>
/// <param name="City">City name (e.g. "Frankfurt").</param>
/// <param name="Country">Country name (e.g. "Germany").</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 country code (e.g. "DE").</param>
/// <param name="Latitude">Latitude in decimal degrees.</param>
/// <param name="Longitude">Longitude in decimal degrees.</param>
public record Location(
    string City,
    string Country,
    string CountryCode,
    double Latitude,
    double Longitude
);

/// <summary>
/// Bandwidth capabilities reported by a Sentinel node.
/// </summary>
/// <param name="Upload">Upload speed in bytes per second.</param>
/// <param name="Download">Download speed in bytes per second.</param>
public record Bandwidth(
    long Upload,
    long Download
);

/// <summary>
/// Status information returned by a Sentinel node's status endpoint.
/// </summary>
/// <param name="Address">Node's sentnode1... address from status endpoint. Used to pre-verify before paying.</param>
/// <param name="Type">Node type (e.g. "wireguard", "v2ray").</param>
/// <param name="Moniker">Human-readable node name.</param>
/// <param name="Peers">Current number of connected peers.</param>
/// <param name="Location">Geographic location of the node.</param>
/// <param name="Bandwidth">Upload and download bandwidth of the node.</param>
/// <param name="ClockDriftSec">Estimated clock drift in seconds relative to our local time, or null if unknown.</param>
/// <param name="MaxPeers">Maximum number of peers allowed by QoS, or null if not reported.</param>
public record NodeStatus(
    string? Address,
    string Type,
    string Moniker,
    int Peers,
    Location Location,
    Bandwidth Bandwidth,
    double? ClockDriftSec,
    int? MaxPeers = null
);

// ─── Internal Response Model ───

internal record NodeStatusResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }
}

// ─── Node Client ───

/// <summary>
/// Client for querying Sentinel dVPN node status and metadata.
/// </summary>
public static class NodeClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Shared <see cref="HttpClient"/> that accepts self-signed TLS certificates.
    /// Reused across all node status calls to prevent socket exhaustion.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
    {
        Timeout = Timeout,
    };

    /// <summary>
    /// Queries a Sentinel node for its current status.
    /// </summary>
    /// <param name="nodeUrl">Full remote URL of the node (e.g. "https://1.2.3.4:8585").</param>
    /// <param name="tofuStore">Optional TOFU trust store for certificate pinning.</param>
    /// <param name="nodeAddress">Node address (sentnode1...) for TOFU pinning. Required when <paramref name="tofuStore"/> is provided.</param>
    /// <returns>A <see cref="NodeStatus"/> with the node's reported information.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="nodeUrl"/> is null.</exception>
    /// <exception cref="SentinelNodeException">Thrown when the node is unreachable or returns an invalid response.</exception>
    /// <exception cref="SecurityException">Thrown when the node's TLS certificate has changed since the first connection (possible MITM).</exception>
    public static async Task<NodeStatus> GetStatusAsync(
        string nodeUrl,
        TofuTrustStore? tofuStore = null,
        string? nodeAddress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodeUrl);

        if (tofuStore is not null && string.IsNullOrWhiteSpace(nodeAddress))
        {
            throw new ArgumentException(
                "nodeAddress is required when tofuStore is provided", nameof(nodeAddress));
        }

        var url = nodeUrl.TrimEnd('/') + "/";

        // When a TOFU store is provided, create a per-request HttpClient with certificate pinning.
        HttpClient? tofuClient = null;
        try
        {
            HttpClient httpClient;
            if (tofuStore is not null)
            {
                var handler = tofuStore.CreateHandler(nodeAddress!);
                tofuClient = new HttpClient(handler) { Timeout = Timeout };
                httpClient = tofuClient;
            }
            else
            {
                httpClient = SharedHttpClient;
            }

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await httpClient.GetAsync(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SentinelNodeException($"Failed to connect to node at {url}: {ex.Message}", ex);
            }

            var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

            NodeStatusResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<NodeStatusResponse>(responseBody);
            }
            catch (JsonException ex)
            {
                throw new SentinelNodeException(
                    $"Failed to parse status response from {url}: {ex.Message}", ex);
            }

            if (response is null || !response.Success)
            {
                var errorMsg = response?.Error ?? "Unknown error";
                throw new SentinelNodeException($"Node {url} returned error: {errorMsg}");
            }

            if (response.Result is null)
            {
                throw new SentinelNodeException($"Node {url} returned empty result");
            }

            return ParseNodeStatus(response.Result.Value, httpResponse, url);
        }
        finally
        {
            tofuClient?.Dispose();
        }
    }

    /// <summary>
    /// Parses the result JSON element into a <see cref="NodeStatus"/> record.
    /// </summary>
    private static NodeStatus ParseNodeStatus(JsonElement result, HttpResponseMessage httpResponse, string nodeUrl)
    {
        // Parse node address for pre-verification (prevents paying for wrong node)
        var nodeAddr = result.TryGetProperty("address", out var addrEl)
            ? addrEl.GetString()
            : null;

        // Normalize type to "wireguard" or "v2ray" — matches JS SDK behavior.
        // Anything that isn't "wireguard" is treated as "v2ray" (the only two service types on Sentinel).
        var rawType = result.TryGetProperty("service_type", out var stEl)
            ? stEl.GetString() ?? ""
            : result.TryGetProperty("type", out var typeEl)
                ? typeEl.GetString() ?? ""
                : "";
        var type = rawType == "wireguard" ? "wireguard" : "v2ray";

        var moniker = result.TryGetProperty("moniker", out var monikerEl)
            ? monikerEl.GetString() ?? ""
            : "";

        var peers = result.TryGetProperty("peers", out var peersEl)
            ? peersEl.GetInt32()
            : 0;

        // Parse location
        var location = new Location("", "", "", 0, 0);
        if (result.TryGetProperty("location", out var locEl))
        {
            var city = locEl.TryGetProperty("city", out var cityEl)
                ? cityEl.GetString() ?? ""
                : "";
            var country = locEl.TryGetProperty("country", out var countryEl)
                ? countryEl.GetString() ?? ""
                : "";
            var countryCode = locEl.TryGetProperty("country_code", out var ccEl)
                ? ccEl.GetString() ?? ""
                : "";
            var lat = locEl.TryGetProperty("latitude", out var latEl)
                ? latEl.GetDouble()
                : 0;
            var lon = locEl.TryGetProperty("longitude", out var lonEl)
                ? lonEl.GetDouble()
                : 0;
            location = new Location(city, country, countryCode, lat, lon);
        }

        // Parse bandwidth — v3 uses top-level "downlink"/"uplink" strings (bytes/s), NOT nested "bandwidth" object
        var bandwidth = new Bandwidth(0, 0);
        {
            long upload = 0, download = 0;
            // v3 format: top-level downlink/uplink (string values)
            if (result.TryGetProperty("uplink", out var upEl))
            {
                if (upEl.ValueKind == JsonValueKind.String)
                    long.TryParse(upEl.GetString(), out upload);
                else if (upEl.ValueKind == JsonValueKind.Number)
                    upload = upEl.GetInt64();
            }
            if (result.TryGetProperty("downlink", out var downEl))
            {
                if (downEl.ValueKind == JsonValueKind.String)
                    long.TryParse(downEl.GetString(), out download);
                else if (downEl.ValueKind == JsonValueKind.Number)
                    download = downEl.GetInt64();
            }
            // Fallback: legacy nested bandwidth object
            if (upload == 0 && download == 0 && result.TryGetProperty("bandwidth", out var bwEl))
            {
                if (bwEl.TryGetProperty("upload", out var bwUp))
                    upload = bwUp.ValueKind == JsonValueKind.Number ? bwUp.GetInt64() : 0;
                if (bwEl.TryGetProperty("download", out var bwDown))
                    download = bwDown.ValueKind == JsonValueKind.Number ? bwDown.GetInt64() : 0;
            }
            bandwidth = new Bandwidth(upload, download);
        }

        // Parse QoS max_peers (matches JS SDK: qos.max_peers)
        int? maxPeers = null;
        if (result.TryGetProperty("qos", out var qosEl)
            && qosEl.TryGetProperty("max_peers", out var maxPeersEl)
            && maxPeersEl.ValueKind == JsonValueKind.Number)
        {
            maxPeers = maxPeersEl.GetInt32();
        }

        // Detect clock drift from the HTTP Date header (matches JS SDK approach).
        // VMess AEAD auth fails if |client_time - server_time| > 120 seconds.
        double? clockDrift = null;
        if (httpResponse.Headers.Date.HasValue)
        {
            var serverTime = httpResponse.Headers.Date.Value;
            var localNow = DateTimeOffset.UtcNow;
            clockDrift = Math.Round((serverTime - localNow).TotalSeconds);
        }

        return new NodeStatus(nodeAddr, type, moniker, peers, location, bandwidth, clockDrift, maxPeers);
    }
}

// ─── Node Exception ───

/// <summary>
/// Thrown when a Sentinel node status query fails.
/// Inherits from <see cref="NodeException"/> in the unified error hierarchy.
/// </summary>
public class SentinelNodeException : NodeException
{
    /// <summary>Initializes a new instance with the specified message.</summary>
    public SentinelNodeException(string message)
        : base(ErrorCodes.NodeOffline, message) { }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    public SentinelNodeException(string message, Exception innerException)
        : base(ErrorCodes.NodeOffline, message, innerException) { }

    /// <summary>Initializes a new instance with a specific error code and message.</summary>
    public SentinelNodeException(string code, string message)
        : base(code, message) { }
}
