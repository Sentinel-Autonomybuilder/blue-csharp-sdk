using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sentinel.SDK.Core;

// ─── State Records ───

/// <summary>
/// Represents the persisted VPN connection state for crash recovery.
/// Saved to disk after each successful connection so orphaned tunnels
/// can be detected and cleaned up on next startup.
/// </summary>
/// <param name="SessionId">Active session ID on chain (numeric string).</param>
/// <param name="ServiceType">Service type: "wireguard" or "v2ray".</param>
/// <param name="WgTunnelName">WireGuard tunnel service name (e.g. "wgsent0").</param>
/// <param name="V2RayPid">V2Ray process PID (if v2ray connection).</param>
/// <param name="SocksPort">SOCKS5 proxy port (if v2ray connection).</param>
/// <param name="SystemProxySet">Whether the OS system proxy was configured.</param>
/// <param name="NodeAddress">Connected node address (sentnode1...).</param>
/// <param name="ConfPath">Path to the WireGuard config file on disk.</param>
/// <param name="SavedAt">ISO 8601 timestamp when the state was saved.</param>
/// <param name="Pid">PID of the process that saved this state.</param>
public record VpnState(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("serviceType")] string? ServiceType,
    [property: JsonPropertyName("wgTunnelName")] string? WgTunnelName,
    [property: JsonPropertyName("v2rayPid")] int? V2RayPid,
    [property: JsonPropertyName("socksPort")] int? SocksPort,
    [property: JsonPropertyName("systemProxySet")] bool SystemProxySet,
    [property: JsonPropertyName("nodeAddress")] string? NodeAddress,
    [property: JsonPropertyName("confPath")] string? ConfPath,
    [property: JsonPropertyName("savedAt")] string SavedAt,
    [property: JsonPropertyName("pid")] int Pid
);

/// <summary>
/// Result of an orphan recovery attempt.
/// </summary>
/// <param name="HadState">True if a saved state file was found.</param>
/// <param name="Cleaned">List of resources that were cleaned up.</param>
public record RecoverResult(bool HadState, string[] Cleaned);

/// <summary>
/// Tracks a session's lifecycle status for poisoned-session detection.
/// </summary>
/// <param name="SessionId">Session ID on chain.</param>
/// <param name="NodeAddress">Node address associated with this session.</param>
/// <param name="Status">Status string: "active", "poisoned", etc.</param>
/// <param name="Timestamp">ISO 8601 timestamp of the last status change.</param>
/// <param name="Error">Error message if the session was poisoned, null otherwise.</param>
public record SessionRecord(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("nodeAddress")] string NodeAddress,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("error")] string? Error
);

/// <summary>
/// Result of checking a PID file.
/// </summary>
/// <param name="Running">True if the process from the PID file is still alive.</param>
/// <param name="Pid">The PID from the file, if it existed.</param>
/// <param name="StartedAt">ISO 8601 timestamp when the process was started, if recorded.</param>
public record PidCheck(bool Running, int? Pid, string? StartedAt);

// ─── State Manager ───

/// <summary>
/// VPN state persistence for crash recovery. Saves and loads connection state to disk
/// so orphaned WireGuard tunnels, V2Ray processes, and system proxy settings can be
/// detected and cleaned up after an unclean shutdown.
/// <para>
/// State directory:
/// <list type="bullet">
///   <item>Windows: <c>%LocalAppData%\SentinelVPN\</c></item>
///   <item>Linux/macOS: <c>~/.sentinel-sdk/</c></item>
/// </list>
/// </para>
/// </summary>
public static class StateManager
{
    // ─── Constants ───

    /// <summary>Maximum number of session records to retain in history.</summary>
    private const int MaxSessionHistory = 200;

    /// <summary>Timeout in milliseconds for external process calls during recovery.</summary>
    private const int ProcessTimeoutMs = 5000;

    /// <summary>Timeout in milliseconds for WireGuard service operations.</summary>
    private const int WireGuardTimeoutMs = 15000;

    /// <summary>
    /// Optional logger for diagnostics. Set via <see cref="SetLogger"/> to route
    /// StateManager warnings through your application's logging framework.
    /// </summary>
    private static ISdkLogger? _logger;

    /// <summary>
    /// Set the logger used by StateManager for diagnostic warnings.
    /// </summary>
    /// <param name="logger">Logger instance, or null to suppress output.</param>
    public static void SetLogger(ISdkLogger? logger) => _logger = logger;

    // ─── Paths ───

