using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── VPN Service (Two-Wallet Architecture) ──────────────────────────────────

/// <summary>
/// High-level VPN service with operator + user wallet separation.
/// <para>
/// The <b>operator wallet</b> manages subscriptions, creates sessions, and
/// performs handshakes — it is the identity the dVPN node recognises.
/// </para>
/// <para>
/// The <b>user wallet</b> holds P2P balance and makes payments (activation
/// payments, top-ups) to the operator address.
/// </para>
/// <para>
/// This mirrors the two-wallet pattern in the JS SDK where a consumer app
/// ships with a pre-configured operator wallet while each end-user imports
/// or generates their own wallet.
/// </para>
/// </summary>
public class SentinelVpnService : IDisposable
{
    // ─── Fields ──────────────────────────────────────────────────────

    private readonly SentinelWallet _operator;
    private readonly SentinelVpnClient _vpnClient;
    private readonly ChainClient _operatorChainClient;
    private readonly SentinelVpnOptions _options;

    private SentinelWallet? _user;
    private ChainClient? _userChainClient;
    private TransactionBuilder? _userTxBuilder;
    private bool _disposed;

    /// <summary>
    /// Plan IDs this VPN service operates on. Nodes shown to users come from these plans.
    /// Set by the white-label developer to their own plan ID(s).
    /// If empty, falls back to querying the operator's subscriptions.
    /// </summary>
    public int[] PlanIds { get; set; }

    // ─── Events ──────────────────────────────────────────────────────

    /// <summary>Raised during each step of the connection flow (forwarded from internal VPN client).</summary>
    public event EventHandler<ProgressEventArgs>? Progress;

    /// <summary>Raised when a VPN connection is successfully established (forwarded from internal VPN client).</summary>
    public event EventHandler<ConnectionEventArgs>? Connected;

    /// <summary>Raised when the VPN is disconnected (forwarded from internal VPN client).</summary>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Raised when an error occurs during connection or tunnel operation (forwarded from internal VPN client).</summary>
    public event EventHandler<ErrorEventArgs>? Error;

    // ─── Constructor ─────────────────────────────────────────────────

    /// <summary>
    /// Create a new VPN service with an operator wallet.
    /// The operator wallet is used for all chain operations (subscriptions, sessions, handshakes).
    /// Call <see cref="SetUserWallet"/> to set the user wallet for balance queries and payments.
    /// </summary>
    /// <param name="operatorWallet">
    /// Wallet used as the operator identity. Must have active subscriptions on chain.
    /// </param>
    /// <param name="options">Optional VPN configuration (endpoints, tunnel mode, V2Ray path, etc.).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="operatorWallet"/> is null.</exception>
    /// <summary>
    /// Create a new VPN service with an operator wallet and plan IDs.
    /// </summary>
    /// <param name="operatorWallet">Wallet that owns the subscription plans.</param>
    /// <param name="planIds">
    /// Plan IDs this app operates on. Nodes shown to users come from these plans.
    /// The white-label developer sets this to their own plan ID(s).
    /// </param>
    /// <param name="options">Optional VPN configuration.</param>
    public SentinelVpnService(SentinelWallet operatorWallet, int[]? planIds = null, SentinelVpnOptions? options = null)
    {
        _operator = operatorWallet ?? throw new ArgumentNullException(nameof(operatorWallet));
        _options = options ?? new SentinelVpnOptions();
        PlanIds = planIds ?? Array.Empty<int>();

        _vpnClient = new SentinelVpnClient(_operator, _options);
        _operatorChainClient = new ChainClient(_options.LcdUrls, _options.RpcUrls);

        // Forward events from the internal VPN client
        _vpnClient.Progress += (s, e) => Progress?.Invoke(this, e);
        _vpnClient.Connected += (s, e) => Connected?.Invoke(this, e);
        _vpnClient.Disconnected += (s, e) => Disconnected?.Invoke(this, e);
        _vpnClient.Error += (s, e) => Error?.Invoke(this, e);
    }

    // ─── User Wallet ─────────────────────────────────────────────────

