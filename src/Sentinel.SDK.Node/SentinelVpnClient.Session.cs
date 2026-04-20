using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Session Creation & Helpers ─────────────────────────────────────────────

public partial class SentinelVpnClient
{
    /// <summary>
    /// Create a new on-chain session with the given node.
    /// Supports both pay-per-GB and pay-per-hour pricing.
    /// When <see cref="SentinelVpnOptions.PreferHourly"/> is true and the node's
    /// hourly price is cheaper than the gigabyte price, uses hourly session.
    /// Broadcasts the session TX and extracts the resulting session ID.
    /// </summary>
    private async Task<ulong> CreateNewSessionAsync(
        ChainNode chainNode, string nodeAddress, CancellationToken ct)
    {
        EmitProgress("subscribe", "Creating on-chain session...");

        // Determine max price from node's gigabyte prices
        PriceEntry? gbPrice = null;
        foreach (var price in chainNode.GigabytePrices)
        {
            if (price.Denom == Constants.Denom)
            {
                gbPrice = price;
                break;
            }
        }

        // Determine hourly price if available
        PriceEntry? hrPrice = null;
        foreach (var price in chainNode.HourlyPrices)
        {
            if (price.Denom == Constants.Denom)
            {
                hrPrice = price;
                break;
            }
        }

        // Determine pricing model: explicit Hours > PreferHourly > default GB
        long gigabytes = _options.Gigabytes;
        long hours = 0;
        PriceEntry? maxPrice = gbPrice;

        if (_options.Hours > 0)
        {
            // Explicit hours requested — use hourly pricing
            if (hrPrice == null)
                throw new SentinelNodeException($"Node {nodeAddress} has no hourly pricing — cannot use hours-based session");
            gigabytes = 0;
            hours = _options.Hours;
            maxPrice = hrPrice;
        }
        else if (_options.PreferHourly && hrPrice != null)
        {
            // PreferHourly = use hourly pricing if the node offers it.
            // No cross-unit comparison (GB vs hour prices are different units).
            gigabytes = 0;
            hours = 1;
            maxPrice = hrPrice;
        }

        var pricingMode = hours > 0 ? "hourly" : "per-GB";
        EmitProgress("subscribe", $"Broadcasting session TX ({pricingMode})...");

        var sessionMsg = MessageBuilder.StartSession(
            _wallet.Address,
            nodeAddress,
            gigabytes,
            maxPrice,
            hours
        );
        var txResult = await _txBuilder.BroadcastAsync(sessionMsg);
        ct.ThrowIfCancellationRequested();

        // ─── Code 105: NODE_INACTIVE retry ───
        // Ported from js-sdk broadcastWithInactiveRetry():
        // LCD may show node as active but the chain disagrees (propagation lag).
        // Wait 15s for LCD to sync, then retry once.
        if (!txResult.Success && txResult.Code == 105)
        {
            _logger.Warn("Node inactive on chain (code 105) — waiting 15s for LCD sync...");
            EmitProgress("subscribe", "Node inactive on chain — retrying in 15s...");
            await Task.Delay(15_000, ct);
            txResult = await _txBuilder.BroadcastAsync(sessionMsg);
            ct.ThrowIfCancellationRequested();

            if (!txResult.Success && txResult.Code == 105)
            {
                throw new SentinelException(
                    ErrorCodes.NodeInactive,
                    $"Node {nodeAddress} is inactive on chain after retry (code 105): {txResult.RawLog}"
                );
            }
        }

        if (!txResult.Success)
        {
            throw new SentinelException(
                ErrorCodes.TxFailed,
                $"Session TX failed (code {txResult.Code}): {txResult.RawLog}"
            );
        }

        EmitProgress("subscribe", $"TX broadcast: {txResult.TxHash}");

        // Wait for chain propagation before querying session
        EmitProgress("propagation", "Waiting for chain propagation (5s)...");
        await Task.Delay(CHAIN_PROPAGATION_DELAY_MS, ct);

        var sessionId = await ExtractSessionId(txResult, ct);
        EmitProgress("subscribe", $"Session ID: {sessionId}");

        return sessionId;
    }

    /// <summary>
    /// Extract the session ID from a broadcast TX result by reading its events directly
    /// (deterministic — no dependency on LCD/RPC active-session propagation). Falls back to
    /// polling active sessions only if event extraction fails.
    /// </summary>
    /// <param name="txResult">The broadcast result (TX hash + raw log).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The on-chain session ID.</returns>
    private async Task<ulong> ExtractSessionId(TxResult txResult, CancellationToken ct = default)
    {
        // Primary: parse session ID directly from TX events (deterministic, no LCD lag).
        var idFromEvents = await _chainClient.ExtractSessionIdFromTxAsync(txResult.TxHash, timeoutMs: 20000, ct);
        if (idFromEvents is > 0) return (ulong)idFromEvents.Value;

        // Fallback: poll active sessions for the wallet (covers rare cases where the TX
        // indexer lags behind the session store).
        IReadOnlyList<ActiveSession> sessions = [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            sessions = await _chainClient.QueryActiveSessionsForAddressAsync(_wallet.Address, ct);
            if (sessions.Count > 0) break;

            if (attempt < 2)
            {
                var delay = (attempt + 1) * 5;
                _logger?.Debug($"No sessions found (attempt {attempt + 1}/3), retrying in {delay}s...");
                EmitProgress("propagation", $"Session not yet indexed, retrying in {delay}s...");
                await Task.Delay(delay * 1000, ct);
            }
        }

        if (sessions.Count == 0)
        {
            throw new SentinelException(
                "SESSION_NOT_FOUND",
                $"No active session found after TX {txResult.TxHash} — events did not contain a session ID and active-session query was empty after 3 attempts."
            );
        }

        ulong maxId = 0;
        foreach (var session in sessions)
        {
            if (session.Id > maxId)
            {
                maxId = session.Id;
            }
        }

        return maxId;
    }

    /// <summary>
    /// Map a Sentinel transport number to the V2Ray transport name.
    /// 1=ds, 2=gun, 3=grpc, 4=http, 5=mkcp, 6=quic, 7=tcp, 8=websocket.
    /// CRITICAL: gun (2) and grpc (3) are DIFFERENT protocols.
    /// </summary>
    /// <param name="transport">Numeric transport identifier from handshake metadata.</param>
    /// <returns>Transport name string for V2Ray config.</returns>
    private static string MapTransportNumber(int transport)
    {
        return transport switch
        {
            1 => "ds",
            2 => "gun",
            3 => "grpc",
            4 => "http",
            5 => "mkcp",
            6 => "quic",
            7 => "tcp",
            8 => "websocket",
            _ => "tcp",
        };
    }
}
