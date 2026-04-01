using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Handshake Types ───

/// <summary>
/// Specifies the proxy transport type for the V3 handshake.
/// </summary>
public enum HandshakeType
{
    /// <summary>WireGuard tunnel — generates X25519 keypair.</summary>
    WireGuard,

    /// <summary>V2Ray tunnel — generates UUID identifier.</summary>
    V2Ray,
}

// ─── Handshake Result Records ───

/// <summary>
/// Result of a successful WireGuard V3 handshake containing tunnel configuration.
/// </summary>
/// <param name="ServerPublicKey">Base64-encoded X25519 public key of the node.</param>
/// <param name="AssignedAddresses">Assigned client addresses (e.g. ["10.8.0.2/24", "fd1d::2/128"]).</param>
/// <param name="ServerEndpoint">Node WireGuard endpoint in "ip:port" format.</param>
/// <param name="ClientPrivateKey">Raw X25519 private key bytes for building the WireGuard config.</param>
public record WireGuardHandshakeResult(
    string ServerPublicKey,
    string[] AssignedAddresses,
    string ServerEndpoint,
    byte[] ClientPrivateKey
);

/// <summary>
/// A single V2Ray transport metadata entry from the handshake response.
/// Each node may advertise multiple transports (e.g. tcp/none, grpc/tls, websocket/none).
/// </summary>
/// <param name="ProxyProtocol">Proxy protocol: 1=VLess, 2=VMess.</param>
/// <param name="Transport">Transport type: 1=domainsocket, 2=gun, 3=grpc, 4=http, 5=mkcp, 6=quic, 7=tcp, 8=websocket.</param>
/// <param name="Tls">TLS mode: 0=none, 1=tls.</param>
/// <param name="Port">Listening port on the node.</param>
public record V2RayTransportEntry(
    int ProxyProtocol,
    int Transport,
    int Tls,
    int Port
);

/// <summary>
/// Result of a successful V2Ray V3 handshake containing proxy metadata.
/// The top-level fields (ProxyProtocol, Transport, Tls, Port) represent the BEST
/// transport entry (highest reliability). <see cref="AllEntries"/> contains ALL
/// usable entries sorted by reliability — pass them all to V2RayConfigBuilder
/// to build a multi-outbound config with automatic fallback.
/// </summary>
/// <param name="Uuid">UUID string for VLess/VMess authentication.</param>
/// <param name="ProxyProtocol">Best transport's proxy protocol: 1=VLess, 2=VMess.</param>
/// <param name="Transport">Best transport type: 1=domainsocket, 2=gun, 3=grpc, 4=http, 5=mkcp, 6=quic, 7=tcp, 8=websocket.</param>
/// <param name="Tls">Best transport's TLS mode: 0=none, 1=tls.</param>
/// <param name="Port">Best transport's listening port on the node.</param>
public record V2RayHandshakeResult(
    string Uuid,
    int ProxyProtocol,
    int Transport,
    int Tls,
    int Port
)
{
    /// <summary>
    /// All usable transport entries from the handshake, sorted by reliability (best first).
    /// The JS SDK builds one V2Ray outbound per entry — use
    /// <see cref="Tunnel.V2Ray.V2RayConfigBuilder.BuildMultiOutboundConfig"/> to replicate this.
    /// </summary>
    public IReadOnlyList<V2RayTransportEntry> AllEntries { get; init; } = Array.Empty<V2RayTransportEntry>();
};

// ─── Internal JSON Models ───

internal record HandshakeRequest
{
    [JsonPropertyName("data")]
    public string Data { get; init; } = "";

    [JsonPropertyName("id")]
    public ulong Id { get; init; }

    [JsonPropertyName("pub_key")]
    public string PubKey { get; init; } = "";

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";
}

internal record HandshakeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    public string? ErrorMessage => Error?.ValueKind switch
    {
        JsonValueKind.String => Error?.GetString(),
        JsonValueKind.Number => $"Error code {Error?.GetInt32()}",
        JsonValueKind.Null => null,
        null => null,
        _ => Error?.GetRawText(),
    };
}

// ─── Handshake Implementation ───

