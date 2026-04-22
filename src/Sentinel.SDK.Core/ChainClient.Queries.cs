using System.Net.Http.Json;
using System.Text.Json;

namespace Sentinel.SDK.Core;

/// <summary>
/// ChainClient partial — balance, node, session, subscription, plan, provider,
/// pricing, cost estimation, and network overview queries.
/// </summary>
public sealed partial class ChainClient
{
    // ─── Public Query Methods ───

    /// <summary>
    /// Get the udvpn balance for an address.
    /// </summary>
    /// <param name="address">Bech32 account address (sent1...).</param>
    /// <returns>Balance with micro-denomination, decimal, and display values.</returns>
    public async Task<Balance> GetBalanceAsync(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        // RPC-first
        try
        {
            return await _rpcClient.QueryBalanceAsync(address, Constants.Denom, ct);
        }
        catch (Exception ex) { _logger?.Debug($"RPC GetBalance failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/cosmos/bank/v1beta1/balances/{address}/by_denom?denom={Constants.Denom}";
        var json = await LcdGetAsync(path, ct);

        long amount = 0;
        if (json.TryGetProperty("balance", out var balanceObj) &&
            balanceObj.TryGetProperty("amount", out var amountProp))
        {
            long.TryParse(amountProp.GetString(), out amount);
        }

        var p2p = amount / 1_000_000m;
        var display = $"{p2p:F2} P2P";

        return new Balance(amount, p2p, display);
    }

    /// <summary>
    /// Get active nodes registered on the chain.
    /// </summary>
    /// <param name="limit">Maximum number of nodes to return.</param>
    /// <returns>List of active chain nodes.</returns>
    public async Task<List<ChainNode>> GetActiveNodesAsync(int limit = 500, CancellationToken ct = default)
    {
        // RPC-first
        try
        {
            var rpcNodes = await _rpcClient.QueryNodesAsync(1, limit, ct);
            if (rpcNodes.Count > 0) return rpcNodes;
            _logger?.Debug("RPC returned no active nodes; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC GetActiveNodes failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/sentinel/node/v3/nodes?status=1&pagination.limit={limit}";
        var items = await LcdPaginatedAsync(path, "nodes", ct);
        return items.Select(ParseChainNode).ToList();
    }

    /// <summary>
    /// Get a single node by its sentnode address.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <returns>The node, or null if not found.</returns>
    public async Task<ChainNode?> GetNodeAsync(string nodeAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);

        // RPC-first
        try
        {
            return await _rpcClient.QueryNodeAsync(nodeAddress, ct);
        }
        catch (Exception ex) { _logger?.Debug($"RPC GetNode failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        try
        {
            var path = $"/sentinel/node/v3/nodes/{nodeAddress}";
            var json = await LcdGetAsync(path, ct);

            if (json.TryGetProperty("node", out var nodeObj))
            {
                return ParseChainNode(nodeObj);
            }

            return null;
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404")
        {
            return null;
        }
    }

    /// <summary>
    /// Get subscriptions for an account address.
    /// </summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <returns>List of subscriptions.</returns>
    public async Task<List<Subscription>> GetSubscriptionsAsync(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        // RPC-first
        try
        {
            var rpcSubs = await _rpcClient.QuerySubscriptionsForAccountAsync(address, ct: ct);
            if (rpcSubs.Count > 0) return rpcSubs;
            _logger?.Debug("RPC returned no subscriptions; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC GetSubscriptions failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/sentinel/subscription/v3/accounts/{address}/subscriptions";
        var items = await LcdPaginatedAsync(path, "subscriptions", ct);
        return items.Select(ParseSubscription).ToList();
    }

    /// <summary>
    /// Get sessions for an account address.
    /// </summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="status">Session status filter (1 = active).</param>
    /// <returns>List of sessions.</returns>
    public async Task<List<ChainSession>> GetSessionsAsync(string address, string status = "1", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        // RPC-first
        try
        {
            var sessions = await _rpcClient.QuerySessionsForAccountAsync(address, ct: ct);
            if (sessions.Count > 0)
            {
                // Filter by status if specified (RPC returns all statuses)
                var filtered = status == "1"
                    ? sessions.Where(s => s.Status == "active" || s.Status == "1").ToList()
                    : sessions;
                if (filtered.Count > 0) return filtered;
            }
            _logger?.Debug("RPC returned no sessions; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC GetSessions failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback — MUST use /accounts/{addr}/sessions path
        var path = $"/sentinel/session/v3/accounts/{address}/sessions?status={status}";
        var items = await LcdPaginatedAsync(path, "sessions", ct);
        return items.Select(ParseChainSession).ToList();
    }

    /// <summary>
    /// Get nodes assigned to a plan.
    /// Uses limit=5000 because Sentinel pagination is broken for plan nodes.
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <returns>List of nodes in the plan.</returns>
    public async Task<List<ChainNode>> GetPlanNodesAsync(int planId, CancellationToken ct = default)
    {
        // RPC-first
        try
        {
            var rpcNodes = await _rpcClient.QueryNodesForPlanAsync((ulong)planId, 1, 5000, ct);
            if (rpcNodes.Count > 0) return rpcNodes;
            _logger?.Debug("RPC returned no plan nodes; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC GetPlanNodes failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/sentinel/node/v3/plans/{planId}/nodes?pagination.limit=5000";
        var items = await LcdPaginatedAsync(path, "nodes", ct);
        return items.Select(ParseChainNode).ToList();
    }

    /// <summary>
    /// Discover subscription plans by probing IDs from 1 to maxId.
    /// </summary>
    /// <param name="maxId">Maximum plan ID to probe.</param>
    /// <returns>List of discovered plans that exist on chain.</returns>
    public async Task<List<DiscoveredPlan>> DiscoverPlansAsync(int maxId = 100, CancellationToken ct = default)
    {
        // NOTE: /sentinel/plan/v3/plans/{id} returns 501 Not Implemented on all LCD endpoints.
        // Instead, probe plan existence via subscriptions + nodes endpoints which DO work.
        // This matches the JS SDK's discoverPlans() strategy.
        var plans = new List<DiscoveredPlan>();
        var batchSize = 15;

        for (var batchStart = 1; batchStart <= maxId; batchStart += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchEnd = Math.Min(batchStart + batchSize - 1, maxId);
            var tasks = new List<Task<DiscoveredPlan?>>();

            for (var id = batchStart; id <= batchEnd; id++)
            {
                var planId = id;
                tasks.Add(ProbeSinglePlanAsync(planId, ct));
            }

            var results = await Task.WhenAll(tasks);
            foreach (var plan in results)
            {
                if (plan != null) plans.Add(plan);
            }
        }

        return plans;
    }

    /// <summary>Probe a single plan via subscriptions + nodes endpoints (plan detail endpoint is 501).</summary>
    private async Task<DiscoveredPlan?> ProbeSinglePlanAsync(int planId, CancellationToken ct)
    {
        try
        {
            // Check subscriber count via subscriptions endpoint (works, unlike /plans/{id})
            var subPath = $"/sentinel/subscription/v3/plans/{planId}/subscriptions?pagination.limit=1&pagination.count_total=true";
            var subJson = await LcdGetAsync(subPath, ct);
            var subscribers = 0;
            if (subJson.TryGetProperty("pagination", out var pagination) &&
                pagination.TryGetProperty("total", out var totalProp))
            {
                int.TryParse(totalProp.GetString() ?? "0", out subscribers);
            }

            // Extract price from the first subscription (if any)
            PriceEntry? price = null;
            if (subJson.TryGetProperty("subscriptions", out var subs) &&
                subs.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in subs.EnumerateArray())
                {
                    if (sub.TryGetProperty("price", out var priceProp))
                    {
                        price = ParsePriceEntry(priceProp);
                        break;
                    }
                }
            }

            // Check node count
            var nodesPath = $"/sentinel/node/v3/plans/{planId}/nodes?pagination.limit=1&pagination.count_total=true";
            var nodeCount = 0;
            try
            {
                var nodesJson = await LcdGetAsync(nodesPath, ct);
                if (nodesJson.TryGetProperty("pagination", out var nodePag) &&
                    nodePag.TryGetProperty("total", out var nodeTotalProp))
                {
                    int.TryParse(nodeTotalProp.GetString() ?? "0", out nodeCount);
                }
                else if (nodesJson.TryGetProperty("nodes", out var nodesArr) &&
                         nodesArr.ValueKind == JsonValueKind.Array)
                {
                    nodeCount = nodesArr.GetArrayLength();
                }
            }
            catch { /* node count query failure is non-fatal */ }

            // Skip empty plans (no subscribers AND no nodes)
            if (subscribers == 0 && nodeCount == 0) return null;

            return new DiscoveredPlan(planId, subscribers, nodeCount, price);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ─── Account Info (used by TransactionBuilder) ───

    /// <summary>
    /// Get account number and sequence for transaction signing.
    /// RPC-first (cosmos.auth.v1beta1.Query/Account) with LCD fallback.
    /// </summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <returns>Tuple of (accountNumber, sequence).</returns>
    internal async Task<(ulong AccountNumber, ulong Sequence)> GetAccountInfoAsync(string address, CancellationToken ct = default)
    {
        // RPC-first: faster, no LCD rate-limit exposure.
        try
        {
            var rpcResult = await _rpcClient.QueryAccountAsync(address, ct);
            if (rpcResult is not null)
                return rpcResult.Value;
            _logger?.Debug("RPC QueryAccount returned null; falling back to LCD.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC QueryAccount failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/cosmos/auth/v1beta1/accounts/{address}";
        var json = await LcdGetAsync(path, ct);

        if (!json.TryGetProperty("account", out var account))
        {
            throw new SentinelException("CLIENT_NO_ACCOUNT",
                $"Account not found on chain: {address}");
        }

        // Handle both direct accounts and vesting accounts (which nest the base account)
        var baseAccount = account;
        if (account.TryGetProperty("base_vesting_account", out var vesting) &&
            vesting.TryGetProperty("base_account", out var nested))
        {
            baseAccount = nested;
        }
        else if (account.TryGetProperty("base_account", out var direct))
        {
            baseAccount = direct;
        }

        ulong accountNumber = 0;
        ulong sequence = 0;

        if (baseAccount.TryGetProperty("account_number", out var accNum))
        {
            ulong.TryParse(accNum.GetString(), out accountNumber);
        }

        if (baseAccount.TryGetProperty("sequence", out var seq))
        {
            ulong.TryParse(seq.GetString(), out sequence);
        }

        return (accountNumber, sequence);
    }

    /// <summary>
    /// Broadcast a raw transaction to the chain. Tries Tendermint RPC
    /// <c>broadcast_tx_sync</c> first; falls back to LCD <c>/cosmos/tx/v1beta1/txs</c> on
    /// failure or unrecognizable RPC response.
    /// </summary>
    /// <param name="txBytes">Serialized TxRaw bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transaction result.</returns>
    internal async Task<TxResult> BroadcastTxAsync(byte[] txBytes, CancellationToken ct = default)
    {
        // RPC-first: broadcast_tx_sync is lower-latency than LCD and avoids REST overhead.
        try
        {
            var rpcResult = await _rpcClient.BroadcastTxAsync(txBytes, ct);
            if (rpcResult is not null) return rpcResult;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"RPC broadcast failed, falling back to LCD: {ex.Message}");
        }

        // LCD fallback
        var base64Tx = Convert.ToBase64String(txBytes);
        var payload = new { tx_bytes = base64Tx, mode = "BROADCAST_MODE_SYNC" };

        var path = "/cosmos/tx/v1beta1/txs";
        Exception? lastException = null;

        foreach (var baseUrl in _lcdUrls)
        {
            try
            {
                var url = baseUrl.TrimEnd('/') + path;
                var response = await _publicHttpClient.PostAsJsonAsync(url, payload, JsonOptions, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var json = doc.RootElement;

                if (json.TryGetProperty("tx_response", out var txResponse))
                {
                    var txHash = txResponse.TryGetProperty("txhash", out var h)
                        ? h.GetString() ?? "" : "";
                    var code = txResponse.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                    var rawLog = txResponse.TryGetProperty("raw_log", out var l)
                        ? l.GetString() ?? "" : "";

                    return new TxResult(txHash, code, rawLog, code == 0);
                }

                throw new SentinelException("CLIENT_TX_PARSE",
                    $"Unexpected broadcast response: {body}");
            }
            catch (SentinelException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new SentinelException("CLIENT_BROADCAST_FAILED",
            $"All RPC and LCD endpoints failed to broadcast: {lastException?.Message}", lastException!);
    }

    /// <summary>
    /// Query a transaction by hash. Tries Tendermint RPC <c>tx</c> method first; falls back
    /// to LCD <c>/cosmos/tx/v1beta1/txs/{hash}</c> if RPC returns nothing.
    /// </summary>
    /// <param name="txHash">Transaction hash (hex).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transaction result if found, or null if not found.</returns>
    internal async Task<TxResult?> QueryTxAsync(string txHash, CancellationToken ct = default)
    {
        // RPC-first: avoids LCD propagation lag for double-spend guard checks.
        try
        {
            var rpcResult = await _rpcClient.QueryTxAsync(txHash, ct);
            if (rpcResult is not null) return rpcResult;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"RPC QueryTx failed for {txHash}, falling back to LCD: {ex.Message}");
        }

        // LCD fallback
        try
        {
            var path = $"/cosmos/tx/v1beta1/txs/{txHash}";
            var json = await LcdGetAsync(path, ct);

            if (json.TryGetProperty("tx_response", out var txResponse))
            {
                var hash = txResponse.TryGetProperty("txhash", out var h)
                    ? h.GetString() ?? "" : "";
                var code = txResponse.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                var rawLog = txResponse.TryGetProperty("raw_log", out var l)
                    ? l.GetString() ?? "" : "";

                return new TxResult(hash, code, rawLog, code == 0);
            }

            return null;
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404")
        {
            return null;
        }
    }

    /// <summary>
    /// Query a TX by hash and return the top-level <c>events</c> array as JSON text.
    /// Modern Cosmos SDK emits events directly on <c>tx_response.events</c>; raw_log can be
    /// empty. This method returns whichever is populated, normalized to the array format
    /// expected by <see cref="EventParser.FindEvent"/>.
    /// </summary>
    private async Task<string?> QueryTxEventsJsonAsync(string txHash, CancellationToken ct)
    {
        // RPC-first: Tendermint tx method returns events directly, no LCD propagation lag.
        try
        {
            var rpcEvents = await _rpcClient.QueryTxEventsJsonAsync(txHash, ct);
            if (!string.IsNullOrEmpty(rpcEvents)) return rpcEvents;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"RPC tx events failed for {txHash}, falling back to LCD: {ex.Message}");
        }

        try
        {
            var path = $"/cosmos/tx/v1beta1/txs/{txHash}";
            var json = await LcdGetAsync(path, ct);
            if (!json.TryGetProperty("tx_response", out var txResponse)) return null;

            // Prefer tx_response.events (top-level, modern)
            if (txResponse.TryGetProperty("events", out var eventsTop) &&
                eventsTop.ValueKind == JsonValueKind.Array &&
                eventsTop.GetArrayLength() > 0)
            {
                return eventsTop.GetRawText();
            }

            // Fallback: raw_log is a JSON string containing log entries with events
            if (txResponse.TryGetProperty("raw_log", out var rl))
            {
                var raw = rl.GetString();
                if (string.IsNullOrEmpty(raw)) return null;
                try
                {
                    using var logDoc = JsonDocument.Parse(raw);
                    if (logDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var combined = new List<JsonElement>();
                        foreach (var entry in logDoc.RootElement.EnumerateArray())
                        {
                            if (entry.TryGetProperty("events", out var evs) &&
                                evs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var ev in evs.EnumerateArray()) combined.Add(ev);
                            }
                        }
                        if (combined.Count == 0) return null;
                        using var ms = new System.IO.MemoryStream();
                        using (var writer = new Utf8JsonWriter(ms))
                        {
                            writer.WriteStartArray();
                            foreach (var ev in combined) ev.WriteTo(writer);
                            writer.WriteEndArray();
                        }
                        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
                catch { /* raw_log not JSON — return as-is for regex fallback */ }
                return raw;
            }
            return null;
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404") { return null; }
    }

    /// <inheritdoc />
    public async Task<long?> ExtractPlanIdFromTxAsync(string txHash, int timeoutMs = 20000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(txHash);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var eventsJson = await QueryTxEventsJsonAsync(txHash, ct);
            if (!string.IsNullOrEmpty(eventsJson))
            {
                var id = EventParser.ExtractPlanIdFromEvents(eventsJson);
                if (id is > 0) return id;
            }
            await Task.Delay(2000, ct);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<long?> ExtractSubscriptionIdFromTxAsync(string txHash, int timeoutMs = 20000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(txHash);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var eventsJson = await QueryTxEventsJsonAsync(txHash, ct);
            if (!string.IsNullOrEmpty(eventsJson))
            {
                var id = EventParser.ExtractSubscriptionIdFromEvents(eventsJson);
                if (id is > 0) return id;
            }
            await Task.Delay(2000, ct);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<long?> ExtractSessionIdFromTxAsync(string txHash, int timeoutMs = 20000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(txHash);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var eventsJson = await QueryTxEventsJsonAsync(txHash, ct);
            if (!string.IsNullOrEmpty(eventsJson))
            {
                var id = EventParser.ExtractSessionId(eventsJson);
                if (id is > 0) return id;
            }
            await Task.Delay(2000, ct);
        }
        return null;
    }

    // ─── IChainClient: Session Queries ───

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActiveSession>> QueryActiveSessionsForAddressAsync(string walletAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(walletAddress);

        // RPC-first. Treat empty result as suspicious and fall through to LCD — a silent
        // empty return used to mask decoder bugs (see suggestions/2026-04-21-rpc-session-decoder-any-and-field-numbers.md).
        try
        {
            var rpcSessions = await _rpcClient.QuerySessionsForAccountAsync(walletAddress, ct: ct);
            if (rpcSessions.Count > 0)
            {
                var active = rpcSessions
                    .Where(s => s.Status == "active" || s.Status == "1")
                    .Select(s => new ActiveSession(
                        ulong.TryParse(s.Id, out var sid) ? sid : 0,
                        s.NodeAddress,
                        SessionStatus.Active))
                    .ToList();
                if (active.Count > 0) return active;
            }
            _logger?.Debug("RPC QueryActiveSessions returned no active sessions; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC QueryActiveSessions failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/sentinel/session/v3/accounts/{walletAddress}/sessions?status=1";
        var items = await LcdPaginatedAsync(path, "sessions", ct);
        var sessions = new List<ActiveSession>();

        foreach (var s in items)
        {
            var bs = s.TryGetProperty("base_session", out var baseEl) ? baseEl : s;
            var idStr = bs.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "0" : "0";
            var nodeAddr = bs.TryGetProperty("node_address", out var naEl) ? naEl.GetString() ?? "" : "";
            var statusStr = bs.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";

            if (ulong.TryParse(idStr, out var id))
            {
                var status = statusStr.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase)
                    ? SessionStatus.Active
                    : SessionStatus.Inactive;
                sessions.Add(new ActiveSession(id, nodeAddr, status));
            }
        }

        return sessions;
    }

    /// <inheritdoc />
    public async Task<RawSessionAllocation?> QuerySessionAllocationAsync(ulong sessionId, CancellationToken ct = default)
    {
        // RPC-first
        try
        {
            var result = await _rpcClient.QuerySessionAllocationAsync(sessionId, ct);
            if (result is not null) return result;
        }
        catch (Exception ex) { _logger?.Debug($"RPC QuerySessionAllocation failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/sentinel/session/v3/sessions/{sessionId}/allocations";
        JsonElement json;
        try
        {
            json = await LcdGetAsync(path, ct);
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404")
        {
            // Fresh session with no bandwidth reported yet (node hasn't called back to chain).
            // Treat as "allocation unknown" rather than failing session reuse — see
            // suggestions/2026-04-19-connect-widen-reuse-handshake-retry.md.
            return null;
        }

        if (json.TryGetProperty("allocations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var grantedStr = a.TryGetProperty("granted_bytes", out var gEl) ? gEl.GetString() ?? "0" : "0";
                var usedStr = a.TryGetProperty("utilised_bytes", out var uEl) ? uEl.GetString() ?? "0" : "0";

                if (long.TryParse(grantedStr, out var maxBytes) && long.TryParse(usedStr, out var usedBytes))
                {
                    return new RawSessionAllocation(maxBytes, usedBytes);
                }
            }
        }

        return null;
    }

    // ─── Subscription Allocation Queries ───

    /// <summary>
    /// Query all bandwidth allocations for a subscription.
    /// Used to verify that a user has been granted access to a plan subscription via sharing.
    /// NOTE: Uses v2 endpoint because v3 returns 501 Not Implemented.
    /// </summary>
    /// <param name="subscriptionId">Subscription ID on chain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of allocations for the subscription.</returns>
    public async Task<List<SubscriptionAllocation>> QuerySubscriptionAllocationsAsync(
        ulong subscriptionId, CancellationToken ct = default)
    {
        // RPC-first (v2 — v3 returns 501)
        try
        {
            var rpcAllocs = await _rpcClient.QuerySubscriptionAllocationsAsync(subscriptionId, ct: ct);
            if (rpcAllocs.Count > 0) return rpcAllocs;
            _logger?.Debug("RPC returned no subscription allocations; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC QuerySubAllocations failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback (v2 path — v3 returns 501)
        var path = $"/sentinel/subscription/v2/subscriptions/{subscriptionId}/allocations";
        var allocations = new List<SubscriptionAllocation>();

        try
        {
            var json = await LcdGetAsync(path, ct);

            if (json.TryGetProperty("allocations", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var idEl)
                        ? idEl.GetString() ?? idEl.ToString() : "0";
                    var address = a.TryGetProperty("address", out var addrEl)
                        ? addrEl.GetString() ?? "" : "";
                    var granted = a.TryGetProperty("granted_bytes", out var gEl)
                        ? gEl.GetString() ?? "0" : "0";
                    var utilised = a.TryGetProperty("utilised_bytes", out var uEl)
                        ? uEl.GetString() ?? "0" : "0";

                    allocations.Add(new SubscriptionAllocation(id, address, granted, utilised));
                }
            }
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404")
        {
            // No allocations found — return empty list
        }

        return allocations;
    }

    // ─── Additional Query Methods ───

    /// <summary>
    /// Query nodes assigned to a specific plan ID, using a single large request
    /// because Sentinel pagination is broken for plan node queries.
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <returns>List of nodes in the plan.</returns>
    public async Task<List<ChainNode>> QueryPlanNodesAsync(int planId, CancellationToken ct = default)
    {
        // RPC-first
        try
        {
            var rpcNodes = await _rpcClient.QueryNodesForPlanAsync((ulong)planId, 1, 5000, ct);
            if (rpcNodes.Count > 0) return rpcNodes;
            _logger?.Debug("RPC returned no plan nodes; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC QueryPlanNodes failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/sentinel/node/v3/plans/{planId}/nodes?pagination.limit=5000";
        var items = await LcdPaginatedAsync(path, "nodes", ct);
        return items.Select(ParseChainNode).ToList();
    }

    /// <summary>
    /// Check whether an address has an active subscription for a given plan.
    /// </summary>
    /// <param name="address">Account address (sent1...).</param>
    /// <param name="planId">Plan ID to check.</param>
    /// <returns>True if an active subscription exists for this plan.</returns>
    public async Task<bool> HasActiveSubscriptionAsync(string address, int planId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        var subscriptions = await GetSubscriptionsAsync(address, ct);

        return subscriptions.Any(sub =>
            sub.PlanId == planId.ToString() &&
            sub.Status.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Subscription-Filtered Node List ───

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChainNode>> GetAvailableNodesAsync(string walletAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(walletAddress);

        // 1. Query wallet's active subscriptions
        var subscriptions = await GetSubscriptionsAsync(walletAddress, ct);
        ct.ThrowIfCancellationRequested();

        // 2. Extract plan IDs from active subscriptions
        var planIds = new HashSet<int>();
        foreach (var sub in subscriptions)
        {
            if (!sub.Status.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(sub.PlanId, out var planId) && planId > 0)
            {
                planIds.Add(planId);
            }
        }

        if (planIds.Count == 0)
        {
            return Array.Empty<ChainNode>();
        }

        // 3. For each plan, query plan nodes (uses limit=5000)
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ChainNode>();

        foreach (var planId in planIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var planNodes = await GetPlanNodesAsync(planId, ct);
                foreach (var node in planNodes)
                {
                    // 4. Deduplicate by node address
                    if (seenAddresses.Add(node.Address))
                    {
                        result.Add(node);
                    }
                }
            }
            catch
            {
                // Plan query failure is non-fatal — continue with other plans
            }
        }

        return result;
    }

    // ─── Provider ───

    /// <summary>
    /// Get a provider by its sentprov address.
    /// </summary>
    /// <param name="provAddress">Provider address (sentprov1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The provider, or null if not found.</returns>
    public async Task<Provider?> GetProviderByAddressAsync(string provAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provAddress);

        // RPC-first (v2 — provider not migrated to v3)
        try
        {
            var result = await _rpcClient.QueryProviderAsync(provAddress, ct);
            if (result is not null) return result;
        }
        catch (Exception ex) { _logger?.Debug($"RPC GetProvider failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        try
        {
            var path = $"/sentinel/provider/v2/providers/{provAddress}";
            var json = await LcdGetAsync(path, ct);

            if (json.TryGetProperty("provider", out var provObj))
            {
                return ParseProvider(provObj);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404")
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Query Helpers ───

    /// <summary>
    /// Get a single subscription by ID.
    /// </summary>
    /// <param name="id">Subscription ID on chain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subscription, or null if not found.</returns>
    public async Task<Subscription?> GetSubscriptionAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        // RPC-first
        if (ulong.TryParse(id, out var subId))
        {
            try
            {
                return await _rpcClient.QuerySubscriptionAsync(subId, ct);
            }
            catch (Exception ex) { _logger?.Debug($"RPC GetSubscription failed, falling back to LCD: {ex.Message}"); }
        }

        // LCD fallback
        try
        {
            var path = $"/sentinel/subscription/v3/subscriptions/{id}";
            var json = await LcdGetAsync(path, ct);

            if (json.TryGetProperty("subscription", out var subObj))
            {
                return ParseSubscription(subObj);
            }

            return null;
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404")
        {
            return null;
        }
    }

    /// <summary>
    /// Query all subscribers of a plan.
    /// RPC-first (verified wire format 2026-04-21); falls through to LCD on empty or exception.
    /// Optionally exclude an address (e.g. the plan owner) from the results.
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="excludeAddress">Optional address to exclude from results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of plan subscribers.</returns>
    public async Task<IReadOnlyList<PlanSubscriber>> QueryPlanSubscribersAsync(
        int planId,
        string? excludeAddress = null,
        CancellationToken ct = default)
    {
        // RPC-first (QuerySubscriptionsForPlan — direct PlanSubscription, not Any-wrapped).
        try
        {
            var rpcSubs = await _rpcClient.QuerySubscriptionsForPlanAsync((ulong)planId, ct: ct);
            if (rpcSubs.Count > 0)
            {
                var filtered = excludeAddress is null
                    ? rpcSubs
                    : rpcSubs.Where(s => !string.Equals(s.Address, excludeAddress, StringComparison.OrdinalIgnoreCase)).ToList();
                return filtered;
            }
            _logger?.Debug("RPC QueryPlanSubscribers returned no results; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC QueryPlanSubscribers failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/sentinel/subscription/v3/plans/{planId}/subscriptions";
        var items = await LcdPaginatedAsync(path, "subscriptions", ct);

        var subscribers = new List<PlanSubscriber>();

        foreach (var item in items)
        {
            var address = item.TryGetProperty("acc_address", out var a) ? a.GetString() ?? ""
                : item.TryGetProperty("address", out var a2) ? a2.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(address) && item.TryGetProperty("subscriber", out var sub))
            {
                address = sub.GetString() ?? "";
            }

            var statusStr = item.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var status = statusStr.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            var subId = item.TryGetProperty("id", out var i) ? i.GetString() ?? i.ToString() : "";
            if (string.IsNullOrEmpty(subId) && item.TryGetProperty("base_id", out var bi))
            {
                subId = bi.GetString() ?? bi.ToString();
            }

            if (!string.IsNullOrEmpty(excludeAddress) &&
                string.Equals(address, excludeAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            subscribers.Add(new PlanSubscriber(address, status, subId));
        }

        return subscribers;
    }

    /// <summary>
    /// Get plan statistics with the owner filtered from subscriber counts.
    /// </summary>
    /// <param name="planId">Plan ID.</param>
    /// <param name="ownerAddress">Plan owner's sent1... address (filtered from counts).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Plan statistics.</returns>
    public async Task<PlanStats> GetPlanStatsAsync(int planId, string ownerAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerAddress);

        var allSubscribers = await QueryPlanSubscribersAsync(planId, ct: ct);
        var ownerSubscribed = allSubscribers.Any(s =>
            string.Equals(s.Address, ownerAddress, StringComparison.OrdinalIgnoreCase));
        var filtered = allSubscribers.Where(s =>
            !string.Equals(s.Address, ownerAddress, StringComparison.OrdinalIgnoreCase)).ToList();

        return new PlanStats(filtered.Count, allSubscribers.Count, ownerSubscribed);
    }

    // ─── Cost Estimation ───

    /// <summary>
    /// Estimate the cost of starting a session with a node.
    /// Uses the node's udvpn gigabyte price to calculate bandwidth cost,
    /// plus an estimated gas cost of ~200,000 gas units.
    /// When preferHourly is true and the node has cheaper hourly pricing,
    /// returns the hourly cost instead.
    /// </summary>
    /// <param name="node">Node with pricing information.</param>
    /// <param name="gigabytes">Number of gigabytes to purchase (default: 1).</param>
    /// <param name="preferHourly">When true, use hourly pricing if cheaper (default: false).</param>
    /// <param name="hours">Number of hours for hourly pricing (default: 1).</param>
    /// <returns>Estimated session cost breakdown.</returns>
    public static SessionCost EstimateSessionCost(
        ChainNode node, int gigabytes = 1, bool preferHourly = false, int hours = 1)
    {
        ArgumentNullException.ThrowIfNull(node);

        var gbEntry = node.GigabytePrices.FirstOrDefault(p => p.Denom == Constants.Denom);
        var perGb = 0L;
        if (gbEntry != null)
        {
            if (!long.TryParse(gbEntry.QuoteValue, out perGb))
            {
                long.TryParse(gbEntry.BaseValue, out perGb);
            }
        }

        var hrEntry = node.HourlyPrices.FirstOrDefault(p => p.Denom == Constants.Denom);
        var perHour = 0L;
        if (hrEntry != null)
        {
            if (!long.TryParse(hrEntry.QuoteValue, out perHour))
            {
                long.TryParse(hrEntry.BaseValue, out perHour);
            }
        }

        var gbCost = perGb * gigabytes;
        var hrCost = perHour * hours;
        var useHourly = preferHourly && hrEntry != null && hrCost < gbCost;
        var sessionCost = useHourly ? hrCost : gbCost;

        const long gasEstimate = 200_000; // ~200k gas per MsgStartSession
        var p2p = sessionCost / 1_000_000m;

        return new SessionCost(sessionCost, p2p, gasEstimate, sessionCost + gasEstimate);
    }

    /// <summary>
    /// Estimate the gas fee for a batch of messages.
    /// Gas per message varies by type: startSession=200k, feeGrant=150k, send=80k, link=150k.
    /// Fee amount uses the chain gas price of 0.2 udvpn per gas unit.
    /// </summary>
    /// <param name="msgCount">Number of messages in the batch.</param>
    /// <param name="msgType">Message type hint: "startSession", "feeGrant", "send", or "link".</param>
    /// <returns>Estimated batch fee breakdown.</returns>
    public static BatchFee EstimateBatchFee(int msgCount, string msgType = "startSession")
    {
        var gasPerMsg = msgType switch
        {
            "feeGrant" => 150_000L,
            "send" => 80_000L,
            "link" => 150_000L,
            _ => 200_000L, // startSession and default
        };

        var gas = gasPerMsg * msgCount;
        var amount = (long)Math.Ceiling(gas * 0.2); // GAS_PRICE = 0.2 udvpn

        return new BatchFee(gas, amount, gas.ToString(), amount.ToString());
    }

    // ─── Pricing ───

    /// <summary>
    /// Get standardized prices for a node — abstracts LCD price parsing entirely.
    /// Queries the node by address and returns formatted gigabyte and hourly prices.
    /// Matches the JS SDK's <c>getNodePrices()</c> behavior.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Formatted node prices with P2P and udvpn values.</returns>
    /// <exception cref="SentinelException">Thrown when the node is not found or query fails.</exception>
    public async Task<NodePrices> GetNodePricesAsync(string nodeAddress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        var node = await GetNodeAsync(nodeAddress, ct)
            ?? throw new SentinelException("NODE_NOT_FOUND",
                $"Node not found on chain: {nodeAddress}");

        return new NodePrices(
            Gigabyte: ExtractPrice(node.GigabytePrices),
            Hourly: ExtractPrice(node.HourlyPrices),
            Denom: "P2P",
            NodeAddress: nodeAddress
        );
    }

    /// <summary>
    /// Extract the udvpn price from a price entry array.
    /// Defensive fallback chain: QuoteValue (V3 current) -> BaseValue -> "0".
    /// </summary>
    private static PriceDetail ExtractPrice(PriceEntry[]? prices)
    {
        if (prices is null || prices.Length == 0)
            return new PriceDetail(0, 0m, null);

        var entry = Array.Find(prices, p =>
            string.Equals(p.Denom, Constants.Denom, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            return new PriceDetail(0, 0m, null);

        // Defensive fallback chain matching JS SDK: quote_value -> base_value -> "0"
        var rawVal = !string.IsNullOrEmpty(entry.QuoteValue) ? entry.QuoteValue
            : !string.IsNullOrEmpty(entry.BaseValue) ? entry.BaseValue
            : "0";

        if (!long.TryParse(rawVal, out var udvpn))
            udvpn = 0;

        var p2p = Math.Round(udvpn / 1_000_000m, 6);
        return new PriceDetail(udvpn, p2p, entry);
    }

    // ─── Authz Grants ───

    /// <summary>
    /// Query authz grants between two addresses.
    /// Uses the Cosmos authz module LCD endpoint.
    /// </summary>
    /// <param name="granter">Granter address (sent1...).</param>
    /// <param name="grantee">Grantee address (sent1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of authz grants from granter to grantee.</returns>
    public async Task<IReadOnlyList<AuthzGrant>> QueryAuthzGrantsAsync(
        string granter, string grantee, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(granter);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee);

        // RPC-first
        try
        {
            var rpcGrants = await _rpcClient.QueryAuthzGrantsAsync(granter, grantee, ct: ct);
            if (rpcGrants.Count > 0) return rpcGrants;
            _logger?.Debug("RPC returned no authz grants; falling back to LCD to verify.");
        }
        catch (Exception ex) { _logger?.Debug($"RPC QueryAuthzGrants failed, falling back to LCD: {ex.Message}"); }

        // LCD fallback
        var path = $"/cosmos/authz/v1beta1/grants?granter={granter}&grantee={grantee}";
        var grants = new List<AuthzGrant>();

        try
        {
            var json = await LcdGetAsync(path, ct);

            if (json.TryGetProperty("grants", out var grantsArray) &&
                grantsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in grantsArray.EnumerateArray())
                {
                    string msgTypeUrl = "";
                    string? expiration = null;

                    if (g.TryGetProperty("authorization", out var auth))
                    {
                        if (auth.TryGetProperty("@type", out var typeProp))
                            msgTypeUrl = typeProp.GetString() ?? "";

                        if (auth.TryGetProperty("msg", out var msgProp))
                            msgTypeUrl = msgProp.GetString() ?? msgTypeUrl;
                    }

                    if (g.TryGetProperty("expiration", out var exp) &&
                        exp.ValueKind == JsonValueKind.String)
                    {
                        expiration = exp.GetString();
                    }

                    grants.Add(new AuthzGrant(granter, grantee, msgTypeUrl, expiration));
                }
            }
        }
        catch (SentinelException ex) when (ex.Code == "CLIENT_HTTP_404")
        {
            // No grants found — return empty list
        }

        return grants;
    }

    // ─── Network Overview ───

    /// <summary>
    /// Get a high-level overview of the Sentinel network.
    /// Fetches all active nodes and aggregates by country and average GB price.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Network overview with total nodes, by-country breakdown, and average GB price.</returns>
    public async Task<NetworkOverview> GetNetworkOverviewAsync(CancellationToken ct = default)
    {
        var nodes = await GetActiveNodesAsync(limit: 5000, ct);
        var byCountry = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var prices = new List<long>();

        foreach (var node in nodes)
        {
            // Best-effort country extraction from remote URL hostname
            // Nodes often have hostnames like "de.node.example.com" or contain country codes
            var country = "UNKNOWN";
            if (!string.IsNullOrEmpty(node.RemoteUrl))
            {
                try
                {
                    var uri = new Uri(node.RemoteUrl);
                    var host = uri.Host;
                    // Try to extract 2-letter TLD or subdomain as country hint
                    var parts = host.Split('.');
                    if (parts.Length >= 2)
                    {
                        var tld = parts[^1].ToUpperInvariant();
                        if (tld.Length == 2 && tld != "CO" && tld != "IO")
                            country = tld;
                    }
                }
                catch
                {
                    // Malformed URL — leave as UNKNOWN
                }
            }

            byCountry.TryGetValue(country, out var count);
            byCountry[country] = count + 1;

            // Extract udvpn GB price for averaging
            var gbPrice = node.GigabytePrices
                .FirstOrDefault(p => p.Denom == Constants.Denom);
            if (gbPrice != null &&
                long.TryParse(
                    gbPrice.BaseValue.Split('.')[0],
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var priceVal))
            {
                prices.Add(priceVal);
            }
        }

        var avgGbPrice = prices.Count > 0
            ? (decimal)prices.Average() / 1_000_000m
            : 0m;

        return new NetworkOverview(nodes.Count, byCountry, Math.Round(avgGbPrice, 2));
    }

    // ─── Internal: JSON Parsing Helpers ───

    private static ChainNode ParseChainNode(JsonElement json)
    {
        var address = json.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";

        // Parse remote_addrs (array) and remote_url (string)
        var addrList = new List<string>();
        if (json.TryGetProperty("remote_addrs", out var addrsProp) && addrsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in addrsProp.EnumerateArray())
            {
                var v = item.GetString();
                if (!string.IsNullOrEmpty(v)) addrList.Add(v);
            }
        }
        if (json.TryGetProperty("remote_url", out var remoteUrlProp) &&
            remoteUrlProp.ValueKind == JsonValueKind.String)
        {
            var url = remoteUrlProp.GetString();
            if (!string.IsNullOrEmpty(url) && !addrList.Contains(url)) addrList.Add(url);
        }
        var remoteAddrs = addrList.ToArray();

        // Construct remote URL: prefer remote_url, fall back to https://{remote_addrs[0]}
        string? remoteUrl = null;
        if (json.TryGetProperty("remote_url", out var ruProp) && ruProp.ValueKind == JsonValueKind.String)
            remoteUrl = ruProp.GetString();
        if (string.IsNullOrEmpty(remoteUrl) && remoteAddrs.Length > 0)
            remoteUrl = remoteAddrs[0].StartsWith("http") ? remoteAddrs[0] : $"https://{remoteAddrs[0]}";

        var gbPrices = ParsePriceArray(json, "gigabyte_prices");
        var hrPrices = ParsePriceArray(json, "hourly_prices");

        var status = 0;
        if (json.TryGetProperty("status", out var statusProp))
        {
            if (statusProp.ValueKind == JsonValueKind.String)
            {
                var statusStr = statusProp.GetString() ?? "";
                status = statusStr.Contains("ACTIVE") ? 1 : statusStr.Contains("INACTIVE") ? 2 : 0;
            }
            else if (statusProp.ValueKind == JsonValueKind.Number)
            {
                status = statusProp.GetInt32();
            }
        }

        return new ChainNode(address, remoteAddrs, remoteUrl, gbPrices, hrPrices, status);
    }

    private static PriceEntry[] ParsePriceArray(JsonElement json, string key)
    {
        if (!json.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PriceEntry>();
        }

        return arr.EnumerateArray().Select(ParsePriceEntry).ToArray();
    }

    private static PriceEntry ParsePriceEntry(JsonElement json)
    {
        var denom = json.TryGetProperty("denom", out var d) ? d.GetString() ?? "" : "";

        // Chain Price type has fields: denom, base_value (sdk.Dec), quote_value (sdk.Int).
        // Defensive fallback chain: base_value (v3 current) → amount (legacy Coin field).
        var baseValue = json.TryGetProperty("base_value", out var bv) ? bv.GetString() ?? "0" : "";
        if (string.IsNullOrEmpty(baseValue))
        {
            baseValue = json.TryGetProperty("amount", out var amt) ? amt.GetString() ?? "0" : "0";
        }

        // quote_value is the integer denomination amount — fallback to base_value if missing.
        var quoteValue = json.TryGetProperty("quote_value", out var q) ? q.GetString() ?? baseValue : baseValue;
        return new PriceEntry(denom, baseValue, quoteValue);
    }

    private static Subscription ParseSubscription(JsonElement json)
    {
        var id = json.TryGetProperty("id", out var i) ? i.GetString() ?? i.ToString() : "0";
        var accAddress = json.TryGetProperty("acc_address", out var a) ? a.GetString() ?? ""
            : json.TryGetProperty("address", out var a2) ? a2.GetString() ?? "" : "";
        var planId = json.TryGetProperty("plan_id", out var p) ? p.GetString() ?? p.ToString() : "0";

        PriceEntry? price = null;
        if (json.TryGetProperty("price", out var priceObj) && priceObj.ValueKind == JsonValueKind.Object)
        {
            price = ParsePriceEntry(priceObj);
        }

        var status = json.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        var startAt = json.TryGetProperty("start_at", out var sa) ? sa.GetString() ?? "" : "";
        var inactiveAt = json.TryGetProperty("inactive_at", out var ia) ? ia.GetString() ?? "" : "";

        return new Subscription(id, accAddress, planId, price, status, startAt, inactiveAt);
    }

    private static ChainSession ParseChainSession(JsonElement json)
    {
        // v3 sessions wrap fields in base_session
        var bs = json.TryGetProperty("base_session", out var baseSession) ? baseSession : json;

        var id = bs.TryGetProperty("id", out var i) ? i.GetString() ?? i.ToString() : "0";
        var accAddress = bs.TryGetProperty("acc_address", out var a) ? a.GetString() ?? ""
            : bs.TryGetProperty("address", out var a2) ? a2.GetString() ?? "" : "";
        var nodeAddress = bs.TryGetProperty("node_address", out var n) ? n.GetString() ?? "" : "";
        var download = bs.TryGetProperty("download_bytes", out var dl) ? dl.GetString() ?? "0" : "0";
        var upload = bs.TryGetProperty("upload_bytes", out var ul) ? ul.GetString() ?? "0" : "0";
        if (download == "0" && bs.TryGetProperty("bandwidth", out var bw))
        {
            if (bw.TryGetProperty("download", out var dl2)) download = dl2.GetString() ?? "0";
            if (bw.TryGetProperty("upload", out var ul2)) upload = ul2.GetString() ?? "0";
        }
        var maxBytes = bs.TryGetProperty("max_bytes", out var mb) ? mb.GetString() ?? "0" : "0";
        var duration = bs.TryGetProperty("duration", out var dur) ? dur.GetString() : null;
        var maxDuration = bs.TryGetProperty("max_duration", out var maxDur) ? maxDur.GetString() : null;
        var status = bs.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        var inactiveAt = bs.TryGetProperty("inactive_at", out var ia) ? ia.GetString() : null;
        var startAt = bs.TryGetProperty("start_at", out var sa) ? sa.GetString() : null;

        return new ChainSession(id, accAddress, nodeAddress, download, upload, maxBytes, duration, maxDuration, status, inactiveAt, startAt);
    }

    // ─── Internal: Provider Parsing ───

    private static Provider ParseProvider(JsonElement json)
    {
        var address = json.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
        var name = json.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var identity = json.TryGetProperty("identity", out var i) ? i.GetString() ?? "" : "";
        var website = json.TryGetProperty("website", out var w) ? w.GetString() ?? "" : "";
        var description = json.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

        var status = 0;
        if (json.TryGetProperty("status", out var statusProp))
        {
            if (statusProp.ValueKind == JsonValueKind.String)
            {
                var statusStr = statusProp.GetString() ?? "";
                status = statusStr.Contains("ACTIVE") ? 1 : statusStr.Contains("INACTIVE") ? 2 : 0;
            }
            else if (statusProp.ValueKind == JsonValueKind.Number)
            {
                status = statusProp.GetInt32();
            }
        }

        return new Provider(address, name, identity, website, description, status);
    }
}
