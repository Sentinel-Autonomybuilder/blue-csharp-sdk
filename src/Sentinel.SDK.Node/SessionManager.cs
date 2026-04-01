using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Session Allocation Model ───

/// <summary>
/// Bandwidth allocation details for an active session.
/// </summary>
/// <param name="MaxBytes">Total bytes allocated for this session.</param>
/// <param name="UsedBytes">Bytes consumed so far.</param>
/// <param name="RemainingBytes">Bytes remaining (MaxBytes - UsedBytes).</param>
/// <param name="PercentUsed">Usage percentage (0-100).</param>
public record SessionAllocation(
    long MaxBytes,
    long UsedBytes,
    long RemainingBytes,
    int PercentUsed
);

/// <summary>
/// Manages Sentinel dVPN session lifecycle including discovery and allocation queries.
/// </summary>
public static class SessionManager
{
    /// <summary>
    /// Searches for an existing active session between a wallet and a node on-chain.
    /// </summary>
    /// <param name="client">Chain client for LCD/RPC queries.</param>
    /// <param name="walletAddress">Bech32 wallet address (e.g. "sent1...").</param>
    /// <param name="nodeAddress">Bech32 node address (e.g. "sentnode1...").</param>
    /// <returns>
    /// The session ID if an active session exists, or <c>null</c> if no active session is found.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    /// <exception cref="SentinelSessionException">Thrown when the chain query fails.</exception>
    public static async Task<ulong?> FindExistingSessionAsync(
        IChainClient client,
        string walletAddress,
        string nodeAddress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(walletAddress);
        ArgumentNullException.ThrowIfNull(nodeAddress);

        try
        {
            var sessions = await client.QueryActiveSessionsForAddressAsync(walletAddress, ct);

            foreach (var session in sessions)
            {
                if (string.Equals(session.NodeAddress, nodeAddress, StringComparison.OrdinalIgnoreCase)
                    && session.Status == SessionStatus.Active)
                {
                    // Check data allocation — skip exhausted sessions (matches JS SDK behavior)
                    var allocation = await GetSessionAllocationAsync(client, session.Id, ct);
                    if (allocation is not null && allocation.MaxBytes > 0
                        && allocation.UsedBytes >= allocation.MaxBytes)
                    {
                        continue; // Data exhausted, look for another session
                    }

                    return session.Id;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not SentinelSessionException)
        {
            throw new SentinelSessionException(
                $"Failed to query active sessions for {walletAddress}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Queries the on-chain bandwidth allocation for a specific session.
    /// </summary>
    /// <param name="client">Chain client for LCD/RPC queries.</param>
    /// <param name="sessionId">The on-chain session ID to query.</param>
    /// <returns>
    /// A <see cref="SessionAllocation"/> with bandwidth details, or <c>null</c> if the session
    /// has no allocation (not yet started or already ended).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
    /// <exception cref="SentinelSessionException">Thrown when the chain query fails.</exception>
    public static async Task<SessionAllocation?> GetSessionAllocationAsync(
        IChainClient client,
        ulong sessionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            var allocation = await client.QuerySessionAllocationAsync(sessionId, ct);

            if (allocation is null)
                return null;

            var remaining = allocation.MaxBytes - allocation.UsedBytes;
            if (remaining < 0) remaining = 0;

            var percentUsed = allocation.MaxBytes > 0
                ? (int)(allocation.UsedBytes * 100 / allocation.MaxBytes)
                : 0;

            return new SessionAllocation(
                MaxBytes: allocation.MaxBytes,
                UsedBytes: allocation.UsedBytes,
                RemainingBytes: remaining,
                PercentUsed: Math.Min(percentUsed, 100)
            );
        }
        catch (Exception ex) when (ex is not SentinelSessionException)
        {
            throw new SentinelSessionException(
                $"Failed to query allocation for session {sessionId}: {ex.Message}", ex);
        }
    }
}

// ─── Session Exception ───

/// <summary>
/// Thrown when a session management operation fails.
/// Inherits from <see cref="ChainException"/> in the unified error hierarchy.
/// </summary>
public class SentinelSessionException : ChainException
{
    /// <summary>Initializes a new instance with the specified message.</summary>
    public SentinelSessionException(string message)
        : base(ErrorCodes.BroadcastFailed, message) { }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    public SentinelSessionException(string message, Exception innerException)
        : base(ErrorCodes.BroadcastFailed, message, innerException) { }

    /// <summary>Initializes a new instance with a specific error code and message.</summary>
    public SentinelSessionException(string code, string message)
        : base(code, message) { }
}
