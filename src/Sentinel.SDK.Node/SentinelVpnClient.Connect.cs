using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Connection Methods ─────────────────────────────────────────────────────

public partial class SentinelVpnClient
{
    /// <summary>
    /// Connect to a specific node by address (direct pay-per-GB).
    /// </summary>
    /// <remarks>
    /// Flow:
    /// 1. Check wallet balance
    /// 2. Query node status to determine service type (WireGuard/V2Ray)
    /// 3. Create on-chain session via <c>MessageBuilder.StartSession()</c>
    /// 4. Wait for chain propagation (5 seconds)
    /// 5. Perform V3 handshake with the node
    /// 6. Install tunnel (WireGuard service or V2Ray SOCKS5 proxy)
    /// </remarks>
    /// <param name="nodeAddress">On-chain node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details including session ID, service type, and tunnel info.</returns>
    /// <exception cref="SentinelException">Thrown when balance is insufficient, node is unreachable, or tunnel fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<ConnectionResult> ConnectAsync(string nodeAddress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        _logger.Info($"Connecting to {nodeAddress}...");

        // Ensure LCD endpoints are sorted by latency before first use
        await _initTask.ConfigureAwait(false);

        // ─── Connection mutex — prevent concurrent connects ───
        if (!await _connectLock.WaitAsync(0, ct))
        {
            throw new SentinelException(
                ErrorCodes.ConnectionInProgress,
                "Connection already in progress"
            );
        }

        // Session ID tracked at method scope for poisoned-session marking in catch blocks.
        // Ported from js-sdk/node-connect.js line 993: let sessionId = null
        ulong sessionIdForCleanup = 0;

