using System.Text.Json;

namespace Sentinel.SDK.Core;

// ─── Dynamic Transport Rate Tracking (persisted to disk) ─────────────────────
// Runtime success/failure tracking per transport type. Overrides hardcoded
// transport success rates when enough samples exist. Persisted to
// %LocalAppData%\SentinelVPN\dynamic-rates.json with 7-day TTL eviction on load.
// Mirrors the JS SDK's dynamic rate tracking in defaults.js.

/// <summary>
/// Tracks transport connection success/failure rates at runtime.
/// Persists results to disk so rates survive app restarts.
/// Thread-safe via internal locking.
/// </summary>
public static class DynamicTransportRates
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, RateEntry> _rates = new();
    private static readonly TimeSpan _ttl = TimeSpan.FromDays(7);
    private static bool _loaded;

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelVPN");

    private static string FilePath => Path.Combine(DataDir, "dynamic-rates.json");

    // ─── Internal Entry ──────────────────────────────────────────────

    private sealed class RateEntry
    {
        public int Success { get; set; }
        public int Fail { get; set; }
        public long UpdatedAt { get; set; }
    }

    // ─── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Record a transport connection result. Called automatically after V2Ray setup.
    /// Persists to disk after each call.
    /// </summary>
    /// <param name="transportKey">Transport identifier (e.g. "tcp", "grpc/none", "websocket/tls").</param>
    /// <param name="success">True if the connection succeeded, false if it failed.</param>
    public static void RecordResult(string transportKey, bool success)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportKey);

        lock (_lock)
        {
            EnsureLoaded();

            if (!_rates.TryGetValue(transportKey, out var entry))
            {
                entry = new RateEntry();
                _rates[transportKey] = entry;
            }

            if (success)
                entry.Success++;
            else
                entry.Fail++;

            entry.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveToDisk();
        }
    }

    /// <summary>
    /// Get the dynamic success rate for a transport. Returns null if fewer than 2 samples exist.
    /// Used by transport sorting to prioritize reliable transports.
    /// </summary>
    /// <param name="transportKey">Transport identifier.</param>
    /// <returns>Success rate (0.0-1.0), or null if insufficient samples.</returns>
    public static double? GetRate(string transportKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportKey);

        lock (_lock)
        {
            EnsureLoaded();

            if (!_rates.TryGetValue(transportKey, out var entry))
                return null;

            var total = entry.Success + entry.Fail;
            if (total < 2)
                return null;

            return (double)entry.Success / total;
        }
    }

    /// <summary>
    /// Get all dynamic rates as a dictionary of transport key to (Rate, Samples).
    /// </summary>
    /// <returns>Dictionary mapping transport keys to their rate and sample count.</returns>
    public static Dictionary<string, (double Rate, int Samples)> GetAll()
    {
        lock (_lock)
        {
            EnsureLoaded();

            var result = new Dictionary<string, (double Rate, int Samples)>();
            foreach (var (key, entry) in _rates)
            {
                var total = entry.Success + entry.Fail;
                if (total > 0)
                {
                    result[key] = ((double)entry.Success / total, total);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Clear all dynamic rate data from memory.
    /// When persist is true, also clears the disk file.
    /// </summary>
    /// <param name="persist">When true, write the empty state to disk (clearing the file).</param>
    public static void Reset(bool persist = false)
    {
        lock (_lock)
        {
            _rates.Clear();
            _loaded = true; // Mark as loaded to prevent re-reading cleared data

            if (persist)
            {
                SaveToDisk();
            }
        }
    }

    // ─── Disk Persistence ────────────────────────────────────────────

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(FilePath)) return;

            var json = File.ReadAllText(FilePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (raw == null) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttlMs = (long)_ttl.TotalMilliseconds;

            foreach (var (key, element) in raw)
            {
                var updatedAt = element.TryGetProperty("UpdatedAt", out var ua) ? ua.GetInt64() : 0;

                // Evict stale entries older than TTL
                if (updatedAt > 0 && now - updatedAt > ttlMs)
                    continue;

                var success = element.TryGetProperty("Success", out var s) ? s.GetInt32() : 0;
                var fail = element.TryGetProperty("Fail", out var f) ? f.GetInt32() : 0;

                _rates[key] = new RateEntry
                {
                    Success = success,
                    Fail = fail,
                    UpdatedAt = updatedAt,
                };
            }
        }
        catch
        {
            // Corrupt file or read error — start fresh
        }
    }

    private static void SaveToDisk()
    {
        try
        {
            if (!Directory.Exists(DataDir))
                Directory.CreateDirectory(DataDir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_rates, options);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Disk write failed — rates stay in memory only
        }
    }
}
