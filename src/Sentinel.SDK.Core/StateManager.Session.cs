using System.Text.Json;

namespace Sentinel.SDK.Core;

// ─── Session History, Poisoning, and Poisoned Keys ───

public static partial class StateManager
{
    // ─── Session Tracking ───

    /// <summary>
    /// Mark a session as poisoned (handshake failed — don't reuse).
    /// Callers checking for reusable sessions should call <see cref="IsSessionPoisoned"/>
    /// to skip sessions that previously failed during handshake.
    /// </summary>
    /// <param name="sessionId">Session ID on chain.</param>
    /// <param name="nodeAddress">Node address associated with the session.</param>
    /// <param name="error">Reason the session was poisoned (truncated to 200 chars).</param>
    public static void MarkSessionPoisoned(string sessionId, string nodeAddress, string error)
    {
        var sessions = LoadSessions();
        sessions[sessionId] = new SessionRecord(
            sessionId,
            nodeAddress,
            "poisoned",
            DateTime.UtcNow.ToString("o"),
            error.Length > 200 ? error[..200] : error
        );
        PruneSessions(sessions);
        SaveSessions(sessions);
    }

    /// <summary>
    /// Check if a session was previously poisoned (handshake failed).
    /// </summary>
    /// <param name="sessionId">Session ID to check.</param>
    /// <returns>True if the session was marked as poisoned.</returns>
    public static bool IsSessionPoisoned(string sessionId)
    {
        var sessions = LoadSessions();
        return sessions.TryGetValue(sessionId, out var record) && record.Status == "poisoned";
    }

    /// <summary>
    /// Mark a session as actively connected.
    /// </summary>
    /// <param name="sessionId">Session ID on chain.</param>
    /// <param name="nodeAddress">Node address hosting the session.</param>
    public static void MarkSessionActive(string sessionId, string nodeAddress)
    {
        var sessions = LoadSessions();
        sessions[sessionId] = new SessionRecord(
            sessionId,
            nodeAddress,
            "active",
            DateTime.UtcNow.ToString("o"),
            null
        );
        SaveSessions(sessions);
    }

    /// <summary>
    /// Get the full session history for debugging and diagnostics.
    /// Returns all tracked sessions (up to <see cref="MaxSessionHistory"/> entries).
    /// </summary>
    /// <returns>Dictionary mapping session IDs to their <see cref="SessionRecord"/>.</returns>
    public static Dictionary<string, SessionRecord> GetSessionHistory()
    {
        return LoadSessions();
    }

    // ─── Poisoned Keys (nodeAddr:sessionId pairs) ───

    /// <summary>Maximum number of poisoned keys to retain on disk.</summary>
    private const int MaxPoisonedKeys = 500;

    /// <summary>
    /// Load poisoned session keys from disk.
    /// Returns an array of "nodeAddr:sessionId" strings used by
    /// the SessionManager to skip sessions that previously failed.
    /// Ported from js-sdk/state.js line 329-332.
    /// </summary>
    /// <returns>Array of poisoned key strings, or empty array if none exist.</returns>
    public static string[] LoadPoisonedKeys()
    {
        // Ported from js-sdk/state.js line 329-332
        try
        {
            if (!File.Exists(SessionsFile)) return [];
            var json = File.ReadAllText(SessionsFile);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("poisoned", out var poisonedEl))
                return [];

            if (poisonedEl.ValueKind != JsonValueKind.Array)
                return [];

            var keys = new List<string>();
            foreach (var item in poisonedEl.EnumerateArray())
            {
                var val = item.GetString();
                if (val is not null)
                    keys.Add(val);
            }

            return [.. keys];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Save poisoned session keys to disk.
    /// Persists alongside the sessions data in sessions.json.
    /// Keeps only the last <see cref="MaxPoisonedKeys"/> entries to prevent unbounded growth.
    /// Ported from js-sdk/state.js line 338-343.
    /// </summary>
    /// <param name="keys">Array of "nodeAddr:sessionId" strings to persist.</param>
    public static void SavePoisonedKeys(string[] keys)
    {
        // Ported from js-sdk/state.js line 338-343
        try
        {
            EnsureStateDir();

            // Load existing sessions data to preserve it
            Dictionary<string, object> wrapper;
            try
            {
                if (File.Exists(SessionsFile))
                {
                    var existingJson = File.ReadAllText(SessionsFile);
                    using var doc = JsonDocument.Parse(existingJson);

                    wrapper = new Dictionary<string, object>();

                    // Preserve existing "sessions" property
                    if (doc.RootElement.TryGetProperty("sessions", out var sessionsEl))
                    {
                        var sessions = LoadSessions();
                        wrapper["sessions"] = sessions;
                    }
                    else
                    {
                        wrapper["sessions"] = new Dictionary<string, SessionRecord>();
                    }
                }
                else
                {
                    wrapper = new Dictionary<string, object>
                    {
                        ["sessions"] = new Dictionary<string, SessionRecord>(),
                    };
                }
            }
            catch
            {
                wrapper = new Dictionary<string, object>
                {
                    ["sessions"] = new Dictionary<string, SessionRecord>(),
                };
            }

            // Keep last MaxPoisonedKeys entries — ported from js-sdk/state.js line 341
            var trimmedKeys = keys.Length > MaxPoisonedKeys
                ? keys[^MaxPoisonedKeys..]
                : keys;

            wrapper["poisoned"] = trimmedKeys;

            var json = JsonSerializer.Serialize(wrapper, JsonOptions);
            AtomicWrite(SessionsFile, json);
        }
        catch
        {
            // Best-effort — non-fatal if write fails
        }
    }
}