        try
        {
            if (_activeConnection is not null)
            {
                throw new SentinelException(
                    ErrorCodes.AlreadyConnected,
                    $"Already connected to {_activeConnection.NodeAddress}. Call DisconnectAsync() first."
                );
            }

            // ─── Step 0: Clean orphaned tunnels/processes from previous crashes ───
            // Ported from js-sdk/node-connect.js registerCleanupHandlers() → recoverOrphans()
            try
            {
                var pf = DependencyCheck.Preflight(new PreflightOptions { AutoClean = true });
                if (pf.Issues.Count > 0)
                {
                    foreach (var issue in pf.Issues)
                    {
                        if (issue.Severity == "error")
                            _logger.Error($"[preflight] {issue.Message}");
                        else
                            _logger.Warn($"[preflight] {issue.Message}");
                    }
                }
            }
            catch
            {
                // Best effort — preflight failures must not block connection
            }

            // ─── Step 1: Wallet setup ───
            EmitProgress("wallet", "Setting up wallet...");
            ct.ThrowIfCancellationRequested();

            // ─── Step 2: Check balance ───
            EmitProgress("balance", $"Checking balance for {_wallet.Address}...");
            var balance = await _chainClient.GetBalanceAsync(_wallet.Address, ct);
            ct.ThrowIfCancellationRequested();

            if (balance.Udvpn < 100_000)
            {
                throw new SentinelException(
                    "INSUFFICIENT_BALANCE",
                    $"Insufficient balance. Need at least 0.1 P2P for gas fees. Balance: {balance.Display}"
                );
            }

            EmitProgress("balance", $"Balance: {balance.Display}");

            // ─── Step 3: Query node status ───
            EmitProgress("node", $"Querying node {nodeAddress}...");
            var chainNode = await _chainClient.GetNodeAsync(nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            if (chainNode is null)
            {
                throw new SentinelException(
                    "NODE_NOT_FOUND",
                    $"Node {nodeAddress} not found on chain"
                );
            }

            if (chainNode.RemoteUrl is null)
            {
                throw new SentinelException(
                    "NODE_NO_URL",
                    $"Node {nodeAddress} has no remote URL"
                );
            }

            var nodeStatus = await NodeClient.GetStatusAsync(chainNode.RemoteUrl, _tofuStore, nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            var serviceType = nodeStatus.Type.ToLowerInvariant();
            EmitProgress("node", $"Node type: {serviceType}, peers: {nodeStatus.Peers}, location: {nodeStatus.Location.Country}");

            // ─── Pre-verify: node's address must match what we're paying for ───
            // Prevents wasting tokens when remote URL serves a different node.
            if (!string.IsNullOrEmpty(nodeStatus.Address) && nodeStatus.Address != nodeAddress)
            {
                throw new SentinelNodeException(
                    $"Node address mismatch: remote URL serves {nodeStatus.Address}, not {nodeAddress}. Tokens would be wasted — aborting before payment."
                );
            }

            // ─── Step 3a: Clock drift detection for V2Ray VMess ───
            // VMess AEAD is sensitive to clock drift >120s, causing silent connection drain.
            // VLess (proxy_protocol=1) is immune to clock drift — only VMess (2) fails.
            // Decision deferred to post-handshake: if VLess transports exist, strip VMess
            // and reorder; if VMess-only, throw CLOCK_DRIFT_TOO_HIGH.
            var extremeDrift = serviceType == "v2ray"
                && nodeStatus.ClockDriftSec.HasValue
                && Math.Abs(nodeStatus.ClockDriftSec.Value) > 120;

            // ─── Step 4: Validate V2Ray path if needed ───
            if (serviceType == "v2ray" && string.IsNullOrWhiteSpace(_options.V2RayExePath))
            {
                throw new SentinelException(
                    "V2RAY_PATH_REQUIRED",
                    "V2Ray node selected but V2RayExePath is not configured in SentinelVpnOptions"
                );
            }

            // ─── Fast Reconnect: Check saved credentials ───
            if (!_options.ForceNewSession)
            {
                var saved = CredentialStore.Load(nodeAddress);
                if (saved != null)
                {
                    _logger.Info($"Found saved credentials for {nodeAddress}, verifying session {saved.SessionId}...");
                    EmitProgress("cache", "Found cached credentials — verifying session...");

                    var cachedSessionId = await SessionManager.FindExistingSessionAsync(
                        _chainClient, _wallet.Address, nodeAddress, ct);

                    if (cachedSessionId.HasValue && cachedSessionId.Value.ToString() == saved.SessionId)
                    {
                        _logger.Info($"Session {saved.SessionId} still active — fast reconnecting (0 cost)");
                        EmitProgress("cache", "Session active — skipping payment and handshake");

                        // Go straight to tunnel setup with saved credentials
                        return await FastReconnectAsync(saved, nodeAddress, ct);
                    }
                    else
                    {
                        _logger.Info("Saved session expired — clearing credentials");
                        CredentialStore.Clear(nodeAddress);
                    }
                }
            }

            // ─── Step 5: Check for existing active session (skip payment) ───
            ulong sessionId;
            var reuseExisting = false;

            if (!_options.ForceNewSession)
            {
                EmitProgress("session", $"Checking for existing session with {nodeAddress}...");
                try
                {
                    var existingSessionId = await SessionManager.FindExistingSessionAsync(
                        _chainClient, _wallet.Address, nodeAddress, ct);

                    if (existingSessionId.HasValue)
                    {
                        // ── Poisoned session check ──
                        // Ported from js-sdk/node-connect.js line 998:
                        // if (sessionId && isSessionPoisoned(String(sessionId))) { sessionId = null; }
                        if (StateManager.IsSessionPoisoned(existingSessionId.Value.ToString()))
                        {
                            _logger.Warn($"Session {existingSessionId.Value} previously failed — skipping");
                            EmitProgress("session", $"Session {existingSessionId.Value} previously failed — creating new");
                            sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                        }
                        else
                        {
                            sessionId = existingSessionId.Value;
                            reuseExisting = true;
                            EmitProgress("session", $"Reusing existing session {sessionId} (no payment needed)");
                        }
                    }
                    else
                    {
                        sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Stale session or 404 on allocation query — fall through to create new
                    _logger.Warn($"Session reuse failed ({ex.Message}) — creating new session");
                    sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                }
            }
            else
            {
                sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
            }

            // Track session ID at method scope for catch-block poisoned-session marking
            sessionIdForCleanup = sessionId;

            // ─── Step 7: Chain propagation ───
            // NOTE: CreateNewSessionAsync already waits 5s for chain propagation before
            // querying the session ID. The handshake also has built-in chain-lag retry
            // (waits 10s and retries if the node returns "does not exist").
            // No additional delay needed here.

            // ─── Step 8: V3 handshake (with 409 retry) ───
            var handshakeType = serviceType == "wireguard"
                ? HandshakeType.WireGuard
                : HandshakeType.V2Ray;

            object handshakeResult;
            try
            {
                EmitProgress("handshake", $"Performing V3 handshake with {chainNode.RemoteUrl}...");
                handshakeResult = await Handshake.HandshakeAsync(
                    _wallet, chainNode.RemoteUrl, sessionId, handshakeType,
                    _tofuStore, nodeAddress, ct);
            }
            catch (SentinelHandshakeException ex) when (ex.Code == ErrorCodes.SessionExists)
            {
                // Session is poisoned — mark it and create a new one
                // Ported from js-sdk/node-connect.js line 1596:
                // markSessionPoisoned(String(sessionId), opts.nodeAddress, hsErr.message)
                StateManager.MarkSessionPoisoned(sessionId.ToString(), nodeAddress, ex.Message);
                _logger.Warn($"Retrying after session conflict (409) on {nodeAddress}");
                EmitProgress("handshake", "Session conflict (409). Creating new session and retrying...");
                sessionId = await CreateNewSessionAsync(chainNode, nodeAddress, ct);
                sessionIdForCleanup = sessionId; // Update for catch-block poisoning

                EmitProgress("propagation", "Waiting for chain propagation (5s)...");
                await Task.Delay(CHAIN_PROPAGATION_DELAY_MS, ct);

                EmitProgress("handshake", $"Retrying V3 handshake with session {sessionId}...");
                handshakeResult = await Handshake.HandshakeAsync(
                    _wallet, chainNode.RemoteUrl, sessionId, handshakeType,
                    _tofuStore, nodeAddress, ct);
            }

            ct.ThrowIfCancellationRequested();
            EmitProgress("handshake", "Handshake successful");

            // ─── Step 9: Install tunnel ───
            ConnectionResult result;

            if (handshakeResult is WireGuardHandshakeResult wgResult)
            {
                result = await InstallWireGuardTunnelAsync(wgResult, sessionId, nodeAddress, ct);
            }
            else if (handshakeResult is V2RayHandshakeResult v2Result)
            {
                result = await InstallV2RayTunnelAsync(
                    v2Result, sessionId, nodeAddress, chainNode.RemoteUrl,
                    extremeDrift, nodeStatus.ClockDriftSec, ct
                );
            }
            else
            {
                throw new SentinelException(
                    "UNKNOWN_SERVICE",
                    $"Unknown handshake result type: {handshakeResult.GetType().Name}"
                );
            }

            // ─── Step 11: Wait for WireGuard peer handshake, then verify ───
            await Task.Delay(5000, ct);
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(timeoutMs: 15000, ct: ct);
            result = result with { Verification = verification };

            if (verification.Working)
            {
                EmitProgress("verify", $"Tunnel verified, external IP: {verification.VpnIp}");
            }
            else
            {
                EmitProgress("verify", "Tunnel verification failed — IP check did not succeed");
            }

            // ─── Step 11: Save credentials for fast reconnect (AFTER tunnel verified) ───
            if (handshakeResult is WireGuardHandshakeResult wgHs)
            {
                CredentialStore.Save(nodeAddress, new SavedCredentials
                {
                    SessionId = sessionId.ToString(),
                    ServiceType = "wireguard",
                    NodeAddress = nodeAddress,
                    WgPrivateKey = Convert.ToBase64String(wgHs.ClientPrivateKey),
                    WgServerPubKey = wgHs.ServerPublicKey,
                    WgAssignedAddrs = wgHs.AssignedAddresses,
                    WgServerEndpoint = wgHs.ServerEndpoint,
                    SavedAt = DateTime.UtcNow.ToString("o"),
                });
            }
            else if (handshakeResult is V2RayHandshakeResult v2Hs)
            {
                var v2RayHost = new Uri(chainNode.RemoteUrl).Host;
                CredentialStore.Save(nodeAddress, new SavedCredentials
                {
                    SessionId = sessionId.ToString(),
                    ServiceType = "v2ray",
                    NodeAddress = nodeAddress,
                    V2RayUuid = v2Hs.Uuid,
                    V2RayTransport = v2Hs.Transport,
                    V2RayProtocol = v2Hs.ProxyProtocol,
                    V2RayTls = v2Hs.Tls,
                    V2RayPort = v2Hs.Port,
                    V2RayServerHost = v2RayHost,
                    SavedAt = DateTime.UtcNow.ToString("o"),
                });
            }

            // ─── Step 12: Finalize ───
            // Ported from js-sdk/node-connect.js line 1586:
            // markSessionActive(String(sessionId), opts.nodeAddress)
            StateManager.MarkSessionActive(sessionId.ToString(), nodeAddress);

            _activeConnection = result;
            _connectedAt = DateTime.UtcNow;

            EmitConnected(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SentinelException and not SentinelHandshakeException and not SentinelNodeException)
        {
            // Clear stale credentials on tunnel failure to prevent reuse of bad handshake data
            CredentialStore.Clear(nodeAddress);

            // Mark session as poisoned so it won't be reused
            // Ported from js-sdk/node-connect.js line 1596:
            // markSessionPoisoned(String(sessionId), opts.nodeAddress, hsErr.message)
            if (sessionIdForCleanup > 0)
            {
                StateManager.MarkSessionPoisoned(sessionIdForCleanup.ToString(), nodeAddress, ex.Message);
            }

            _logger.Error($"Connection failed to {nodeAddress}", ex);
            var wrapped = new SentinelException(
                "CONNECT_FAILED",
                $"Failed to connect to {nodeAddress}: {ex.Message}",
                ex
            );
            EmitError(wrapped);
            throw wrapped;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Auto-pick the best available node and connect.
    /// Queries online nodes, filters by country and service type, and attempts
    /// connection to up to <see cref="ConnectAutoOptions.MaxAttempts"/> nodes.
    /// </summary>
    /// <param name="options">Filter and retry options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details of the successful connection.</returns>
    /// <exception cref="SentinelException">Thrown when no suitable node can be connected after all attempts.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<ConnectionResult> ConnectAutoAsync(
        ConnectAutoOptions? options = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Ensure LCD endpoints are sorted by latency before first use
        await _initTask.ConfigureAwait(false);

        // ─── Connection mutex — prevent concurrent connects ───
        if (!await _connectLock.WaitAsync(0, ct))
        {
            throw new SentinelException(
                ErrorCodes.ConnectionInProgress,
                "Connection already in progress"
            );
        }

        var lockHeld = true;
        try
        {
            var autoOpts = options ?? new ConnectAutoOptions();
            var maxAttempts = Math.Max(1, autoOpts.MaxAttempts);
            var nodePool = autoOpts.NodePool;

            // ─── Step 1: Query online nodes (cached) ───
            EmitProgress("discovery", "Querying active nodes from chain...");
            var nodes = await _nodeCache.GetAsync(async () =>
                (IReadOnlyList<ChainNode>)await _chainClient.GetActiveNodesAsync(limit: 5000, ct: ct));
            ct.ThrowIfCancellationRequested();

            if (nodes.Count == 0)
            {
                throw new SentinelException("NO_NODES", "No active nodes found on chain");
            }

            EmitProgress("discovery", $"Found {nodes.Count} active nodes");

            // ─── Step 2: Filter candidates ───
            var candidates = new List<ChainNode>();

            foreach (var node in nodes)
            {
                if (node.RemoteUrl is null)
                {
                    continue;
                }

                // If NodePool is set, only include those specific addresses
                if (nodePool is not null && nodePool.Length > 0)
                {
                    var inPool = false;
                    foreach (var poolAddr in nodePool)
                    {
                        if (string.Equals(node.Address, poolAddr, StringComparison.OrdinalIgnoreCase))
                        {
                            inPool = true;
                            break;
                        }
                    }
                    if (!inPool) continue;
                }

                candidates.Add(node);
            }

            // ─── Step 3: Probe candidates in parallel ───
            var semaphore = new SemaphoreSlim(10); // max 10 parallel probes
            var probeTasks = candidates.Select(async node =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (_circuitBreaker.IsOpen(node.Address)) return (node, (NodeStatus?)null);
                    var status = await NodeClient.GetStatusAsync(node.RemoteUrl!, _tofuStore, node.Address, ct);
                    return (node, (NodeStatus?)status);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    _circuitBreaker.RecordFailure(node.Address);
                    return (node, (NodeStatus?)null);
                }
                finally { semaphore.Release(); }
            });
            var probed = await Task.WhenAll(probeTasks);

            // Apply country/service type/clock drift filters on probed results
            var filtered = new List<(ChainNode Node, NodeStatus Status)>();
            foreach (var (node, status) in probed)
            {
                if (status is null) continue;

                // Filter by service type
                if (autoOpts.ServiceType is not null &&
                    !status.Type.Equals(autoOpts.ServiceType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Filter by country — match against both country name and ISO alpha-2 code
                if (autoOpts.Countries is not null && autoOpts.Countries.Length > 0)
                {
                    var match = false;
                    foreach (var country in autoOpts.Countries)
                    {
                        if (status.Location.Country.Equals(country, StringComparison.OrdinalIgnoreCase)
                            || status.Location.CountryCode.Equals(country, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                            break;
                        }
                    }
                    if (!match) continue;
                }

                // Skip nodes with excessive clock drift (>120s)
                if (status.ClockDriftSec.HasValue && Math.Abs(status.ClockDriftSec.Value) > 120)
                {
                    continue;
                }

                filtered.Add((node, status));

                if (filtered.Count >= maxAttempts * 3)
                {
                    break; // Enough candidates
                }
            }

            if (filtered.Count == 0)
            {
                throw new SentinelException(
                    "NO_MATCHING_NODES",
                    "No nodes match the specified criteria (country, service type, node pool)"
                );
            }

            // ─── Step 4: Sort by fewest peers (least loaded) ───
            filtered.Sort((a, b) => a.Status.Peers.CompareTo(b.Status.Peers));

            // ─── Step 5: Try connecting to candidates ───
            // Release the lock before calling ConnectAsync (which acquires it)
            _connectLock.Release();
            lockHeld = false;

            var attempts = 0;
            Exception? lastException = null;

            foreach (var (node, status) in filtered)
            {
                if (attempts >= maxAttempts)
                {
                    break;
                }

                ct.ThrowIfCancellationRequested();
                attempts++;

                EmitProgress("auto", $"Attempt {attempts}/{maxAttempts}: {node.Address} ({status.Type}, {status.Location.Country})");

                try
                {
                    var result = await ConnectAsync(node.Address, ct);
                    _circuitBreaker.Reset(node.Address);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Auto-connect attempt {attempts} failed for {node.Address}: {ex.Message}");
                    _circuitBreaker.RecordFailure(node.Address);
                    lastException = ex;
                    EmitProgress("auto", $"Attempt {attempts} failed: {ex.Message}");

                    // Clean up any partial state from the failed attempt
                    await CleanupTunnelsAsync();
                }
            }

            throw new SentinelException(
                ErrorCodes.AllNodesFailed,
                $"Failed to connect after {attempts} attempts. Last error: {lastException?.Message}",
                lastException!
            );
        }
        finally
        {
            // Only release if we still hold the lock (not yet released for ConnectAsync calls)
            if (lockHeld)
            {
                _connectLock.Release();
            }
        }
    }

    /// <summary>
    /// Connect to a node using an existing on-chain subscription.
    /// Skips the session creation TX and uses the existing subscription's session.
    /// </summary>
    /// <param name="subscriptionId">On-chain subscription ID.</param>
    /// <param name="nodeAddress">Node address to connect to (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details including session ID, service type, and tunnel info.</returns>
    /// <exception cref="SentinelException">Thrown when the subscription is invalid or connection fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<ConnectionResult> ConnectViaSubscriptionAsync(
        ulong subscriptionId,
        string nodeAddress,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeAddress);

        // Ensure LCD endpoints are sorted by latency before first use
        await _initTask.ConfigureAwait(false);

        // ─── Connection mutex — prevent concurrent connects ───
        if (!await _connectLock.WaitAsync(0, ct))
        {
            throw new SentinelException(
                ErrorCodes.ConnectionInProgress,
                "Connection already in progress"
            );
        }

        // Session ID tracked at method scope for catch-block poisoned-session marking
        ulong sessionIdForCleanup = 0;

        try
        {
            if (_activeConnection is not null)
            {
                throw new SentinelException(
                    ErrorCodes.AlreadyConnected,
                    $"Already connected to {_activeConnection.NodeAddress}. Call DisconnectAsync() first."
                );
            }

            // ─── Step 1: Wallet check ───
            EmitProgress("wallet", "Setting up wallet...");
            ct.ThrowIfCancellationRequested();

            // ─── Step 2: Query node ───
            EmitProgress("node", $"Querying node {nodeAddress}...");
            var chainNode = await _chainClient.GetNodeAsync(nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            if (chainNode is null)
            {
                throw new SentinelException("NODE_NOT_FOUND", $"Node {nodeAddress} not found on chain");
            }

            if (chainNode.RemoteUrl is null)
            {
                throw new SentinelException("NODE_NO_URL", $"Node {nodeAddress} has no remote URL");
            }

            var nodeStatus = await NodeClient.GetStatusAsync(chainNode.RemoteUrl, _tofuStore, nodeAddress, ct);
            ct.ThrowIfCancellationRequested();

            var serviceType = nodeStatus.Type.ToLowerInvariant();

            // ─── Step 2a: Clock drift detection ───
            var extremeDriftSub = serviceType == "v2ray"
                && nodeStatus.ClockDriftSec.HasValue
                && Math.Abs(nodeStatus.ClockDriftSec.Value) > 120;

            // ─── Step 3: Validate V2Ray path ───
            if (serviceType == "v2ray" && string.IsNullOrWhiteSpace(_options.V2RayExePath))
            {
                throw new SentinelException(
                    "V2RAY_PATH_REQUIRED",
                    "V2Ray node selected but V2RayExePath is not configured in SentinelVpnOptions"
                );
            }

            // ─── Step 4: Allocate session on existing subscription ───
            EmitProgress("subscribe", $"Allocating session on subscription {subscriptionId}...");

            var allocateMsg = MessageBuilder.SubStartSession(
                _wallet.Address,
                subscriptionId,
                nodeAddress
            );
            var txResult = await _txBuilder.BroadcastAsync(allocateMsg);
            ct.ThrowIfCancellationRequested();

            if (!txResult.Success)
            {
                throw new SentinelException(
                    "TX_FAILED",
                    $"Session allocation TX failed (code {txResult.Code}): {txResult.RawLog}"
                );
            }

            EmitProgress("subscribe", $"TX broadcast: {txResult.TxHash}");

            // ─── Step 5: Wait for chain propagation BEFORE querying session ───
            EmitProgress("propagation", "Waiting for chain propagation (5s)...");
            await Task.Delay(CHAIN_PROPAGATION_DELAY_MS, ct);

            var sessionId = await ExtractSessionId(txResult, ct);
            sessionIdForCleanup = sessionId; // Track for catch-block poisoning
            EmitProgress("subscribe", $"Session ID: {sessionId}");

            // ─── Step 6: V3 handshake ───
            EmitProgress("handshake", $"Performing V3 handshake with {chainNode.RemoteUrl}...");

            var handshakeType = serviceType == "wireguard"
                ? HandshakeType.WireGuard
                : HandshakeType.V2Ray;

            var handshakeResult = await Handshake.HandshakeAsync(
                _wallet,
                chainNode.RemoteUrl,
                sessionId,
                handshakeType,
                _tofuStore,
                nodeAddress,
                ct
            );
            ct.ThrowIfCancellationRequested();

            EmitProgress("handshake", "Handshake successful");

            // ─── Step 7: Install tunnel ───
            ConnectionResult result;

            if (handshakeResult is WireGuardHandshakeResult wgResult)
            {
                result = await InstallWireGuardTunnelAsync(wgResult, sessionId, nodeAddress, ct);
            }
            else if (handshakeResult is V2RayHandshakeResult v2Result)
            {
                result = await InstallV2RayTunnelAsync(
                    v2Result, sessionId, nodeAddress, chainNode.RemoteUrl,
                    extremeDriftSub, nodeStatus.ClockDriftSec, ct
                );
            }
            else
            {
                throw new SentinelException(
                    "UNKNOWN_SERVICE",
                    $"Unknown handshake result type: {handshakeResult.GetType().Name}"
                );
            }

            // ─── Step 8: Wait for WireGuard peer handshake, then verify ───
            await Task.Delay(5000, ct);
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(timeoutMs: 15000, ct: ct);
            result = result with { Verification = verification };

            if (verification.Working)
            {
                EmitProgress("verify", $"Tunnel verified, external IP: {verification.VpnIp}");
            }
            else
            {
                EmitProgress("verify", "Tunnel verification failed — IP check did not succeed");
            }

            // ─── Step 9: Finalize ───
            // Ported from js-sdk/node-connect.js line 1586:
            // markSessionActive(String(sessionId), opts.nodeAddress)
            StateManager.MarkSessionActive(sessionId.ToString(), nodeAddress);

            _activeConnection = result;
            _connectedAt = DateTime.UtcNow;

            EmitConnected(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SentinelException and not SentinelHandshakeException and not SentinelNodeException)
        {
            // Clear stale credentials on tunnel failure to prevent reuse of bad handshake data
            CredentialStore.Clear(nodeAddress);

            // Mark session as poisoned so it won't be reused
            // Ported from js-sdk/node-connect.js line 1596
            if (sessionIdForCleanup > 0)
            {
                StateManager.MarkSessionPoisoned(sessionIdForCleanup.ToString(), nodeAddress, ex.Message);
            }

            _logger.Error($"Connection failed via subscription {subscriptionId} to {nodeAddress}", ex);
            var wrapped = new SentinelException(
                "CONNECT_FAILED",
                $"Failed to connect via subscription {subscriptionId} to {nodeAddress}: {ex.Message}",
                ex
            );
            EmitError(wrapped);
            throw wrapped;
        }
        finally
        {
            _connectLock.Release();
        }
    }
}
