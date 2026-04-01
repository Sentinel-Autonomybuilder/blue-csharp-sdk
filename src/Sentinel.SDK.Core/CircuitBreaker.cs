namespace Sentinel.SDK.Core;

// ─── Circuit Breaker Status ─────────────────────────────────────────────────

/// <summary>
/// Snapshot of a single node's circuit breaker state.
/// </summary>
/// <param name="FailureCount">Number of recorded failures since last reset.</param>
/// <param name="LastFailure">UTC timestamp of the most recent failure.</param>
/// <param name="IsOpen">True if the breaker has tripped (node should be skipped).</param>
public record CircuitBreakerStatus(int FailureCount, DateTime LastFailure, bool IsOpen);

// ─── Circuit Breaker ────────────────────────────────────────────────────────

/// <summary>
/// Per-node failure tracking. Nodes that fail repeatedly are skipped for a
/// cooldown period. Thread-safe.
/// <para>
/// Ported from the JS SDK's <c>_circuitBreaker</c> in <c>node-connect.js</c>.
/// After <see cref="Configure">threshold</see> consecutive failures, the
/// circuit opens and <see cref="IsOpen"/> returns <c>true</c> until the TTL
/// expires, at which point the entry is automatically purged.
/// </para>
/// </summary>
public class CircuitBreaker
{
  private readonly object _lock = new();
  private readonly Dictionary<string, (int Count, DateTime LastFail)> _breakers = new();
  private int _threshold = 3;
  private TimeSpan _ttl = TimeSpan.FromMinutes(5);

  // ─── Record Failure ─────────────────────────────────────────────────────

  /// <summary>
  /// Record a failure for the given node address. Increments the failure
  /// counter and updates the last-failure timestamp.
  /// </summary>
  /// <param name="nodeAddress">The Sentinel node address (sentnode1...).</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="nodeAddress"/> is null or empty.</exception>
  public void RecordFailure(string nodeAddress)
  {
    ArgumentException.ThrowIfNullOrEmpty(nodeAddress);

    lock (_lock)
    {
      var entry = _breakers.TryGetValue(nodeAddress, out var existing)
        ? existing
        : (Count: 0, LastFail: DateTime.MinValue);

      entry.Count++;
      entry.LastFail = DateTime.UtcNow;
      _breakers[nodeAddress] = entry;
    }
  }

  // ─── Is Open ────────────────────────────────────────────────────────────

  /// <summary>
  /// Check whether a node's circuit is open (i.e. the node should be
  /// skipped). If the TTL has expired since the last failure, the entry is
  /// purged and the circuit is considered closed.
  /// </summary>
  /// <param name="nodeAddress">The Sentinel node address.</param>
  /// <returns><c>true</c> if the node has hit the failure threshold and the TTL has not yet expired.</returns>
  public bool IsOpen(string nodeAddress)
  {
    ArgumentException.ThrowIfNullOrEmpty(nodeAddress);

    lock (_lock)
    {
      if (!_breakers.TryGetValue(nodeAddress, out var entry))
        return false;

      // TTL expired — auto-purge and treat as closed
      if (DateTime.UtcNow - entry.LastFail > _ttl)
      {
        _breakers.Remove(nodeAddress);
        return false;
      }

      return entry.Count >= _threshold;
    }
  }

  // ─── Reset ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Reset the breaker for a specific node, or clear all breakers when
  /// <paramref name="nodeAddress"/> is <c>null</c>.
  /// </summary>
  /// <param name="nodeAddress">
  /// Node address to reset, or <c>null</c> to clear every entry.
  /// </param>
  public void Reset(string? nodeAddress = null)
  {
    lock (_lock)
    {
      if (nodeAddress is not null)
        _breakers.Remove(nodeAddress);
      else
        _breakers.Clear();
    }
  }

  // ─── Configure ──────────────────────────────────────────────────────────

  /// <summary>
  /// Configure the failure threshold and cooldown TTL.
  /// </summary>
  /// <param name="threshold">
  /// Number of failures before the circuit opens. Minimum 1, default 3.
  /// </param>
  /// <param name="ttl">
  /// Cooldown period after the last failure. Minimum 1 second, default 5 minutes.
  /// When <c>null</c>, the current value is kept.
  /// </param>
  public void Configure(int threshold = 3, TimeSpan? ttl = null)
  {
    lock (_lock)
    {
      _threshold = Math.Max(1, threshold);
      if (ttl.HasValue)
      {
        _ttl = ttl.Value < TimeSpan.FromSeconds(1)
          ? TimeSpan.FromSeconds(1)
          : ttl.Value;
      }
    }
  }

  // ─── Get Status ─────────────────────────────────────────────────────────

  /// <summary>
  /// Get the circuit breaker status for all tracked nodes. Useful for
  /// observability, dashboards, and debugging.
  /// </summary>
  /// <returns>
  /// A dictionary keyed by node address with the current failure count,
  /// last failure time, and open/closed state.
  /// </returns>
  public Dictionary<string, CircuitBreakerStatus> GetStatus()
  {
    lock (_lock)
    {
      var result = new Dictionary<string, CircuitBreakerStatus>(_breakers.Count);

      foreach (var (addr, entry) in _breakers)
      {
        var isOpen = entry.Count >= _threshold
          && DateTime.UtcNow - entry.LastFail <= _ttl;

        result[addr] = new CircuitBreakerStatus(entry.Count, entry.LastFail, isOpen);
      }

      return result;
    }
  }

  /// <summary>
  /// Get the circuit breaker status for a single node.
  /// </summary>
  /// <param name="nodeAddress">The Sentinel node address.</param>
  /// <returns>The status, or <c>null</c> if the node has no recorded failures.</returns>
  public CircuitBreakerStatus? GetStatus(string nodeAddress)
  {
    ArgumentException.ThrowIfNullOrEmpty(nodeAddress);

    lock (_lock)
    {
      if (!_breakers.TryGetValue(nodeAddress, out var entry))
        return null;

      var isOpen = entry.Count >= _threshold
        && DateTime.UtcNow - entry.LastFail <= _ttl;

      return new CircuitBreakerStatus(entry.Count, entry.LastFail, isOpen);
    }
  }
}