    /// <summary>
    /// State directory path. On Windows: <c>%LocalAppData%\SentinelVPN</c>.
    /// On Linux/macOS: <c>~/.sentinel-sdk</c>.
    /// </summary>
    private static readonly string StateDir = GetStateDir();

    /// <summary>Full path to the VPN state file.</summary>
    private static readonly string StateFile = Path.Combine(StateDir, "state.json");

    /// <summary>Full path to the session history file.</summary>
    private static readonly string SessionsFile = Path.Combine(StateDir, "sessions.json");

    // ─── JSON Serialization ───

    /// <summary>Shared JSON options: indented, camelCase, ignore nulls on read.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ─── State Validation (prevents command injection via poisoned state.json) ───

    /// <summary>Validates that all fields in a loaded VPN state are safe to use in process calls.</summary>
    /// <param name="state">The state to validate.</param>
    /// <returns>True if all fields pass validation, false if any field looks corrupted.</returns>
    private static bool ValidateStateValues(VpnState state)
    {
        if (state.SessionId is not null && !Regex.IsMatch(state.SessionId, @"^\d+$"))
        {
            _logger?.Warn($"Corrupted state: invalid sessionId \"{state.SessionId}\" — skipping recovery");
            return false;
        }

        if (state.ServiceType is not null && state.ServiceType != "wireguard" && state.ServiceType != "v2ray")
        {
            _logger?.Warn($"Corrupted state: invalid serviceType \"{state.ServiceType}\" — skipping recovery");
            return false;
        }

        if (state.V2RayPid is not null && state.V2RayPid <= 0)
        {
            _logger?.Warn($"Corrupted state: invalid v2rayPid \"{state.V2RayPid}\" — skipping recovery");
            return false;
        }

        if (state.SocksPort is not null && (state.SocksPort < 1 || state.SocksPort > 65535))
        {
            _logger?.Warn($"Corrupted state: invalid socksPort \"{state.SocksPort}\" — skipping recovery");
            return false;
        }

        if (state.WgTunnelName is not null && !Regex.IsMatch(state.WgTunnelName, @"^[a-zA-Z0-9_\-]{1,64}$"))
        {
            _logger?.Warn($"Corrupted state: invalid wgTunnelName \"{state.WgTunnelName}\" — skipping recovery");
            return false;
        }

        if (state.NodeAddress is not null && !Regex.IsMatch(state.NodeAddress, @"^sentnode1[a-z0-9]{38}$"))
        {
            _logger?.Warn($"Corrupted state: invalid nodeAddress \"{state.NodeAddress}\" — skipping recovery");
            return false;
        }

        if (state.ConfPath is not null)
        {
            if (state.ConfPath.Length > 260)
            {
                _logger?.Warn("Corrupted state: confPath too long — skipping recovery");
                return false;
            }

            var isWindowsPath = Regex.IsMatch(state.ConfPath, @"^[a-zA-Z]:[\\\/][a-zA-Z0-9_.\-\\\/ ]+$");
            var isUnixPath = Regex.IsMatch(state.ConfPath, @"^\/[a-zA-Z0-9_.\-\/ ]+$");
            if (!isWindowsPath && !isUnixPath)
            {
                _logger?.Warn($"Corrupted state: invalid confPath \"{state.ConfPath}\" — skipping recovery");
                return false;
            }
        }

        return true;
    }

    // ─── State Persistence ───

    /// <summary>
    /// Save current VPN connection state to disk for crash recovery.
    /// Uses atomic write (write to .tmp, then rename) to prevent corruption.
    /// Call this after a successful connection is established.
    /// </summary>
    /// <param name="state">The VPN state to persist.</param>
    public static void SaveState(VpnState state)
    {
        try
        {
            EnsureStateDir();
            var json = JsonSerializer.Serialize(state, JsonOptions);
            AtomicWrite(StateFile, json);
        }
        catch (Exception e)
        {
            _logger?.Warn($"saveState warning: {e.Message}");
        }
    }

    /// <summary>
    /// Load saved VPN state from disk.
    /// Returns null if no state file exists, the file is corrupt, or deserialization fails.
    /// </summary>
    /// <returns>The persisted <see cref="VpnState"/>, or null if unavailable.</returns>
    public static VpnState? LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            var json = File.ReadAllText(StateFile);
            return JsonSerializer.Deserialize<VpnState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Clear saved VPN state from disk.
    /// Call this after a clean disconnect to prevent false orphan detection on next startup.
    /// </summary>
    public static void ClearState()
    {
        try
        {
            if (File.Exists(StateFile)) File.Delete(StateFile);
        }
        catch (Exception e)
        {
            _logger?.Warn($"clearState warning: {e.Message}");
        }
    }