    /// <summary>
    /// Set the user wallet for balance queries and activation payments.
    /// This wallet represents the end-user and holds their P2P balance.
    /// Can be called multiple times to switch users.
    /// </summary>
    /// <param name="user">User wallet (from <see cref="SentinelWallet.Generate"/> or <see cref="SentinelWallet.FromMnemonic"/>).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="user"/> is null.</exception>
    public void SetUserWallet(SentinelWallet user)
    {
        ArgumentNullException.ThrowIfNull(user);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Clean up previous user chain client if switching users
        _userChainClient?.Dispose();

        _user = user;
        _userChainClient = new ChainClient(_options.LcdUrls, _options.RpcUrls);
        _userTxBuilder = new TransactionBuilder(_user, _userChainClient);
    }

    /// <summary>
    /// Whether a user wallet has been set via <see cref="SetUserWallet"/>.
    /// </summary>
    public bool HasUserWallet => _user is not null;

    /// <summary>
    /// The operator wallet address (sent1...).
    /// </summary>
    public string OperatorAddress => _operator.Address;

    /// <summary>
    /// The user wallet address (sent1...), or null if no user wallet is set.
    /// </summary>
    public string? UserAddress => _user?.Address;

    // ─── Connection State ────────────────────────────────────────────

    /// <summary>
    /// Whether the VPN client currently has an active connection.
    /// </summary>
    public bool IsConnected => _vpnClient.IsConnected;