/// <summary>
/// Performs V3 handshake with Sentinel dVPN nodes to establish tunnel sessions.
/// </summary>
public static class Handshake
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Shared <see cref="HttpClient"/> that accepts self-signed TLS certificates.
    /// Reused across all handshake calls to prevent socket exhaustion.
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
    /// Performs a V3 handshake with a Sentinel node to establish a WireGuard or V2Ray tunnel.
    /// </summary>
    /// <param name="wallet">Wallet used for signing the handshake request.</param>
    /// <param name="nodeUrl">Full remote URL of the node (e.g. "https://1.2.3.4:8585").</param>
    /// <param name="sessionId">On-chain session ID obtained after subscribing to the node.</param>
    /// <param name="type">Transport type: WireGuard or V2Ray.</param>
    /// <param name="tofuStore">Optional TOFU trust store for certificate pinning. When provided, creates a per-request
    /// handler with TOFU validation instead of accepting all certificates.</param>
    /// <param name="nodeAddress">Node address (sentnode1...) for TOFU pinning. Required when <paramref name="tofuStore"/> is provided.</param>
    /// <returns>
    /// A <see cref="WireGuardHandshakeResult"/> when <paramref name="type"/> is <see cref="HandshakeType.WireGuard"/>,
    /// or a <see cref="V2RayHandshakeResult"/> when <paramref name="type"/> is <see cref="HandshakeType.V2Ray"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="wallet"/> or <paramref name="nodeUrl"/> is null.</exception>
    /// <exception cref="SentinelHandshakeException">Thrown when the node rejects the handshake or communication fails.</exception>
    /// <exception cref="SecurityException">Thrown when the node's TLS certificate has changed since the first connection (possible MITM).</exception>
    public static async Task<object> HandshakeAsync(
        ISentinelWallet wallet,
        string nodeUrl,
        ulong sessionId,
        HandshakeType type,
        TofuTrustStore? tofuStore = null,
        string? nodeAddress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(nodeUrl);

        if (tofuStore is not null && string.IsNullOrWhiteSpace(nodeAddress))
        {
            throw new ArgumentException(
                "nodeAddress is required when tofuStore is provided", nameof(nodeAddress));
        }

        // Generate client credentials
        byte[]? clientPrivateKey = null;
        string? clientUuidStr = null; // V2Ray: dashed UUID string for V2Ray config
        string dataJson;

        if (type == HandshakeType.WireGuard)
        {
            // Generate WireGuard key pair using pure .NET Curve25519 (NSec.Cryptography).
            // Removes dependency on having wg.exe on PATH and works on any .NET platform.
            string pubKeyB64;
            try
            {
                var algorithm = KeyAgreementAlgorithm.X25519;
                using var key = Key.Create(algorithm, new KeyCreationParameters
                {
                    ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
                });
                var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
                var publicKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

                // Copy private key before zeroing intermediate buffer
                clientPrivateKey = new byte[privateKeyBytes.Length];
                Array.Copy(privateKeyBytes, clientPrivateKey, privateKeyBytes.Length);
                CryptographicOperations.ZeroMemory(privateKeyBytes);

                pubKeyB64 = Convert.ToBase64String(publicKeyBytes);
                CryptographicOperations.ZeroMemory(publicKeyBytes);
            }
            catch (Exception ex)
            {
                throw new SentinelHandshakeException($"Failed to generate WireGuard keys: {ex.Message}", ex);
            }

            dataJson = JsonSerializer.Serialize(new { public_key = pubKeyB64 }, JsonOptions);
        }
        else
        {
            var uuid = Guid.NewGuid();
            // Store UUID as standard dashed string for V2Ray config (matches JS SDK's randomUUID())
            clientUuidStr = uuid.ToString("D");
            // Get big-endian bytes (RFC 4122 format, matching Go uuid.UUID)
            // .NET Guid.ToByteArray() returns mixed-endian (first 3 groups LE, last 2 BE).
            // Instead, parse the hex string to get correct network byte order.
            var uuidHex = uuid.ToString("N"); // 32 hex chars, no dashes
            var uuidBytes = Convert.FromHexString(uuidHex);
            // CRITICAL: V2Ray peer data must send uuid as integer byte array, NOT base64 string.
            // JS SDK: { uuid: [b0, b1, ..., b15] } — the node expects this exact format.
            var uuidIntArray = new int[uuidBytes.Length];
            for (var i = 0; i < uuidBytes.Length; i++)
            {
                uuidIntArray[i] = uuidBytes[i];
            }
            dataJson = JsonSerializer.Serialize(new { uuid = uuidIntArray }, JsonOptions);
        }

        // data field for the request = base64(json)
        var rawJsonBytes = Encoding.UTF8.GetBytes(dataJson);
        var dataBase64 = Convert.ToBase64String(rawJsonBytes);

        // sign_bytes = BigEndian(uint64(session_id)) + raw_json_bytes
        // IMPORTANT: sign over the RAW JSON bytes, NOT the base64 string
        var idBytes = new byte[8];
        WriteBigEndianUInt64(idBytes, sessionId);
        var signBytes = new byte[idBytes.Length + rawJsonBytes.Length];
        Buffer.BlockCopy(idBytes, 0, signBytes, 0, idBytes.Length);
        Buffer.BlockCopy(rawJsonBytes, 0, signBytes, idBytes.Length, rawJsonBytes.Length);

        // Hash and sign, then zero sensitive intermediates
        var hash = SHA256.HashData(signBytes);
        var signature = wallet.Sign(hash);
        var signatureBase64 = Convert.ToBase64String(signature);
        CryptographicOperations.ZeroMemory(hash);
        CryptographicOperations.ZeroMemory(signBytes);
        CryptographicOperations.ZeroMemory(signature);

        // Build pub_key
        var compressedPubKey = wallet.GetPublicKeyCompressed();
        var pubKeyField = "secp256k1:" + Convert.ToBase64String(compressedPubKey);

        // Build request
        var request = new HandshakeRequest
        {
            Data = dataBase64,
            Id = sessionId,
            PubKey = pubKeyField,
            Signature = signatureBase64,
        };

        // Send POST with chain-lag retry
        // When a TOFU store is provided, create a per-request HttpClient with certificate pinning.
        // Otherwise, fall back to the shared client that accepts all certificates.
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

            var url = nodeUrl.TrimEnd('/') + "/";
            var (httpResponse, responseBody) = await PostWithChainLagRetryAsync(url, request, httpClient, ct);

        // ─── Handle HTTP 409 or "already exists" — session conflict ───
        if (httpResponse.StatusCode == System.Net.HttpStatusCode.Conflict ||
            responseBody.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new SentinelHandshakeException(
                ErrorCodes.SessionExists,
                $"Session already exists on node {url}. The previous session must be ended or a new session created.");
        }

        HandshakeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<HandshakeResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            throw new SentinelHandshakeException(
                $"Failed to parse handshake response from {url}: {ex.Message}", ex);
        }

        if (response is null || !response.Success)
        {
            var errorMsg = response?.ErrorMessage ?? "Unknown error";

            // Also check error text for "already exists" pattern
            if (errorMsg.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw new SentinelHandshakeException(
                    ErrorCodes.SessionExists,
                    $"Session already exists on node {url}: {errorMsg}");
            }

            // ─── Classify known node errors for caller-side retry/fast-fail ───
            // Address mismatch: node has wrong internal address config (retryable once)
            if (errorMsg.Contains("address mismatch", StringComparison.OrdinalIgnoreCase))
            {
                throw new SentinelHandshakeException(
                    "NODE_MISCONFIGURED",
                    $"Node address mismatch at {url}: {errorMsg}");
            }

            // DB corrupt: node's SQLite is broken (permanent, don't retry)
            if (errorMsg.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                throw new SentinelHandshakeException(
                    "NODE_DB_CORRUPT",
                    $"Node database corrupt at {url}: {errorMsg}");
            }

            // RPC backend broken: node can't verify session via its RPC (retryable)
            if (errorMsg.Contains("ABCI query failed", StringComparison.OrdinalIgnoreCase)
                || errorMsg.Contains("rpc error", StringComparison.OrdinalIgnoreCase))
            {
                throw new SentinelHandshakeException(
                    "NODE_RPC_BROKEN",
                    $"Node RPC backend error at {url}: {errorMsg}");
            }

            throw new SentinelHandshakeException(
                $"Handshake rejected by node {url}: {errorMsg}");
        }

        if (response.Result is null)
        {
            throw new SentinelHandshakeException(
                $"Handshake response from {url} is missing result data");
        }

        // Parse result.data (base64-encoded response payload)
        try
        {
            return type == HandshakeType.WireGuard
                ? ParseWireGuardResponse(response.Result.Value, clientPrivateKey!, url)
                : ParseV2RayResponse(response.Result.Value, clientUuidStr!, url);
        }
        catch (Exception ex) when (ex is not SentinelHandshakeException)
        {
            var raw = response.Result?.GetRawText() ?? "null";
            throw new SentinelHandshakeException($"Failed to parse handshake response: {ex.Message}. Raw: {raw[..Math.Min(200, raw.Length)]}", ex);
        }
        }
        finally
        {
            tofuClient?.Dispose();
        }
    }

    /// <summary>
    /// POST the handshake request with automatic retry on chain propagation lag.
    /// When the node returns "does not exist" (session not yet visible on-chain),
    /// waits 10 seconds and retries once.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, string Body)> PostWithChainLagRetryAsync(
        string url, HandshakeRequest request, HttpClient httpClient, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await httpClient.PostAsJsonAsync(url, request, JsonOptions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SentinelHandshakeException($"Failed to connect to node at {url}: {ex.Message}", ex);
            }

            var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

            // Check for chain lag: node hasn't seen the session on-chain yet
            var isChainLag = responseBody.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || (httpResponse.StatusCode == System.Net.HttpStatusCode.NotFound
                    && responseBody.Contains("\"code\":5", StringComparison.Ordinal));

            if (isChainLag && attempt == 0)
            {
                // Node's RPC hasn't seen the block yet — wait and retry
                System.Diagnostics.Trace.WriteLine(
                    "Session not yet visible on node — waiting 10s for chain propagation...");
                await Task.Delay(10_000, ct);
                continue;
            }

            if (isChainLag && attempt == 1)
            {
                throw new SentinelHandshakeException(
                    "CHAIN_LAG",
                    $"Session does not exist on node {url} after retry (chain propagation delay)");
            }

            return (httpResponse, responseBody);
        }

        // Unreachable, but satisfies compiler
        throw new SentinelHandshakeException("Unexpected retry loop exit");
    }

    /// <summary>
    /// Parses the WireGuard AddPeerResponse from the handshake result.
    /// </summary>
    private static WireGuardHandshakeResult ParseWireGuardResponse(
        JsonElement result, byte[] clientPrivateKey, string nodeUrl)
    {
        string resultData;
        try
        {
            resultData = result.GetProperty("data").GetString()
                ?? throw new SentinelHandshakeException("WireGuard result data is null");
        }
        catch (KeyNotFoundException)
        {
            throw new SentinelHandshakeException(
                $"WireGuard handshake response from {nodeUrl} is missing 'data' field");
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(resultData));
        JsonElement peerResponse;
        try
        {
            peerResponse = JsonSerializer.Deserialize<JsonElement>(decoded);
        }
        catch (JsonException ex)
        {
            throw new SentinelHandshakeException(
                $"Failed to parse WireGuard peer response from {nodeUrl}: {ex.Message}", ex);
        }

        // ─── v3 format: { addrs: [...], metadata: [{ port, public_key }] } ───
        if (peerResponse.TryGetProperty("metadata", out var metaArr) && metaArr.ValueKind == JsonValueKind.Array)
        {
            var meta = metaArr[0];
            var serverPubKey = meta.GetProperty("public_key").GetString()
                ?? throw new SentinelHandshakeException("Server public key missing in metadata");
            var port = 51820;
            if (meta.TryGetProperty("port", out var portEl))
            {
                if (portEl.ValueKind == JsonValueKind.String)
                    int.TryParse(portEl.GetString(), out port);
                else if (portEl.ValueKind == JsonValueKind.Number)
                    port = portEl.GetInt32();
            }

            var addresses = new List<string>();
            if (peerResponse.TryGetProperty("addrs", out var addrsEl) && addrsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var ip in addrsEl.EnumerateArray())
                {
                    var addr = ip.GetString();
                    if (addr is not null) addresses.Add(addr);
                }
            }

            // Build endpoint from top-level addrs (server IPs) + metadata port
            // result.addrs may already contain "IP:PORT" — don't double-append port
            var rawEndpoint = "";
            if (result.TryGetProperty("addrs", out var serverAddrs) && serverAddrs.ValueKind == JsonValueKind.Array)
            {
                rawEndpoint = serverAddrs[0].GetString() ?? "";
            }
            var endpoint = string.IsNullOrEmpty(rawEndpoint) ? ""
                : rawEndpoint.Contains(':') ? rawEndpoint
                : port > 0 ? $"{rawEndpoint}:{port}" : "";

            return new WireGuardHandshakeResult(
                ServerPublicKey: serverPubKey,
                AssignedAddresses: addresses.ToArray(),
                ServerEndpoint: endpoint,
                ClientPrivateKey: clientPrivateKey
            );
        }

        // ─── Legacy format: { public_key, allowed_ips, endpoint } ───
        var legacyPubKey = peerResponse.GetProperty("public_key").GetString()
            ?? throw new SentinelHandshakeException("Server public key is missing");

        var legacyAddresses = new List<string>();
        if (peerResponse.TryGetProperty("allowed_ips", out var allowedIps) && allowedIps.ValueKind == JsonValueKind.Array)
        {
            foreach (var ip in allowedIps.EnumerateArray())
            {
                var addr = ip.GetString();
                if (addr is not null) legacyAddresses.Add(addr);
            }
        }

        var legacyEndpoint = peerResponse.TryGetProperty("endpoint", out var ep)
            ? ep.GetString() ?? ""
            : "";

        return new WireGuardHandshakeResult(
            ServerPublicKey: legacyPubKey,
            AssignedAddresses: legacyAddresses.ToArray(),
            ServerEndpoint: legacyEndpoint,
            ClientPrivateKey: clientPrivateKey
        );
    }

    /// <summary>
    /// Parses the V2Ray metadata from the handshake result.
    /// The response contains a metadata ARRAY with entries like:
    ///   {"metadata":[{"port":"55215","proxy_protocol":2,"transport_protocol":3,"transport_security":1},...]}
    /// Field names are transport_protocol (NOT transport) and transport_security (NOT tls).
    /// transport_security: 0=unspecified, 1=none, 2=TLS (per sentinel-go-sdk transport.go iota).
    /// The UUID is client-generated and must be passed in, not read from the response.
    /// </summary>
    private static V2RayHandshakeResult ParseV2RayResponse(JsonElement result, string clientUuid, string nodeUrl)
    {
        string resultData;
        try
        {
            resultData = result.GetProperty("data").GetString()
                ?? throw new SentinelHandshakeException("V2Ray result data is null");
        }
        catch (KeyNotFoundException)
        {
            throw new SentinelHandshakeException(
                $"V2Ray handshake response from {nodeUrl} is missing 'data' field");
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(resultData));
        JsonElement parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(decoded);
        }
        catch (JsonException ex)
        {
            throw new SentinelHandshakeException(
                $"Failed to parse V2Ray metadata from {nodeUrl}: {ex.Message}", ex);
        }

        // The response is { metadata: [{port, proxy_protocol, transport_protocol, transport_security}, ...] }
        if (!parsed.TryGetProperty("metadata", out var metaArr) || metaArr.ValueKind != JsonValueKind.Array || metaArr.GetArrayLength() == 0)
        {
            throw new SentinelHandshakeException(
                $"V2Ray handshake response from {nodeUrl} has no metadata entries");
        }

        // Sort metadata entries by transport reliability (matches JS SDK's filterAndSortTransports).
        // The JS SDK creates outbounds for ALL entries and routes to the most reliable one.
        // We pick the single best entry after sorting by the same priority order.
        var entries = new List<JsonElement>();
        foreach (var entry in metaArr.EnumerateArray())
        {
            // Filter out domainsocket (transport_protocol=1) — can't work remotely/on Windows
            // Filter out QUIC (transport_protocol=6) — 0% success rate in 1017-node audit
            var tp = entry.TryGetProperty("transport_protocol", out var tpEl) ? tpEl.GetInt32() : 7;
            if (tp != 1 && tp != 6)
            {
                entries.Add(entry);
            }
        }

        if (entries.Count == 0)
        {
            throw new SentinelHandshakeException(
                $"V2Ray handshake response from {nodeUrl} has no usable transport entries");
        }

        // Sort by transport reliability: tcp(7)=0, ws(8)=1, http(4)=2, gun(2)=3, kcp(5)=4,
        // grpc(3)/none=5, grpc(3)/tls=8, quic(6)/tls=9, quic(6)/none=10
        entries.Sort((a, b) => TransportSortKey(a) - TransportSortKey(b));

        // Build ALL transport entries (sorted by reliability) for multi-outbound config.
        // The JS SDK creates one outbound per entry — the C# SDK now does the same.
        var allEntries = new List<V2RayTransportEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var entryPp = entry.TryGetProperty("proxy_protocol", out var ppEl) ? ppEl.GetInt32() : 0;
            var entryTp = entry.TryGetProperty("transport_protocol", out var tpInner) ? tpInner.GetInt32() : 7;
            var entryTs = entry.TryGetProperty("transport_security", out var tsInner) ? tsInner.GetInt32() : 0;
            var entryTls = entryTs == 2 ? 1 : 0;
            var entryPort = 0;
            if (entry.TryGetProperty("port", out var portInner))
            {
                if (portInner.ValueKind == JsonValueKind.String)
                    int.TryParse(portInner.GetString(), out entryPort);
                else if (portInner.ValueKind == JsonValueKind.Number)
                    entryPort = portInner.GetInt32();
            }
            allEntries.Add(new V2RayTransportEntry(entryPp, entryTp, entryTls, entryPort));
        }

        // The "best" entry (first after sorting) goes into the top-level fields for backward compat
        var best = allEntries[0];

        return new V2RayHandshakeResult(
            Uuid: clientUuid,
            ProxyProtocol: best.ProxyProtocol,
            Transport: best.Transport,
            Tls: best.Tls,
            Port: best.Port
        )
        {
            AllEntries = allEntries,
        };
    }

    /// <summary>
    /// Sort key for V2Ray transport selection — matches JS SDK's transportSortKey().
    /// Lower = more reliable. Based on observed success rates from 780-node test:
    /// tcp=100%, ws=100%, http=100%, gun=100%, kcp=100%, grpc/none=87%, quic=0%, grpc/tls=0%.
    /// </summary>
    private static int TransportSortKey(JsonElement entry)
    {
        var tp = entry.TryGetProperty("transport_protocol", out var tpEl) ? tpEl.GetInt32() : 7;
        var ts = entry.TryGetProperty("transport_security", out var tsEl) ? tsEl.GetInt32() : 0;

        return tp switch
        {
            7 => 0,  // tcp — most reliable
            8 => 1,  // websocket
            4 => 2,  // http
            2 => 3,  // gun
            5 => 4,  // kcp/mkcp
            3 when ts != 2 => 5,  // grpc/none
            3 when ts == 2 => 8,  // grpc/tls
            6 when ts == 2 => 9,  // quic/tls
            6 when ts != 2 => 10, // quic/none
            _ => 7,  // unknown
        };
    }

    /// <summary>
    /// Writes a <see cref="ulong"/> value in big-endian byte order.
    /// </summary>
    private static void WriteBigEndianUInt64(byte[] buffer, ulong value)
    {
        buffer[0] = (byte)(value >> 56);
        buffer[1] = (byte)(value >> 48);
        buffer[2] = (byte)(value >> 40);
        buffer[3] = (byte)(value >> 32);
        buffer[4] = (byte)(value >> 24);
        buffer[5] = (byte)(value >> 16);
        buffer[6] = (byte)(value >> 8);
        buffer[7] = (byte)value;
    }
}

// ─── Handshake Exception ───

/// <summary>
/// Thrown when a V3 handshake with a Sentinel node fails.
/// Inherits from <see cref="HandshakeException"/> in the unified error hierarchy.
/// </summary>
public class SentinelHandshakeException : HandshakeException
{
    /// <summary>Initializes a new instance with the specified message.</summary>
    public SentinelHandshakeException(string message)
        : base(ErrorCodes.HandshakeFailed, message) { }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    public SentinelHandshakeException(string message, Exception innerException)
        : base(ErrorCodes.HandshakeFailed, message, innerException) { }

    /// <summary>Initializes a new instance with a specific error code and message.</summary>
    public SentinelHandshakeException(string code, string message)
        : base(code, message) { }
}
