using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Disconnect & Cleanup ───────────────────────────────────────────────────

public partial class SentinelVpnClient
{
    /// <summary>
    /// Disconnect from the current node and clean up the tunnel.
    /// Stops V2Ray process or uninstalls WireGuard tunnel service.
    /// </summary>
    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await DisconnectInternalAsync("user");
    }

    /// <summary>
    /// Internal disconnect that skips the disposed check, used by Dispose/DisposeAsync.
    /// </summary>
    private async Task DisconnectInternalAsync(string reason)
    {
        if (_activeConnection is null)
        {
            return; // Nothing to disconnect
        }

        var sessionId = _activeConnection.SessionId;
        var nodeAddress = _activeConnection.NodeAddress;
        await CleanupTunnelsAsync();

        // End session on chain (best-effort, stored for DisposeAsync to await)
        if (ulong.TryParse(sessionId, out var sid) && sid > 0)
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
                    // Non-fatal — session will expire naturally
                }
            });
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
