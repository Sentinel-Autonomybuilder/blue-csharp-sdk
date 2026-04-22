using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Disconnect & Cleanup ───────────────────────────────────────────────────
//
// TWO DISCONNECT PATHS — CHOOSE INTENT EXPLICITLY.
//
// Soft: DisconnectAsync()
//   - Tears down the local tunnel (WireGuard/V2Ray), stops V2Ray process.
//   - Leaves the on-chain session in status=1 (active).
//   - Next ConnectAsync to the SAME node reuses the session via
//     SessionManager.FindExistingSessionAsync — no MsgStartSession TX,
//     no new ~40 P2P deposit, remaining bandwidth preserved.
//   - Use when: user is pausing, closing the app, or will reconnect soon.
//
// Hard: DisconnectAndEndSessionAsync()
//   - Tears down the tunnel AND broadcasts MsgCancelSession on chain.
//   - Session moves status=1 → status=2 (inactive_pending) → status=3 (inactive)
//     after the ~2h settlement window. Unused bandwidth deposit is refunded.
//   - Use when: user is done with this node, switching nodes permanently,
//     or wants the deposit back.
//
// PLAN-BASED FLOWS: plan subscribers do not broadcast MsgStartSession and do
// not pay per-session deposits. Either disconnect path is safe; hard-end just
// stops metering against the plan's allocation. Prefer hard-end for
// predictable plan accounting.
//
// (Until 2026-04-21 this was a single DisconnectAsync(bool endSession=true)
// with a hidden default that always ended the session. The boolean flag
// hid intent and made session reuse invisible.)

public partial class SentinelVpnClient
{
    /// <summary>
    /// Soft disconnect — tear down the local tunnel, leave the on-chain session active.
    /// A subsequent <see cref="ConnectAsync"/> to the SAME node will reuse the session
    /// (no new MsgStartSession, no new payment).
    /// </summary>
    /// <remarks>
    /// Use this when the user is pausing, switching networks, closing the app temporarily,
    /// or will likely reconnect to the same node. To settle the session on-chain and
    /// reclaim the unused deposit, use <see cref="DisconnectAndEndSessionAsync"/>.
    /// </remarks>
    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await DisconnectInternalAsync("user", endSession: false);
    }

    /// <summary>
    /// Hard disconnect — tear down the tunnel AND broadcast <c>MsgCancelSession</c> on chain.
    /// The session settles after the ~2h <c>inactive_pending</c> window and the node refunds
    /// the unused portion of the bandwidth deposit.
    /// </summary>
    /// <remarks>
    /// Use this when the user is done with this node (switching nodes, ending the trip, or
    /// wants the deposit back). For pause / temporary-disconnect use <see cref="DisconnectAsync"/>
    /// so the session can be reused without re-paying.
    /// <para>
    /// Applies to both peer-to-peer and plan-based flows. For plan subscribers, this stops
    /// metering against the plan allocation (no refund, because no per-session deposit).
    /// </para>
    /// </remarks>
    public async Task DisconnectAndEndSessionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await DisconnectInternalAsync("user", endSession: true);
    }

    /// <summary>
    /// Internal disconnect that skips the disposed check, used by Dispose/DisposeAsync
    /// and the network-change handler.
    /// </summary>
    /// <param name="reason">Reason string emitted on the Disconnected event.</param>
    /// <param name="endSession">
    /// <c>true</c> to broadcast <c>MsgCancelSession</c>, <c>false</c> to preserve the session.
    /// </param>
    private async Task DisconnectInternalAsync(string reason, bool endSession)
    {
        if (_activeConnection is null)
        {
            return; // Nothing to disconnect
        }

        var sessionId = _activeConnection.SessionId;
        var nodeAddress = _activeConnection.NodeAddress;
        await CleanupTunnelsAsync();

        // End session on chain (best-effort, stored for DisposeAsync to await).
        if (endSession && ulong.TryParse(sessionId, out var sid) && sid > 0)
        {
            _pendingEndSession = Task.Run(async () =>
            {
                try
                {
                    var msg = MessageBuilder.EndSession(_wallet.Address, sid);
                    var tx = await _txBuilder.BroadcastAsync(msg);
                    _logger.Info($"Session {sid} ended on chain: TX {tx.TxHash} (code={tx.Code})");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to end session {sid} on chain: {ex.Message}");
                    // Non-fatal — session will expire naturally at the chain-level deadline.
                }
            });
        }
        else if (!endSession && ulong.TryParse(sessionId, out var preservedSid) && preservedSid > 0)
        {
            _logger.Info($"Session {preservedSid} preserved on chain (status=1) for future reuse — no MsgCancelSession broadcast.");
        }

        _activeConnection = null;
        EmitDisconnected(reason);
    }

    /// <summary>
    /// Clean up any active tunnels (WireGuard service or V2Ray process).
    /// </summary>
    private async Task CleanupTunnelsAsync()
    {
        if (_wgTunnel is not null)
        {
            try
            {
                await _wgTunnel.UninstallAsync();
            }
            catch (Exception ex)
            {
                EmitError(new SentinelException("CLEANUP_WG", $"WireGuard cleanup failed: {ex.Message}", ex));
            }
            finally
            {
                _wgTunnel.Dispose();
                _wgTunnel = null;
            }
        }

        if (_v2RayProcess is not null)
        {
            try
            {
                await _v2RayProcess.StopAsync();
            }
            catch (Exception ex)
            {
                EmitError(new SentinelException("CLEANUP_V2RAY", $"V2Ray cleanup failed: {ex.Message}", ex));
            }
            finally
            {
                _v2RayProcess.Dispose();
                _v2RayProcess = null;
            }
        }

        // Clear system proxy if we set it
        if (_systemProxySet)
        {
            try { SystemProxy.Clear(); }
            catch { /* best effort */ }
            _systemProxySet = false;
        }
    }
}
