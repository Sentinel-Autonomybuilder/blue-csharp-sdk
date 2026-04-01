using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.SDK.Core;

// ─── Session Tracker Record ───

/// <summary>
/// A tracked session entry with lifecycle status and optional error reason.
/// Ported from js-sdk/session-manager.js line 67-68 (_poisoned Set + session map).
/// </summary>
/// <param name="SessionId">Session ID on chain (as string for JSON compatibility).</param>
/// <param name="NodeAddress">Node address associated with this session.</param>
/// <param name="Status">Status string: "active", "poisoned", "ended".</param>
/// <param name="Timestamp">ISO 8601 timestamp of the last status change.</param>
/// <param name="Error">Error message if poisoned, null otherwise.</param>
public record TrackedSession(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("nodeAddress")] string NodeAddress,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("error")] string? Error
);

/// <summary>
/// Thread-safe session tracker with poisoned-session detection and disk persistence.
///
/// The JS SDK has two layers of session poisoning:
/// 1. StateManager (state.js): markSessionPoisoned / isSessionPoisoned — per session ID
/// 2. SessionManager (session-manager.js): _poisoned Set of "nodeAddr:sessionId" keys — per node+session pair
///
/// This class ports the SessionManager's poisoning pattern (layer 2) as a standalone,
/// importable module. It uses ConcurrentDictionary for thread safety and persists
/// to %LocalAppData%\SentinelVPN\session-tracker.json.
///
/// Ported from js-sdk/session-manager.js lines 60-293.
/// </summary>
public sealed class SessionTracker : IDisposable
{
    // ─── Constants ───

    /// <summary>Maximum number of poisoned keys retained on disk.</summary>
    /// Ported from js-sdk/state.js line 340: keys.length > 500 ? keys.slice(-500) : keys
    private const int MaxPoisonedKeys = 500;

    /// <summary>Maximum number of session history entries retained.</summary>
    private const int MaxSessionHistory = 500;

    /// <summary>File name for session tracker persistence.</summary>
    private const string TrackerFileName = "session-tracker.json";

    // ─── Fields ───

    /// <summary>Thread-safe set of poisoned keys: "nodeAddr:sessionId".</summary>
    /// Ported from js-sdk/session-manager.js line 68: this._poisoned = new Set(loadPoisonedKeys())
    private readonly ConcurrentDictionary<string, byte> _poisonedKeys = new();

    /// <summary>Thread-safe session history: sessionId -> TrackedSession.</summary>
    private readonly ConcurrentDictionary<string, TrackedSession> _sessions = new();

    /// <summary>Full path to the tracker persistence file.</summary>
    private readonly string _trackerFile;

    /// <summary>Optional logger.</summary>
    private readonly ISdkLogger? _logger;

    /// <summary>Lock for disk I/O to prevent concurrent writes.</summary>
    private readonly object _diskLock = new();

    // ─── JSON Options ───

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ─── Constructor ───

    /// <summary>
    /// Create a new SessionTracker, loading persisted state from disk.
    /// Ported from js-sdk/session-manager.js line 68: this._poisoned = new Set(loadPoisonedKeys())
    /// </summary>
    /// <param name="stateDir">
    /// Directory for persistence. Defaults to %LocalAppData%\SentinelVPN (Windows)
    /// or ~/.sentinel-sdk (Linux/macOS).
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SessionTracker(string? stateDir = null, ISdkLogger? logger = null)
    {
        _logger = logger;

        var dir = stateDir ?? GetDefaultStateDir();
        _trackerFile = Path.Combine(dir, TrackerFileName);

        // Load persisted poisoned keys from disk
        // Ported from js-sdk/session-manager.js line 68
        LoadFromDisk();
    }

    // ─── Session Poisoning ───
    // Ported from js-sdk/session-manager.js lines 263-293

    /// <summary>
    /// Mark a session as poisoned (failed handshake — should not be reused).
    /// Persists to disk immediately.
    /// Ported from js-sdk/session-manager.js line 271-273.
    /// </summary>
    /// <param name="sessionId">Session ID on chain.</param>
    /// <param name="nodeAddress">Node address associated with the session.</param>
    /// <param name="error">Reason the session was poisoned (truncated to 200 chars).</param>
    public void MarkSessionPoisoned(ulong sessionId, string error)
    {
        MarkSessionPoisoned(sessionId.ToString(), "", error);
    }

