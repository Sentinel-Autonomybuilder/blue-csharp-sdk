using System.Collections.Concurrent;
using System.Text.Json;

namespace Sentinel.SDK.Core;

// ─── Payment Mode Enum ───

/// <summary>
/// Payment mode for a session. Ported from js-sdk/session-tracker.js.
/// The chain doesn't distinguish these — we persist them so apps can
/// restore the correct pricing UI after restart.
/// </summary>
public static class PaymentMode
{
    /// <summary>Pay-per-gigabyte session (default for unknown sessions).</summary>
    public const string Gb = "gb";

    /// <summary>Pay-per-hour session.</summary>
    public const string Hour = "hour";

    /// <summary>Plan subscription session (operator pays gas via fee grant).</summary>
    public const string Plan = "plan";

    /// <summary>All valid payment mode strings.</summary>
    public static readonly string[] All = [Gb, Hour, Plan];
}

/// <summary>
/// Persists the payment mode (<c>gb</c> / <c>hour</c> / <c>plan</c>) per session ID.
/// The chain doesn't distinguish these, so UIs need this tracker to display
/// the correct pricing model after a restart.
///
/// <para>Ported from js-sdk/session-tracker.js (81 lines). File layout matches JS:
/// <c>~/.sentinel-sdk/session-modes.json</c> is a flat <c>{ sessionId: mode }</c> map.
/// On Windows, the file lives under <c>%LocalAppData%\SentinelVPN\session-modes.json</c>.</para>
/// </summary>
public sealed class SessionModeTracker
{
    private const string FileName = "session-modes.json";

    private readonly ConcurrentDictionary<string, string> _modes = new();
    private readonly string _file;
    private readonly object _diskLock = new();
    private readonly ISdkLogger? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Create a tracker rooted at <paramref name="stateDir"/> (default: platform state dir).
    /// Ported from js-sdk/session-tracker.js line 20-21.
    /// </summary>
    public SessionModeTracker(string? stateDir = null, ISdkLogger? logger = null)
    {
        _logger = logger;
        var dir = stateDir ?? GetDefaultStateDir();
        _file = Path.Combine(dir, FileName);
        Load();
    }

    /// <summary>
    /// Track the payment mode for a session. Ported from session-tracker.js line 48-52.
    /// </summary>
    /// <param name="sessionId">Session ID (any numeric or string form).</param>
    /// <param name="mode">One of <see cref="PaymentMode.Gb"/>, <c>Hour</c>, <c>Plan</c>.</param>
    public void TrackSession(object sessionId, string mode)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(mode);
        if (!PaymentMode.All.Contains(mode))
            throw new ArgumentException($"Invalid payment mode: '{mode}'. Expected one of: {string.Join(", ", PaymentMode.All)}");

        _modes[sessionId.ToString()!] = mode;
        Save();
    }

    /// <summary>
    /// Get the tracked mode. Defaults to <see cref="PaymentMode.Gb"/> for unknown IDs
    /// (parity with js-sdk/session-tracker.js line 61).
    /// </summary>
    public string GetSessionMode(object sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return _modes.TryGetValue(sessionId.ToString()!, out var m) ? m : PaymentMode.Gb;
    }

    /// <summary>
    /// Snapshot of all tracked sessions. Ported from session-tracker.js line 68-70.
    /// </summary>
    public Dictionary<string, string> GetAllTrackedSessions()
        => new(_modes);

    /// <summary>
    /// Clear tracking for a single session. Ported from session-tracker.js line 76-80.
    /// </summary>
    public void ClearSessionMode(object sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (_modes.TryRemove(sessionId.ToString()!, out _)) Save();
    }

    // ─── Disk Persistence ───

    private void Load()
    {
        lock (_diskLock)
        {
            try
            {
                if (!File.Exists(_file)) return;
                var json = File.ReadAllText(_file);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (map is null) return;
                foreach (var kvp in map) _modes[kvp.Key] = kvp.Value;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"SessionModeTracker: failed to load from disk: {ex.Message}");
            }
        }
    }

    private void Save()
    {
        lock (_diskLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_file)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(new Dictionary<string, string>(_modes), JsonOptions);

                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"SessionModeTracker: failed to save to disk: {ex.Message}");
            }
        }
    }

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
}