    /// <summary>
    /// Get the current connection status, or null if not connected.
    /// </summary>
    /// <returns>Connection status with uptime, or null if disconnected.</returns>
    public ConnectionStatus? GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _vpnClient.GetStatus();
    }

    // ─── Balance Queries ─────────────────────────────────────────────

    /// <summary>
    /// Get the user wallet's P2P balance.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Balance with micro-denomination, decimal, and display values.</returns>
    /// <exception cref="SentinelException">Thrown when no user wallet is set.</exception>
    public async Task<Balance> GetUserBalanceAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUserWallet();

        return await _userChainClient!.GetBalanceAsync(_user!.Address, ct);
    }

    /// <summary>
    /// Get the operator wallet's P2P balance.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Balance with micro-denomination, decimal, and display values.</returns>
    public async Task<Balance> GetOperatorBalanceAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _operatorChainClient.GetBalanceAsync(_operator.Address, ct);
    }

    // ─── Connection ──────────────────────────────────────────────────

    /// <summary>
    /// Connect to a specific node using the operator wallet's identity.
    /// The operator wallet manages the on-chain session and handshake.
    /// </summary>
    /// <param name="nodeAddress">On-chain node address (sentnode1...).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details including session ID, service type, and tunnel info.</returns>
    /// <exception cref="SentinelException">Thrown when balance is insufficient, node is unreachable, or tunnel fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<ConnectionResult> ConnectAsync(string nodeAddress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _vpnClient.ConnectAsync(nodeAddress, ct);
    }

    /// <summary>
    /// Connect to a node using an existing operator subscription.
    /// Skips the direct session payment and uses the operator's subscription.
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
        return await _vpnClient.ConnectViaSubscriptionAsync(subscriptionId, nodeAddress, ct);
    }

    /// <summary>
    /// Auto-pick the best available node and connect using the operator wallet.
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
        return await _vpnClient.ConnectAutoAsync(options, ct);
    }

    /// <summary>
    /// Disconnect from the current node and clean up the tunnel.
    /// </summary>
    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _vpnClient.DisconnectAsync();
    }

    // ─── Node Discovery ──────────────────────────────────────────────

    /// <summary>
    /// Get nodes available to the operator through active subscriptions.
    /// Queries the operator's subscriptions, extracts plan IDs, and fetches
    /// the deduplicated list of plan nodes.
    /// </summary>
    /// <remarks>
    /// This returns only nodes that the operator can connect to via existing
    /// subscriptions — NOT all nodes on the chain. For all chain nodes, use
    /// <see cref="ChainClient.GetActiveNodesAsync"/> directly.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deduplicated list of nodes available through the operator's subscriptions.</returns>
    public async Task<IReadOnlyList<ChainNode>> GetAvailableNodesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // If developer configured specific plan IDs, fetch nodes from those plans directly
        if (PlanIds.Length > 0)
        {
            var allNodes = new List<ChainNode>();
            var seen = new HashSet<string>();
            foreach (var planId in PlanIds)
            {
                ct.ThrowIfCancellationRequested();
                var planNodes = await _operatorChainClient.QueryPlanNodesAsync(planId, ct);
                foreach (var node in planNodes)
                {
                    if (seen.Add(node.Address))
                        allNodes.Add(node);
                }
            }
            return allNodes;
        }

        // Fallback: discover plans from operator's subscriptions
        return await _operatorChainClient.GetAvailableNodesAsync(_operator.Address, ct);
    }

    /// <summary>
    /// Get the operator's active subscriptions on chain.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of the operator's subscriptions.</returns>
    public async Task<List<Subscription>> GetOperatorSubscriptionsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _operatorChainClient.GetSubscriptionsAsync(_operator.Address, ct);
    }

    // ─── Payments ────────────────────────────────────────────────────

    /// <summary>
    /// Send an activation payment from the user wallet to the operator wallet.
    /// This is the typical flow where a user pays the operator to gain VPN access.
    /// </summary>
    /// <param name="amountUdvpn">Amount to send in micro-denomination (udvpn). 1 P2P = 1,000,000 udvpn.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transaction result with hash, code, and status.</returns>
    /// <exception cref="SentinelException">
    /// Thrown when no user wallet is set, balance is insufficient, or the TX fails.
    /// </exception>
    public async Task<TxResult> SendActivationPaymentAsync(long amountUdvpn, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUserWallet();

        if (amountUdvpn <= 0)
        {
            throw new SentinelException(
                "INVALID_AMOUNT",
                $"Payment amount must be positive, got {amountUdvpn} udvpn"
            );
        }

        // Check user balance first
        var balance = await _userChainClient!.GetBalanceAsync(_user!.Address, ct);
        ct.ThrowIfCancellationRequested();

        if (balance.Udvpn < amountUdvpn)
        {
            throw new SentinelException(
                ErrorCodes.InsufficientBalance,
                $"User wallet has {balance.Display} but needs {amountUdvpn} udvpn. " +
                "Fund the user wallet before sending an activation payment."
            );
        }

        // Build and broadcast Send TX from user → operator
        var sendMsg = MessageBuilder.Send(
            _user!.Address,
            _operator.Address,
            amountUdvpn
        );

        var result = await _userTxBuilder!.BroadcastAsync(sendMsg);

        if (!result.Success)
        {
            throw new SentinelException(
                ErrorCodes.TxFailed,
                $"Activation payment TX failed (code {result.Code}): {result.RawLog}"
            );
        }

        return result;
    }

    // ─── Verification ────────────────────────────────────────────────

    /// <summary>
    /// Verify the VPN tunnel is working by checking the public IP via an external service.
    /// Delegates to the internal VPN client's verification method.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds for the IP check (default: 8000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ConnectionVerification"/> indicating whether the tunnel is working
    /// and the public IP seen through the tunnel.
    /// </returns>
    public async Task<ConnectionVerification> VerifyConnectionAsync(
        int timeoutMs = 8000,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _vpnClient.VerifyConnectionAsync(timeoutMs, ct);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Ensures a user wallet has been set via <see cref="SetUserWallet"/>.
    /// </summary>
    /// <exception cref="SentinelException">Thrown when no user wallet is configured.</exception>
    private void EnsureUserWallet()
    {
        if (_user is null || _userChainClient is null || _userTxBuilder is null)
        {
            throw new SentinelException(
                "NO_USER_WALLET",
                "No user wallet configured. Call SetUserWallet() before performing user operations."
            );
        }
    }

    // ─── IDisposable ─────────────────────────────────────────────────

    /// <summary>
    /// Dispose the VPN service, disconnecting if still connected and releasing all resources.
    /// Disposes the internal VPN client, both chain clients, and event subscriptions.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _vpnClient.Dispose();
        _operatorChainClient.Dispose();
        _userChainClient?.Dispose();

        GC.SuppressFinalize(this);
    }
}