    /// <summary>
    /// Mark a session as poisoned with full node address tracking.
    /// Ported from js-sdk/session-manager.js line 271-273 + js-sdk/state.js line 274-289.
    /// </summary>
    /// <param name="sessionId">Session ID (string).</param>
    /// <param name="nodeAddress">Node address.</param>
    /// <param name="error">Reason the session was poisoned (truncated to 200 chars).</param>
    public void MarkSessionPoisoned(string sessionId, string nodeAddress, string error)
    {
        // Ported from js-sdk/session-manager.js line 272: this._poisoned.add(`${nodeAddr}:${sessionId}`)
        var key = $"{nodeAddress}:{sessionId}";
        _poisonedKeys.TryAdd(key, 0);

        // Also track in session history
        // Ported from js-sdk/state.js line 276-281
        var truncatedError = error.Length > 200 ? error[..200] : error;
        _sessions[sessionId] = new TrackedSession(
            sessionId,
            nodeAddress,
            "poisoned",
            DateTime.UtcNow.ToString("o"),
            truncatedError
        );

        // Persist to disk
        // Ported from js-sdk/session-manager.js line 273: savePoisonedKeys([...this._poisoned])
        SaveToDisk();
    }

    /// <summary>
    /// Check if a session was previously poisoned (handshake failed).
    /// Ported from js-sdk/state.js line 311-314.
    /// </summary>
    /// <param name="sessionId">Session ID to check.</param>
    /// <returns>True if the session was marked as poisoned.</returns>
    public bool IsSessionPoisoned(ulong sessionId)
    {
        return IsSessionPoisoned(sessionId.ToString());
    }

    /// <summary>
    /// Check if a session was previously poisoned by session ID alone.
    /// Ported from js-sdk/state.js line 311-314.
    /// </summary>
    /// <param name="sessionId">Session ID (string).</param>
    /// <returns>True if the session was marked as poisoned.</returns>
    public bool IsSessionPoisoned(string sessionId)
    {
        // Ported from js-sdk/state.js line 313:
        // return data.sessions[String(sessionId)]?.status === 'poisoned';
        return _sessions.TryGetValue(sessionId, out var record) && record.Status == "poisoned";
    }

    /// <summary>
    /// Check if a specific node+session pair is poisoned.
    /// Ported from js-sdk/session-manager.js line 283-285.
    /// </summary>
    /// <param name="nodeAddress">Node address.</param>
    /// <param name="sessionId">Session ID.</param>
    /// <returns>True if this exact node+session combination was poisoned.</returns>
    public bool IsPoisoned(string nodeAddress, string sessionId)
    {
        // Ported from js-sdk/session-manager.js line 284:
        // return this._poisoned.has(`${nodeAddr}:${sessionId}`)
        return _poisonedKeys.ContainsKey($"{nodeAddress}:{sessionId}");
    }

    /// <summary>
    /// Mark a session as actively connected (clears poisoned status for that session).
    /// Ported from js-sdk/state.js line 296-304.
    /// </summary>
    /// <param name="sessionId">Session ID (string).</param>
    /// <param name="nodeAddress">Node address.</param>
    public void MarkSessionActive(string sessionId, string nodeAddress)
    {
        // Ported from js-sdk/state.js line 297-303
        _sessions[sessionId] = new TrackedSession(
            sessionId,
            nodeAddress,
            "active",
            DateTime.UtcNow.ToString("o"),
            null
        );

        SaveToDisk();
    }

    /// <summary>
    /// Get full session history for debugging.
    /// Ported from js-sdk/state.js line 320-322.
    /// </summary>
    /// <returns>Dictionary mapping session IDs to their tracked records.</returns>
    public Dictionary<string, TrackedSession> GetSessionHistory()
    {
        // Ported from js-sdk/state.js line 321: return loadSessions().sessions
        return new Dictionary<string, TrackedSession>(_sessions);
    }

    /// <summary>
    /// Load all poisoned keys from disk.
    /// Ported from js-sdk/state.js line 329-332.
    /// </summary>
    /// <returns>Array of "nodeAddr:sessionId" key strings.</returns>
    public string[] LoadPoisonedKeys()
    {
        // Ported from js-sdk/state.js line 330-331
        return [.. _poisonedKeys.Keys];
    }