    // ─── Orphan Recovery ───

    /// <summary>
    /// Detect and clean up orphaned tunnels, processes, and proxy settings from a previous crash.
    /// <para>
    /// Call this at application startup. It loads the saved state file, checks if the process
    /// that created it is still alive, and if not, cleans up:
    /// <list type="bullet">
    ///   <item>Orphaned V2Ray process (killed by PID)</item>
    ///   <item>Orphaned WireGuard tunnel service (uninstalled)</item>
    ///   <item>Stuck system proxy settings (reset to direct)</item>
    ///   <item>Stale config files (deleted)</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <returns>
    /// A <see cref="RecoverResult"/> describing what was found and cleaned,
    /// or a result with <c>HadState = false</c> if no saved state existed.
    /// </returns>
    public static RecoverResult RecoverOrphans()
    {
        var state = LoadState();
        if (state is null)
        {
            return new RecoverResult(false, []);
        }

        // Validate state values before using them in shell commands
        if (!ValidateStateValues(state))
        {
            ClearState();
            return new RecoverResult(true, ["Corrupted state file removed"]);
        }

        var cleaned = new List<string>();

        // Check if the process that saved the state is still running
        var processAlive = false;
        if (state.Pid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(state.Pid);
                processAlive = !proc.HasExited;
            }
            catch
            {
                // Process not found — it has exited
                processAlive = false;
            }
        }

        // If the original process is still running, don't touch anything
        if (processAlive)
        {
            return new RecoverResult(true, []);
        }

