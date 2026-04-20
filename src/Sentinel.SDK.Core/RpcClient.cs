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
        // pagination: cosmos.base.query.v1beta1.PageRequest (field 1=key, 2=offset, 3=limit)
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit); // field 3: limit (NOT field 2 which is offset)
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
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit); // field 3: limit
        ProtobufWriter.WriteEmbeddedField(ms, 3, pag.ToArray()); // pagination
        return ms.ToArray();
    }

    // ─── Tendermint TX Lookup ──────────────────────────────────────

    /// <summary>
    /// Broadcast a raw transaction via Tendermint RPC <c>broadcast_tx_sync</c>.
    /// Returns a parsed <see cref="TxResult"/> on success, or null if the RPC call fails
    /// or returns a non-zero response code that should fall back to LCD.
    /// </summary>
    /// <param name="txBytes">Serialized TxRaw bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed result, or null if RPC is unavailable or response is unrecognizable.</returns>
    public async Task<TxResult?> BroadcastTxAsync(byte[] txBytes, CancellationToken ct = default)
    {
        var base64Tx = Convert.ToBase64String(txBytes);
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "broadcast_tx_sync",
            @params = new { tx = base64Tx },
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

                // JSON-RPC 2.0 error envelope — skip to next RPC URL
                if (doc.RootElement.TryGetProperty("error", out _)) continue;
                if (!doc.RootElement.TryGetProperty("result", out var result)) continue;

                var code = result.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                var log = result.TryGetProperty("log", out var l) ? l.GetString() ?? "" : "";
                var hash = result.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";

                // Normalize hash to uppercase hex (RPC returns uppercase; LCD also uppercase)
                return new TxResult(hash.ToUpperInvariant(), code, log, code == 0);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"RPC broadcast_tx_sync {rpcUrl} failed: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Query a transaction by hash via Tendermint RPC (<c>tx</c> method) and return a parsed
    /// <see cref="TxResult"/>. Returns null if the TX is not yet indexed or RPC is unavailable.
    /// </summary>
    /// <param name="txHash">Transaction hash (hex, upper or lower case).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TxResult?> QueryTxAsync(string txHash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(txHash);
        var normalized = txHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? txHash : "0x" + txHash;
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tx",
            @params = new { hash = normalized, prove = false },
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

                if (doc.RootElement.TryGetProperty("error", out _)) continue;
                if (!doc.RootElement.TryGetProperty("result", out var result)) continue;
                if (!result.TryGetProperty("tx_result", out var txResult)) continue;

                var hash = result.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";
                var code = txResult.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                var log = txResult.TryGetProperty("log", out var l) ? l.GetString() ?? "" : "";

                return new TxResult(hash.ToUpperInvariant(), code, log, code == 0);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"RPC tx lookup {rpcUrl} failed: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Query a transaction by hash via Tendermint RPC (<c>tx</c> method) and return the
    /// <c>tx_result.events</c> array as JSON text. Events are already decoded by the node —
    /// no base64 unwrapping required. Returns null if the TX is not yet indexed.
    /// </summary>
    /// <param name="txHash">Transaction hash (hex, upper or lower case).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string?> QueryTxEventsJsonAsync(string txHash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(txHash);
        // Tendermint's `tx` method accepts a 0x-prefixed hex hash.
        var normalized = txHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? txHash : "0x" + txHash;
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tx",
            @params = new { hash = normalized, prove = false },
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

                // Tendermint returns an error payload when TX not indexed — keep trying the next RPC.
                if (!doc.RootElement.TryGetProperty("result", out var result)) continue;
                if (!result.TryGetProperty("tx_result", out var txResult)) continue;
                if (!txResult.TryGetProperty("events", out var events) ||
                    events.ValueKind != JsonValueKind.Array ||
                    events.GetArrayLength() == 0) continue;

                return events.GetRawText();
            }
            catch (Exception ex)
            {
                _logger?.Debug($"RPC tx lookup {rpcUrl} failed: {ex.Message}");
            }
        }

        return null;
    }

    // ─── Typed Query Methods ───────────────────────────────────────

    /// <summary>
    /// Query active nodes via RPC (ABCI protobuf). Much faster than LCD for bulk queries.
    /// </summary>
    /// <remarks>
    /// PAGINATION GOTCHA: Sentinel v3 QueryNodes truncates at <paramref name="limit"/>
    /// and does NOT emit pagination.next_key. A standard Cosmos
    /// "loop while next_key is non-empty" pattern terminates on the first call
    /// and silently loses data. Keep <paramref name="limit"/> above the chain's
    /// current active-node ceiling (~1048 as of 2026-04). Default raised to 10000.
    /// </remarks>
    /// <param name="status">Node status filter (1 = active).</param>
    /// <param name="limit">Maximum nodes to return. Default 10000 — see remarks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of chain nodes.</returns>
    public async Task<List<ChainNode>> QueryNodesAsync(int status = 1, int limit = 10000, CancellationToken ct = default)
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
    /// <remarks>
    /// PAGINATION GOTCHA: `QueryNodesForPlan` silently truncates at
    /// <paramref name="limit"/> with no next_key. Observed 2026-04: plan 36 has
    /// 803 active nodes but `limit=500` returns exactly 500 with no indication
    /// more exist. Default raised to 10000. If a plan grows beyond that, raise
    /// further — the chain's own ceiling is the effective limit.
    /// </remarks>
    /// <param name="planId">Plan ID.</param>
    /// <param name="status">Node status filter (1 = active).</param>
    /// <param name="limit">Maximum nodes to return. Default 10000 — see remarks.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<ChainNode>> QueryNodesForPlanAsync(ulong planId, int status = 1, int limit = 10000, CancellationToken ct = default)
    {
        var request = EncodeNodesForPlanRequest(planId, status, limit);
        var response = await AbciQueryAsync("/sentinel.node.v3.QueryService/QueryNodesForPlan", request, ct);
        var fields = ProtobufReader.Decode(response);
        return ProtobufReader.GetFields(fields, 1)
            .Select(f => ProtobufReader.DecodeNode(ProtobufReader.DecodeEmbedded(f)))
            .ToList();
    }

    /// <summary>Query sessions for an account via RPC (typed).</summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="limit">Maximum sessions to return.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<ChainSession>> QuerySessionsForAccountAsync(string address, int limit = 100, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteStringField(ms, 1, address);
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit);
        ProtobufWriter.WriteEmbeddedField(ms, 2, pag.ToArray());
        var response = await AbciQueryAsync("/sentinel.session.v3.QueryService/QuerySessionsForAccount", ms.ToArray(), ct);
        var fields = ProtobufReader.Decode(response);
        return ProtobufReader.GetFields(fields, 1)
            .Select(f => ProtobufReader.DecodeSession(ProtobufReader.DecodeEmbedded(f)))
            .ToList();
    }

    /// <summary>Query subscriptions for an account via RPC (typed).</summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="limit">Maximum subscriptions to return.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Subscription>> QuerySubscriptionsForAccountAsync(string address, int limit = 100, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteStringField(ms, 1, address);
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit);
        ProtobufWriter.WriteEmbeddedField(ms, 2, pag.ToArray());
        var response = await AbciQueryAsync("/sentinel.subscription.v3.QueryService/QuerySubscriptionsForAccount", ms.ToArray(), ct);
        var fields = ProtobufReader.Decode(response);
        return ProtobufReader.GetFields(fields, 1)
            .Select(f => ProtobufReader.DecodeSubscription(ProtobufReader.DecodeEmbedded(f)))
            .ToList();
    }

    /// <summary>Query a single plan by ID via RPC.</summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw protobuf plan bytes, or null if not found.</returns>
    public async Task<byte[]?> QueryPlanAsync(ulong planId, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            ProtobufWriter.WriteVarintField(ms, 1, planId);
            var response = await AbciQueryAsync("/sentinel.plan.v3.QueryService/QueryPlan", ms.ToArray(), ct);
            var fields = ProtobufReader.Decode(response);
            return ProtobufReader.GetField(fields, 1)?.Data.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Query a single session by ID via RPC.</summary>
    public async Task<ChainSession?> QuerySessionAsync(ulong sessionId, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            ProtobufWriter.WriteVarintField(ms, 1, sessionId);
            var response = await AbciQueryAsync("/sentinel.session.v3.QueryService/QuerySession", ms.ToArray(), ct);
            var fields = ProtobufReader.Decode(response);
            var sf = ProtobufReader.GetField(fields, 1);
            if (sf is null) return null;
            return ProtobufReader.DecodeSession(ProtobufReader.DecodeEmbedded(sf));
        }
        catch { return null; }
    }

    /// <summary>Query a single subscription by ID via RPC.</summary>
    public async Task<Subscription?> QuerySubscriptionAsync(ulong subscriptionId, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            ProtobufWriter.WriteVarintField(ms, 1, subscriptionId);
            var response = await AbciQueryAsync("/sentinel.subscription.v3.QueryService/QuerySubscription", ms.ToArray(), ct);
            var fields = ProtobufReader.Decode(response);
            var sf = ProtobufReader.GetField(fields, 1);
            if (sf is null) return null;
            return ProtobufReader.DecodeSubscription(ProtobufReader.DecodeEmbedded(sf));
        }
        catch { return null; }
    }

    /// <summary>Query a provider by address via RPC (v2 — provider not migrated to v3).</summary>
    public async Task<Provider?> QueryProviderAsync(string provAddress, CancellationToken ct = default)
    {
        try
        {
            var request = EncodeStringRequest(1, provAddress);
            var response = await AbciQueryAsync("/sentinel.provider.v2.QueryService/QueryProvider", request, ct);
            var fields = ProtobufReader.Decode(response);
            var pf = ProtobufReader.GetField(fields, 1);
            if (pf is null) return null;
            return ProtobufReader.DecodeProvider(ProtobufReader.DecodeEmbedded(pf));
        }
        catch { return null; }
    }

    /// <summary>Query fee grants received by a grantee via RPC.</summary>
    public async Task<List<FeeGrant>> QueryFeeGrantsAsync(string grantee, int limit = 100, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteStringField(ms, 1, grantee);
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit);
        ProtobufWriter.WriteEmbeddedField(ms, 2, pag.ToArray());
        var response = await AbciQueryAsync("/cosmos.feegrant.v1beta1.Query/Allowances", ms.ToArray(), ct);
        var fields = ProtobufReader.Decode(response);
        // Each allowance is field 1 (repeated Grant message: granter=1, grantee=2, allowance=3)
        return ProtobufReader.GetFields(fields, 1).Select(f =>
        {
            var gf = ProtobufReader.DecodeEmbedded(f);
            var granter = ProtobufReader.GetField(gf, 1) is { } g1 ? ProtobufReader.DecodeString(g1) : "";
            var granteeAddr = ProtobufReader.GetField(gf, 2) is { } g2 ? ProtobufReader.DecodeString(g2) : "";
            // Allowance is field 3 (Any-encoded) — pass raw bytes as string for LCD compat
            var allowance = ProtobufReader.GetField(gf, 3) is { } g3 ? (object)Convert.ToBase64String(g3.Data.ToArray()) : new object();
            return new FeeGrant(granter, granteeAddr, allowance);
        }).ToList();
    }

    /// <summary>Query fee grants issued by a granter via RPC.</summary>
    public async Task<List<FeeGrant>> QueryFeeGrantsIssuedAsync(string granter, int limit = 100, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteStringField(ms, 1, granter);
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit);
        ProtobufWriter.WriteEmbeddedField(ms, 2, pag.ToArray());
        var response = await AbciQueryAsync("/cosmos.feegrant.v1beta1.Query/AllowancesByGranter", ms.ToArray(), ct);
        var fields = ProtobufReader.Decode(response);
        return ProtobufReader.GetFields(fields, 1).Select(f =>
        {
            var gf = ProtobufReader.DecodeEmbedded(f);
            var granterAddr = ProtobufReader.GetField(gf, 1) is { } g1 ? ProtobufReader.DecodeString(g1) : "";
            var granteeAddr = ProtobufReader.GetField(gf, 2) is { } g2 ? ProtobufReader.DecodeString(g2) : "";
            var allowance = ProtobufReader.GetField(gf, 3) is { } g3 ? (object)Convert.ToBase64String(g3.Data.ToArray()) : new object();
            return new FeeGrant(granterAddr, granteeAddr, allowance);
        }).ToList();
    }

    /// <summary>Query authz grants between two addresses via RPC.</summary>
    public async Task<List<AuthzGrant>> QueryAuthzGrantsAsync(string granter, string grantee, int limit = 100, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteStringField(ms, 1, granter);
        ProtobufWriter.WriteStringField(ms, 2, grantee);
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit);
        ProtobufWriter.WriteEmbeddedField(ms, 3, pag.ToArray());
        var response = await AbciQueryAsync("/cosmos.authz.v1beta1.Query/Grants", ms.ToArray(), ct);
        var fields = ProtobufReader.Decode(response);
        // Field 1 = repeated GrantAuthorization: authorization=1(Any), expiration=2(Timestamp)
        return ProtobufReader.GetFields(fields, 1).Select(f =>
        {
            var gf = ProtobufReader.DecodeEmbedded(f);
            var msgTypeUrl = "";
            // authorization is field 1 (Any: type_url=1, value=2)
            if (ProtobufReader.GetField(gf, 1) is { } authField)
            {
                var authFields = ProtobufReader.DecodeEmbedded(authField);
                if (ProtobufReader.GetField(authFields, 1) is { } typeUrlField)
                    msgTypeUrl = ProtobufReader.DecodeString(typeUrlField);
                // For GenericAuthorization, the inner value has msg=1
                if (ProtobufReader.GetField(authFields, 2) is { } valueField)
                {
                    var innerFields = ProtobufReader.DecodeEmbedded(valueField);
                    if (ProtobufReader.GetField(innerFields, 1) is { } msgField)
                        msgTypeUrl = ProtobufReader.DecodeString(msgField);
                }
            }
            // expiration is field 2 (Timestamp: seconds=1, nanos=2) — just note presence
            string? expiration = null;
            if (ProtobufReader.GetField(gf, 2) is { } expField)
            {
                var expFields = ProtobufReader.DecodeEmbedded(expField);
                var seconds = ProtobufReader.GetField(expFields, 1)?.Varint ?? 0;
                if (seconds > 0)
                    expiration = DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToString("o");
            }
            return new AuthzGrant(granter, grantee, msgTypeUrl, expiration);
        }).ToList();
    }

    /// <summary>Query subscription allocations via RPC.</summary>
    public async Task<List<SubscriptionAllocation>> QuerySubscriptionAllocationsAsync(ulong subscriptionId, int limit = 100, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        ProtobufWriter.WriteVarintField(ms, 1, subscriptionId);
        using var pag = new MemoryStream();
        ProtobufWriter.WriteVarintField(pag, 3, (ulong)limit);
        ProtobufWriter.WriteEmbeddedField(ms, 2, pag.ToArray());
        // NOTE: v3 returns 501 for allocations, try v2 first
        try
        {
            var response = await AbciQueryAsync("/sentinel.subscription.v2.QueryService/QueryAllocations", ms.ToArray(), ct);
            var fields = ProtobufReader.Decode(response);
            return ProtobufReader.GetFields(fields, 1).Select(f =>
            {
                var af = ProtobufReader.DecodeEmbedded(f);
                var id = ProtobufReader.GetField(af, 1) is { } f1 ? ProtobufReader.DecodeString(f1) : "0";
                var address = ProtobufReader.GetField(af, 2) is { } f2 ? ProtobufReader.DecodeString(f2) : "";
                var granted = ProtobufReader.GetField(af, 3) is { } f3 ? ProtobufReader.DecodeString(f3) : "0";
                var utilised = ProtobufReader.GetField(af, 4) is { } f4 ? ProtobufReader.DecodeString(f4) : "0";
                return new SubscriptionAllocation(id, address, granted, utilised);
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Query session allocations via RPC (for session bandwidth tracking).</summary>
    public async Task<RawSessionAllocation?> QuerySessionAllocationAsync(ulong sessionId, CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            ProtobufWriter.WriteVarintField(ms, 1, sessionId);
            var response = await AbciQueryAsync("/sentinel.session.v3.QueryService/QueryAllocations", ms.ToArray(), ct);
            var fields = ProtobufReader.Decode(response);
            var allocs = ProtobufReader.GetFields(fields, 1);
            if (allocs.Count == 0) return null;
            var af = ProtobufReader.DecodeEmbedded(allocs[0]);
            var granted = ProtobufReader.GetField(af, 3) is { } f3 ? ProtobufReader.DecodeString(f3) : "0";
            var utilised = ProtobufReader.GetField(af, 4) is { } f4 ? ProtobufReader.DecodeString(f4) : "0";
            if (long.TryParse(granted, out var maxBytes) && long.TryParse(utilised, out var usedBytes))
                return new RawSessionAllocation(maxBytes, usedBytes);
            return null;
        }
        catch { return null; }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