    /// <summary>
    /// Clear all poisoned session markers.
    /// Ported from js-sdk/session-manager.js line 290-293.
    /// </summary>
    public void ClearPoisonedSessions()
    {
        // Ported from js-sdk/session-manager.js line 291-292
        _poisonedKeys.Clear();
        SaveToDisk();
    }

    // ─── Disk Persistence ───

    /// <summary>
    /// Load tracker state from disk into memory.
    /// Ported from js-sdk/state.js line 249-256 (loadSessions) +
    /// js-sdk/state.js line 329-332 (loadPoisonedKeys).
    /// </summary>
    private void LoadFromDisk()
    {
        lock (_diskLock)
        {
            try
            {
                if (!File.Exists(_trackerFile)) return;

                var json = File.ReadAllText(_trackerFile);
                using var doc = JsonDocument.Parse(json);

                // Load poisoned keys array
                // Ported from js-sdk/state.js line 330-331
                if (doc.RootElement.TryGetProperty("poisoned", out var poisonedEl)
                    && poisonedEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in poisonedEl.EnumerateArray())
                    {
                        var key = item.GetString();
                        if (key is not null)
                        {
                            _poisonedKeys.TryAdd(key, 0);
                        }
                    }
                }

                // Load session history
                // Ported from js-sdk/state.js line 250-255
                if (doc.RootElement.TryGetProperty("sessions", out var sessionsEl)
                    && sessionsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in sessionsEl.EnumerateObject())
                    {
                        var id = prop.Name;
                        var el = prop.Value;
                        var status = el.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                        var nodeAddr = el.TryGetProperty("nodeAddress", out var na) ? na.GetString() ?? "" : "";
                        var timestamp = el.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                        var error = el.TryGetProperty("error", out var err) ? err.GetString() : null;

                        _sessions[id] = new TrackedSession(id, nodeAddr, status, timestamp, error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"SessionTracker: failed to load from disk: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Persist current tracker state to disk using atomic write.
    /// Ported from js-sdk/state.js line 258-265 (saveSessions) +
    /// js-sdk/state.js line 338-343 (savePoisonedKeys).
    /// </summary>
    private void SaveToDisk()
    {
        lock (_diskLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_trackerFile)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Prune sessions to MaxSessionHistory
                // Ported from js-sdk/state.js line 283-287 (prune old entries)
                if (_sessions.Count > MaxSessionHistory)
                {
                    var toRemove = _sessions
                        .OrderByDescending(kvp => kvp.Value.Timestamp)
                        .Skip(MaxSessionHistory)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in toRemove)
                    {
                        _sessions.TryRemove(key, out _);
                    }
                }

                // Prune poisoned keys to MaxPoisonedKeys
                // Ported from js-sdk/state.js line 341: keys.length > 500 ? keys.slice(-500) : keys
                var poisonedArray = _poisonedKeys.Keys.ToArray();
                if (poisonedArray.Length > MaxPoisonedKeys)
                {
                    var toRemove = poisonedArray.Take(poisonedArray.Length - MaxPoisonedKeys);
                    foreach (var key in toRemove)
                    {
                        _poisonedKeys.TryRemove(key, out _);
                    }
                    poisonedArray = _poisonedKeys.Keys.ToArray();
                }

                // Build the JSON structure matching JS format:
                // { "sessions": { ... }, "poisoned": [ ... ] }
                var wrapper = new Dictionary<string, object>
                {
                    ["sessions"] = new Dictionary<string, TrackedSession>(_sessions),
                    ["poisoned"] = poisonedArray,
                };

                var json = JsonSerializer.Serialize(wrapper, JsonOptions);

                // Atomic write: write .tmp then rename
                var tmpFile = _trackerFile + ".tmp";
                File.WriteAllText(tmpFile, json);
                File.Move(tmpFile, _trackerFile, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"SessionTracker: failed to save to disk: {ex.Message}");
            }
        }
    }

    // ─── Helpers ───

    /// <summary>
    /// Get the default state directory for the current platform.
    /// Ported from js-sdk/state.js line 49-50.
    /// </summary>
    private static string GetDefaultStateDir()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SentinelVPN");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".sentinel-sdk");
    }

    /// <summary>
    /// Dispose is a no-op but provided for IDisposable pattern (consumers using `using` blocks).
    /// </summary>
    public void Dispose()
    {
        // No unmanaged resources; persistence is handled synchronously.
    }
}
