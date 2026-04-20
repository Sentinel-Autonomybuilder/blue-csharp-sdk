using System.Collections.Concurrent;
using System.Text.Json;

namespace Sentinel.SDK.Core;

// ─── DiskEntry Record ───

/// <summary>
/// A cached payload with its save timestamp and staleness flag.
/// Ported from js-sdk/disk-cache.js line 89-98 (diskLoad return shape).
/// </summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="Data">Cached payload.</param>
/// <param name="SavedAt">Epoch ms when the entry was persisted.</param>
/// <param name="Stale">True if the entry exceeds the caller's <c>maxAgeMs</c>.</param>
public record DiskEntry<T>(T Data, long SavedAt, bool Stale);

/// <summary>
/// Generic in-memory TTL cache with inflight dedup, stale-while-revalidate,
/// and optional disk persistence. Ported from js-sdk/disk-cache.js.
///
/// <para>
/// Use <see cref="CachedAsync"/> for the primary pattern: returns fresh data when
/// within TTL, dedupes concurrent fetches, and falls back to stale in-memory data
/// when the fetch fails.
/// </para>
///
/// <para>
/// Use <see cref="DiskSave"/> / <see cref="DiskLoad{T}"/> for cold-start warmup or
/// cross-process sharing. Files live in <c>~/.sentinel-sdk/cache/{key}.json</c> on
/// Linux/macOS and <c>%LocalAppData%\SentinelVPN\cache\{key}.json</c> on Windows.
/// </para>
/// </summary>
public static class DiskCache
{
    // ─── In-Memory Cache ───

    private sealed class MemEntry
    {
        public object? Data;
        public long Ts;
        public Task<object?>? Inflight;
    }

    private static readonly ConcurrentDictionary<string, MemEntry> _memCache = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ─── Cache Primitives ───

    /// <summary>
    /// Fetch with TTL caching, inflight deduplication, and stale fallback on error.
    /// Ported from js-sdk/disk-cache.js line 29-53.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="key">Cache key.</param>
    /// <param name="ttlMs">Time-to-live in milliseconds.</param>
    /// <param name="fetchFn">Async fetch function invoked on miss.</param>
    /// <returns>Cached or fresh data.</returns>
    public static async Task<T> CachedAsync<T>(string key, int ttlMs, Func<Task<T>> fetchFn)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetchFn);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_memCache.TryGetValue(key, out var existing))
        {
            // Fresh hit
            if (existing.Data is T fresh && (now - existing.Ts) < ttlMs) return fresh;

            // Inflight dedup
            if (existing.Inflight is not null)
            {
                var dedup = await existing.Inflight;
                if (dedup is T typed) return typed;
            }
        }

        var entry = _memCache.GetOrAdd(key, _ => new MemEntry());

        var task = Task.Run<object?>(async () =>
        {
            try
            {
                var data = await fetchFn();
                entry.Data = data;
                entry.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                entry.Inflight = null;
                return data;
            }
            catch
            {
                entry.Inflight = null;
                // Stale fallback — return prior data if present
                if (entry.Data is T stale) return stale;
                throw;
            }
        });

        entry.Inflight = task;

        var result = await task;
        return result is T ret ? ret : throw new InvalidCastException($"DiskCache[{key}] returned wrong type");
    }

    /// <summary>Invalidate a single in-memory cache entry. Ported from disk-cache.js line 56.</summary>
    public static void CacheInvalidate(string key) => _memCache.TryRemove(key, out _);

    /// <summary>Clear all in-memory cache entries. Ported from disk-cache.js line 59.</summary>
    public static void CacheClear() => _memCache.Clear();

    /// <summary>
    /// Get cache entry metadata (for diagnostics). Ported from disk-cache.js line 62-66.
    /// Returns null when the entry is absent.
    /// </summary>
    public static (long AgeMs, bool HasData, bool Inflight)? CacheInfo(string key)
    {
        if (!_memCache.TryGetValue(key, out var e)) return null;
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.Ts;
        return (age, e.Data is not null, e.Inflight is not null);
    }

    // ─── Disk Persistence ───

    private static string CacheDir()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SentinelVPN", "cache");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".sentinel-sdk", "cache");
    }

    /// <summary>
    /// Save data to disk with timestamp. Silent on failure (non-fatal).
    /// Ported from js-sdk/disk-cache.js line 77-83.
    /// </summary>
    public static void DiskSave<T>(string key, T data)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            var dir = CacheDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{key}.json");
            var payload = new { data, savedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var json = JsonSerializer.Serialize(payload, JsonOptions);

            // Atomic write: .tmp → rename
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* disk write failure is non-fatal (parity with JS) */ }
    }

    /// <summary>
    /// Load data from disk cache. Returns null when file is absent or unreadable.
    /// The <c>Stale</c> flag is true when age exceeds <paramref name="maxAgeMs"/>.
    /// Ported from js-sdk/disk-cache.js line 91-99.
    /// </summary>
    public static DiskEntry<T>? DiskLoad<T>(string key, long maxAgeMs)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            var file = Path.Combine(CacheDir(), $"{key}.json");
            if (!File.Exists(file)) return null;

            var raw = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(raw);

            long savedAt = 0;
            if (doc.RootElement.TryGetProperty("savedAt", out var savedAtEl)
                && savedAtEl.ValueKind == JsonValueKind.Number)
            {
                savedAt = savedAtEl.GetInt64();
            }

            if (!doc.RootElement.TryGetProperty("data", out var dataEl)) return null;
            var data = dataEl.Deserialize<T>(JsonOptions);
            if (data is null) return null;

            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - savedAt;
            return new DiskEntry<T>(data, savedAt, age > maxAgeMs);
        }
        catch { return null; }
    }

    /// <summary>
    /// Delete a disk cache entry. Silent on failure. Ported from disk-cache.js line 102-106.
    /// </summary>
    public static void DiskClear(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            var file = Path.Combine(CacheDir(), $"{key}.json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { /* non-fatal */ }
    }
}
