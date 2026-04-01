namespace Sentinel.SDK.Core;

// ─── Node Cache ─────────────────────────────────────────────────────────────

/// <summary>
/// In-memory node list cache with TTL and stale-while-revalidate. Thread-safe.
/// <para>
/// Ported from the JS SDK's <c>_nodeCache</c> / <c>_inflightRefresh</c> pattern
/// in <c>node-connect.js</c>. When the cache is fresh, returns immediately.
/// When stale, returns the stale data and kicks off a deduplicated background
/// refresh. Concurrent callers share a single inflight fetch.
/// </para>
/// </summary>
public class NodeCache
{
  private readonly object _lock = new();
  private readonly TimeSpan _ttl;

  private List<ChainNode>? _nodes;
  private DateTime _timestamp;
  private Task<IReadOnlyList<ChainNode>>? _inflightRefresh;

  /// <summary>
  /// Create a new node cache.
  /// </summary>
  /// <param name="ttl">
  /// Time-to-live for cached data. After this period the cache is considered
  /// stale and a background refresh is triggered. Default: 5 minutes.
  /// </param>
  public NodeCache(TimeSpan? ttl = null)
  {
    _ttl = ttl ?? TimeSpan.FromMinutes(5);
    _timestamp = DateTime.MinValue;
  }

  // ─── IsFresh ──────────────────────────────────────────────────────────

  /// <summary>
  /// Check if the cache contains data that is within the TTL window.
  /// </summary>
  public bool IsFresh
  {
    get
    {
      lock (_lock)
      {
        return _nodes is not null
          && DateTime.UtcNow - _timestamp < _ttl;
      }
    }
  }

  // ─── AgeSeconds ───────────────────────────────────────────────────────

  /// <summary>
  /// Cache age in seconds since the last successful refresh. Returns
  /// <see cref="double.MaxValue"/> if the cache has never been populated.
  /// </summary>
  public double AgeSeconds
  {
    get
    {
      lock (_lock)
      {
        if (_nodes is null)
          return double.MaxValue;

        return (DateTime.UtcNow - _timestamp).TotalSeconds;
      }
    }
  }

  // ─── GetAsync ─────────────────────────────────────────────────────────

  /// <summary>
  /// Get nodes from the cache. Behaviour depends on cache state:
  /// <list type="bullet">
  ///   <item><description>
  ///     <b>Fresh cache:</b> returns cached data immediately and starts a
  ///     deduplicated background refresh.
  ///   </description></item>
  ///   <item><description>
  ///     <b>Stale / empty cache:</b> awaits a fresh fetch (deduplicated so
  ///     concurrent callers share one request). Falls back to stale data if
  ///     the fetch fails.
  ///   </description></item>
  /// </list>
  /// </summary>
  /// <param name="fetcher">
  /// Async function that queries the chain for the current node list.
  /// </param>
  /// <returns>The node list (may be stale if refresh fails).</returns>
  public async Task<IReadOnlyList<ChainNode>> GetAsync(
    Func<Task<IReadOnlyList<ChainNode>>> fetcher)
  {
    ArgumentNullException.ThrowIfNull(fetcher);

    Task<IReadOnlyList<ChainNode>>? taskToAwait;
    List<ChainNode>? staleNodes;

    lock (_lock)
    {
      // ── Cache hit (fresh) — return immediately, background refresh ──
      if (_nodes is not null && DateTime.UtcNow - _timestamp < _ttl)
      {
        staleNodes = _nodes;
        EnsureInflightRefreshLocked(fetcher);
        return staleNodes.AsReadOnly();
      }

      // ── Cache miss or stale — need a fresh fetch ──
      staleNodes = _nodes; // may be null (cold start) or stale
      taskToAwait = EnsureInflightRefreshLocked(fetcher);
    }

    // Await the (possibly shared) inflight fetch
    try
    {
      var result = await taskToAwait.ConfigureAwait(false);
      return result;
    }
    catch
    {
      // Fetch failed — return stale data if available, otherwise rethrow
      if (staleNodes is not null)
        return staleNodes.AsReadOnly();

      throw;
    }
  }

  // ─── Flush ────────────────────────────────────────────────────────────

  /// <summary>
  /// Force clear the cache. The next <see cref="GetAsync"/> call will
  /// perform a fresh fetch.
  /// </summary>
  public void Flush()
  {
    lock (_lock)
    {
      _nodes = null;
      _timestamp = DateTime.MinValue;
      _inflightRefresh = null;
    }
  }

  // ─── Private Helpers ──────────────────────────────────────────────────

  /// <summary>
  /// Ensure exactly one inflight refresh task exists. Must be called while
  /// holding <see cref="_lock"/>. Returns the task to await.
  /// </summary>
  private Task<IReadOnlyList<ChainNode>> EnsureInflightRefreshLocked(
    Func<Task<IReadOnlyList<ChainNode>>> fetcher)
  {
    if (_inflightRefresh is not null)
      return _inflightRefresh;

    _inflightRefresh = RefreshAsync(fetcher);
    return _inflightRefresh;
  }

  /// <summary>
  /// Execute the fetch, update the cache, and clear the inflight slot.
  /// Runs outside the lock to avoid blocking other callers during I/O.
  /// </summary>
  private async Task<IReadOnlyList<ChainNode>> RefreshAsync(
    Func<Task<IReadOnlyList<ChainNode>>> fetcher)
  {
    try
    {
      var nodes = await fetcher().ConfigureAwait(false);
      var list = nodes is List<ChainNode> l ? l : new List<ChainNode>(nodes);

      lock (_lock)
      {
        _nodes = list;
        _timestamp = DateTime.UtcNow;
      }

      return list.AsReadOnly();
    }
    finally
    {
      lock (_lock)
      {
        _inflightRefresh = null;
      }
    }
  }
}
