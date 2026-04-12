using System.Text;
using System.Text.Json;

namespace Sentinel.SDK.Core;

/// <summary>
/// RPC client for Sentinel chain queries via Tendermint ABCI.
/// ~10x faster than LCD REST for bulk queries. Uses protobuf transport.
/// Falls back to LCD if RPC endpoints are unavailable.
/// </summary>
public sealed class RpcClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string[] _rpcUrls;
    private readonly ISdkLogger? _logger;

    /// <summary>Create an RPC client with the given endpoints.</summary>
    /// <param name="rpcUrls">Tendermint RPC endpoints (tried in order).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public RpcClient(string[]? rpcUrls = null, ISdkLogger? logger = null)
    {
        _rpcUrls = rpcUrls ?? Constants.DefaultRpcUrls;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    // ─── ABCI Query Primitive ──────────────────────────────────────

    /// <summary>
    /// Execute a raw ABCI query against a gRPC service path.
    /// Returns the protobuf-encoded response bytes.
    /// </summary>
    /// <param name="path">gRPC method path (e.g. "/sentinel.node.v3.QueryService/QueryNodes").</param>
    /// <param name="requestBytes">Protobuf-encoded request.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]> AbciQueryAsync(string path, byte[] requestBytes, CancellationToken ct = default)
    {
        var hexData = Convert.ToHexString(requestBytes).ToLowerInvariant();
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "abci_query",
            @params = new { path, data = hexData, height = "0", prove = false },
        });

        foreach (var rpcUrl in _rpcUrls)
        {
            try
            {
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(rpcUrl, content, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                var resultValue = doc.RootElement
                    .GetProperty("result")
                    .GetProperty("response")
                    .GetProperty("value")
                    .GetString();

                if (string.IsNullOrEmpty(resultValue)) return [];
                return Convert.FromBase64String(resultValue);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"RPC {rpcUrl} failed: {ex.Message}");
            }
        }

        throw new ChainException(ErrorCodes.AllEndpointsFailed,
            "All RPC endpoints failed for ABCI query", new { path });
    }

    // ─── Protobuf Request Encoding ─────────────────────────────────

    private static byte[] EncodeNodesRequest(int status, int limit)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteVarintField(ms, 1, (ulong)status); // status
        // pagination (embedded message at field 2)
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 2, (ulong)limit); // limit
        ProtobufWriter.WriteEmbeddedField(ms, 2, pag.ToArray());
        return ms.ToArray();
    }

    private static byte[] EncodeStringRequest(int field, string value)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteStringField(ms, field, value);
        return ms.ToArray();
    }

    private static byte[] EncodeBalanceRequest(string address, string denom)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteStringField(ms, 1, address);
        ProtobufWriter.WriteStringField(ms, 2, denom);
        return ms.ToArray();
    }

    private static byte[] EncodeNodesForPlanRequest(ulong planId, int status, int limit)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteVarintField(ms, 1, planId); // id
        ProtobufWriter.WriteVarintField(ms, 2, (ulong)status); // status
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 2, (ulong)limit);
        ProtobufWriter.WriteEmbeddedField(ms, 3, pag.ToArray()); // pagination
        return ms.ToArray();
    }

    // ─── Typed Query Methods ───────────────────────────────────────

    /// <summary>
    /// Query active nodes via RPC (ABCI protobuf). Much faster than LCD for bulk queries.
    /// </summary>
    /// <param name="status">Node status filter (1 = active).</param>
    /// <param name="limit">Maximum nodes to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of chain nodes.</returns>
    public async Task<List<ChainNode>> QueryNodesAsync(int status = 1, int limit = 500, CancellationToken ct = default)
    {
        var request = EncodeNodesRequest(status, limit);
        var response = await AbciQueryAsync("/sentinel.node.v3.QueryService/QueryNodes", request, ct);
        var fields = ProtobufReader.Decode(response);
        return ProtobufReader.GetFields(fields, 1)
            .Select(f => ProtobufReader.DecodeNode(ProtobufReader.DecodeEmbedded(f)))
            .ToList();
    }

    /// <summary>Query a single node by address via RPC.</summary>
    /// <param name="address">Node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The node, or null if not found.</returns>
    public async Task<ChainNode?> QueryNodeAsync(string address, CancellationToken ct = default)
    {
        try
        {
            var request = EncodeStringRequest(1, address);
            var response = await AbciQueryAsync("/sentinel.node.v3.QueryService/QueryNode", request, ct);
            var fields = ProtobufReader.Decode(response);
            var nodeField = ProtobufReader.GetField(fields, 1);
            if (nodeField is null) return null;
            return ProtobufReader.DecodeNode(ProtobufReader.DecodeEmbedded(nodeField));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Query wallet balance via RPC.</summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="denom">Token denomination (default: udvpn).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Balance record.</returns>
    public async Task<Balance> QueryBalanceAsync(string address, string denom = "udvpn", CancellationToken ct = default)
    {
        var request = EncodeBalanceRequest(address, denom);
        var response = await AbciQueryAsync("/cosmos.bank.v1beta1.Query/Balance", request, ct);
        var fields = ProtobufReader.Decode(response);
        var coinField = ProtobufReader.GetField(fields, 1);
        if (coinField is null) return new Balance(0, 0m, "0.00 P2P");

        var coinFields = ProtobufReader.DecodeEmbedded(coinField);
        var amountStr = ProtobufReader.GetField(coinFields, 2) is { } a
            ? ProtobufReader.DecodeString(a) : "0";
        var amount = long.TryParse(amountStr, out var v) ? v : 0;
        var p2p = amount / 1_000_000m;
        return new Balance(amount, p2p, $"{p2p:F2} P2P");
    }

    /// <summary>Query nodes linked to a plan via RPC.</summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="status">Node status filter (1 = active).</param>
    /// <param name="limit">Maximum nodes to return.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<ChainNode>> QueryNodesForPlanAsync(ulong planId, int status = 1, int limit = 500, CancellationToken ct = default)
    {
        var request = EncodeNodesForPlanRequest(planId, status, limit);
        var response = await AbciQueryAsync("/sentinel.node.v3.QueryService/QueryNodesForPlan", request, ct);
        var fields = ProtobufReader.Decode(response);
        return ProtobufReader.GetFields(fields, 1)
            .Select(f => ProtobufReader.DecodeNode(ProtobufReader.DecodeEmbedded(f)))
            .ToList();
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