        // Clean up orphaned V2Ray
        if (state.ServiceType == "v2ray" && state.V2RayPid is > 0)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RunProcess("taskkill", ["/F", "/PID", state.V2RayPid.Value.ToString()],
                        ProcessTimeoutMs);
                }
                else
                {
                    try
                    {
                        var v2proc = Process.GetProcessById(state.V2RayPid.Value);
                        v2proc.Kill(true);
                    }
                    catch
                    {
                        // Already dead — expected
                    }
                }

                cleaned.Add($"v2ray PID {state.V2RayPid}");
            }
            catch
            {
                // Already dead — expected if process exited naturally
            }
        }

        // Clean up orphaned WireGuard tunnel
        if (state.ServiceType == "wireguard" && state.WgTunnelName is not null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var output = RunProcess("sc",
                        ["query", $"WireGuardTunnel${state.WgTunnelName}"],
                        ProcessTimeoutMs);

                    if (output.Contains("RUNNING") || output.Contains("STOPPED"))
                    {
                        // Find wireguard.exe
                        string? wgExe = new[]
                            {
                                @"C:\Program Files\WireGuard\wireguard.exe",
                                @"C:\Program Files (x86)\WireGuard\wireguard.exe",
                            }
                            .FirstOrDefault(File.Exists);

                        if (wgExe is not null)
                        {
                            RunProcess(wgExe,
                                ["/uninstalltunnelservice", state.WgTunnelName],
                                WireGuardTimeoutMs);
                            cleaned.Add($"WireGuard tunnel {state.WgTunnelName}");
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger?.Warn($"WG orphan cleanup warning: {e.Message}");
                }
            }
            else
            {
                // Linux/macOS: use wg-quick to remove stale tunnel
                try
                {
                    RunProcess("wg-quick", ["down", state.WgTunnelName], 10000);
                    cleaned.Add($"WireGuard tunnel {state.WgTunnelName} (wg-quick down)");
                }
                catch (Exception e)
                {
                    _logger?.Warn($"wg-quick down warning: {e.Message}");
                }
            }
        }

        // Clean up orphaned system proxy
        if (state.SystemProxySet)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    const string regKey =
                        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                    RunProcess("reg",
                        ["add", regKey, "/v", "ProxyEnable", "/t", "REG_DWORD", "/d", "0", "/f"],
                        ProcessTimeoutMs);
                    RunProcess("reg",
                        ["delete", regKey, "/v", "ProxyServer", "/f"],
                        ProcessTimeoutMs);
                    cleaned.Add("Windows system proxy");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var servicesOutput = RunProcess("networksetup",
                        ["-listallnetworkservices"], ProcessTimeoutMs);
                    var services = servicesOutput
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(s => !s.StartsWith('*') && !s.StartsWith("An asterisk"));

                    foreach (var svc in services)
                    {
                        try
                        {
                            RunProcess("networksetup",
                                ["-setsocksfirewallproxystate", svc.Trim(), "off"],
                                ProcessTimeoutMs);
                        }
                        catch
                        {
                            // Service may not have proxy enabled
                        }
                    }

                    cleaned.Add("macOS system proxy");
                }
                else
                {
                    // Linux (GNOME)
                    RunProcess("gsettings",
                        ["set", "org.gnome.system.proxy", "mode", "none"],
                        ProcessTimeoutMs);
                    cleaned.Add("Linux system proxy (GNOME)");
                }
            }
            catch (Exception e)
            {
                _logger?.Warn($"proxy orphan cleanup warning: {e.Message}");
            }
        }

        // Clean up stale config file
        if (state.ConfPath is not null && File.Exists(state.ConfPath))
        {
            try
            {
                File.Delete(state.ConfPath);
            }
            catch (Exception e)
            {
                _logger?.Warn($"conf cleanup warning: {e.Message}");
            }
        }

        ClearState();
        return new RecoverResult(true, [.. cleaned]);
    }

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
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("poisoned", out var poisonedEl))
                return [];

            if (poisonedEl.ValueKind != System.Text.Json.JsonValueKind.Array)
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
                    using var doc = System.Text.Json.JsonDocument.Parse(existingJson);

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

            var json = System.Text.Json.JsonSerializer.Serialize(wrapper, JsonOptions);
            AtomicWrite(SessionsFile, json);
        }
        catch
        {
            // Best-effort — non-fatal if write fails
        }
    }

    // ─── PID File ───

    /// <summary>
    /// Write a PID file for the current process.
    /// Use at server/application startup to enable detection of stale instances.
    /// The PID file is stored at <c>{StateDir}/{name}.pid</c>.
    /// </summary>
    /// <param name="name">Application name (defaults to "app"). Creates <c>{name}.pid</c>.</param>
    /// <returns>Full path to the written PID file.</returns>
    public static string WritePidFile(string name = "app")
    {
        try
        {
            EnsureStateDir();
            var pidFile = Path.Combine(StateDir, $"{name}.pid");
            var data = new
            {
                pid = Environment.ProcessId,
                startedAt = DateTime.UtcNow.ToString("o"),
            };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            AtomicWrite(pidFile, json);
            return pidFile;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Check if a previous instance is running from a PID file.
    /// If the PID file exists but the process is dead, the stale file is removed.
    /// </summary>
    /// <param name="name">Application name (defaults to "app").</param>
    /// <returns>
    /// A <see cref="PidCheck"/> indicating whether the process is running,
    /// and if so, its PID and start time.
    /// </returns>
    public static PidCheck CheckPidFile(string name = "app")
    {
        try
        {
            var pidFile = Path.Combine(StateDir, $"{name}.pid");
            if (!File.Exists(pidFile))
            {
                return new PidCheck(false, null, null);
            }

            var json = File.ReadAllText(pidFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var pid = root.GetProperty("pid").GetInt32();
            var startedAt = root.TryGetProperty("startedAt", out var sa) ? sa.GetString() : null;

            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    return new PidCheck(true, pid, startedAt);
                }
            }
            catch
            {
                // Process not found — stale PID file
            }

            // Process is dead — clean up stale PID file
            try { File.Delete(pidFile); } catch { /* best-effort */ }
            return new PidCheck(false, pid, null);
        }
        catch
        {
            return new PidCheck(false, null, null);
        }
    }

    /// <summary>
    /// Remove the PID file for the given application name.
    /// Call this on clean shutdown to prevent stale PID detection on next startup.
    /// </summary>
    /// <param name="name">Application name (defaults to "app").</param>
    public static void ClearPidFile(string name = "app")
    {
        try
        {
            var pidFile = Path.Combine(StateDir, $"{name}.pid");
            if (File.Exists(pidFile)) File.Delete(pidFile);
        }
        catch
        {
            // Best-effort cleanup — non-fatal
        }
    }

    // ─── Private Helpers ───

    /// <summary>
    /// Determine the platform-appropriate state directory.
    /// </summary>
    private static string GetStateDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SentinelVPN");
        }

        // Linux / macOS
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".sentinel-sdk");
    }

    /// <summary>
    /// Ensure the state directory exists with restricted permissions.
    /// On Windows, sets ACL to owner-only access. On Unix, sets mode 0700.
    /// </summary>
    private static void EnsureStateDir()
    {
        if (Directory.Exists(StateDir)) return;

        var dir = Directory.CreateDirectory(StateDir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // Restrict access to current user only
                var dirInfo = new DirectoryInfo(StateDir);
                var security = dirInfo.GetAccessControl();
                security.SetAccessRuleProtection(true, false); // disable inheritance
                var currentUser = WindowsIdentity.GetCurrent();
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser.Name,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dirInfo.SetAccessControl(security);
            }
            catch
            {
                // Non-fatal — directory still usable, just less restricted
            }
        }
        else
        {
            // Unix: set directory to 0700 via UnixFileMode
            try
            {
                File.SetUnixFileMode(StateDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Non-fatal on platforms that don't support UnixFileMode
            }
        }
    }

    /// <summary>
    /// Atomically write content to a file by writing to a .tmp file first, then renaming.
    /// This prevents corruption if the process crashes mid-write.
    /// On Windows, sets file attributes to restrict access. On Unix, sets mode 0600.
    /// </summary>
    /// <param name="filePath">Target file path.</param>
    /// <param name="content">UTF-8 content to write.</param>
    private static void AtomicWrite(string filePath, string content)
    {
        var tmpFile = filePath + ".tmp";
        File.WriteAllText(tmpFile, content);

        // Set restrictive permissions on the temp file before renaming
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(tmpFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Non-fatal on platforms that don't support UnixFileMode
            }
        }

        // Atomic rename (File.Move with overwrite)
        File.Move(tmpFile, filePath, overwrite: true);

        // On Windows, restrict access via ACL after rename
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var security = fileInfo.GetAccessControl();
                security.SetAccessRuleProtection(true, false);
                var currentUser = WindowsIdentity.GetCurrent();
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser.Name,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                fileInfo.SetAccessControl(security);
            }
            catch
            {
                // Non-fatal
            }
        }
    }

    /// <summary>
    /// Load session history from the sessions file.
    /// Returns an empty dictionary if the file doesn't exist or is corrupt.
    /// </summary>
    private static Dictionary<string, SessionRecord> LoadSessions()
    {
        try
        {
            if (!File.Exists(SessionsFile)) return new Dictionary<string, SessionRecord>();
            var json = File.ReadAllText(SessionsFile);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("sessions", out var sessionsEl))
            {
                return new Dictionary<string, SessionRecord>();
            }

            var result = new Dictionary<string, SessionRecord>();
            foreach (var prop in sessionsEl.EnumerateObject())
            {
                var id = prop.Name;
                var el = prop.Value;
                var status = el.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                var nodeAddr = el.TryGetProperty("nodeAddress", out var na) ? na.GetString() ?? "" : "";
                var timestamp = el.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                var error = el.TryGetProperty("error", out var err) ? err.GetString() : null;

                result[id] = new SessionRecord(id, nodeAddr, status, timestamp, error);
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, SessionRecord>();
        }
    }

    /// <summary>
    /// Persist session history to disk using atomic write.
    /// </summary>
    /// <param name="sessions">Session records to persist.</param>
    private static void SaveSessions(Dictionary<string, SessionRecord> sessions)
    {
        try
        {
            EnsureStateDir();
            // Wrap in { sessions: { ... } } to match JS format
            var wrapper = new Dictionary<string, object> { ["sessions"] = sessions };
            var json = JsonSerializer.Serialize(wrapper, JsonOptions);
            AtomicWrite(SessionsFile, json);
        }
        catch
        {
            // Best-effort session tracking — non-fatal if write fails
        }
    }

    /// <summary>
    /// Prune session history to keep only the most recent <see cref="MaxSessionHistory"/> entries.
    /// </summary>
    /// <param name="sessions">Session dictionary to prune in-place.</param>
    private static void PruneSessions(Dictionary<string, SessionRecord> sessions)
    {
        if (sessions.Count <= MaxSessionHistory) return;

        var toRemove = sessions
            .OrderByDescending(kvp => kvp.Value.Timestamp)
            .Skip(MaxSessionHistory)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            sessions.Remove(key);
        }
    }

    /// <summary>
    /// Run an external process with arguments, capturing stdout.
    /// Throws if the process fails or times out.
    /// </summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="args">Arguments to pass.</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    /// <returns>Standard output from the process.</returns>
    private static string RunProcess(string fileName, string[] args, int timeoutMs)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            proc.StartInfo.ArgumentList.Add(arg);
        }

        proc.Start();

        // Read stdout and stderr asynchronously to prevent deadlock when
        // either buffer fills (OS pipe buffer is ~4KB-64KB depending on platform).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(true); } catch { /* best-effort */ }
            throw new TimeoutException($"Process {fileName} timed out after {timeoutMs}ms");
        }

        // Ensure async reads complete after process exit
        stdoutTask.Wait(timeoutMs);
        stderrTask.Wait(timeoutMs);

        return stdoutTask.Result;
    }
}
