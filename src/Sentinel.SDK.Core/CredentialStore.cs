using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.SDK.Core;

// ─── Saved Credentials Record ───

/// <summary>
/// Saved handshake credentials for fast reconnection.
/// Persisted per node so subsequent connections to the same node
/// can skip both payment and handshake when the session is still active.
/// </summary>
public record SavedCredentials
{
    /// <summary>On-chain session ID (numeric string).</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    /// <summary>Service type: "wireguard" or "v2ray".</summary>
    [JsonPropertyName("serviceType")]
    public string ServiceType { get; init; } = "";

    /// <summary>Node address (sentnode1...).</summary>
    [JsonPropertyName("nodeAddress")]
    public string NodeAddress { get; init; } = "";

    // ─── WireGuard Fields ───

    /// <summary>Client X25519 private key (base64-encoded). Null for V2Ray credentials.</summary>
    [JsonPropertyName("wgPrivateKey")]
    public string? WgPrivateKey { get; init; }

    /// <summary>Server X25519 public key (base64-encoded). Null for V2Ray credentials.</summary>
    [JsonPropertyName("wgServerPubKey")]
    public string? WgServerPubKey { get; init; }

    /// <summary>Assigned VPN addresses (e.g. ["10.8.0.2/24", "fd1d::2/128"]). Null for V2Ray credentials.</summary>
    [JsonPropertyName("wgAssignedAddrs")]
    public string[]? WgAssignedAddrs { get; init; }

    /// <summary>Server WireGuard endpoint in "ip:port" format. Null for V2Ray credentials.</summary>
    [JsonPropertyName("wgServerEndpoint")]
    public string? WgServerEndpoint { get; init; }

    // ─── V2Ray Fields ───

    /// <summary>UUID string for VLess/VMess authentication. Null for WireGuard credentials.</summary>
    [JsonPropertyName("v2rayUuid")]
    public string? V2RayUuid { get; init; }

    /// <summary>V2Ray transport type (1=ds, 2=gun, 3=grpc, 4=http, 5=kcp, 6=quic, 7=tcp, 8=ws). Null for WireGuard credentials.</summary>
    [JsonPropertyName("v2rayTransport")]
    public int? V2RayTransport { get; init; }

    /// <summary>Proxy protocol (1=VLess, 2=VMess). Null for WireGuard credentials.</summary>
    [JsonPropertyName("v2rayProtocol")]
    public int? V2RayProtocol { get; init; }

    /// <summary>TLS mode (0=none, 1=tls). Null for WireGuard credentials.</summary>
    [JsonPropertyName("v2rayTls")]
    public int? V2RayTls { get; init; }

    /// <summary>Listening port on the node. Null for WireGuard credentials.</summary>
    [JsonPropertyName("v2rayPort")]
    public int? V2RayPort { get; init; }

    /// <summary>V2Ray server host (IP address from node remote URL). Null for WireGuard credentials.</summary>
    [JsonPropertyName("v2rayServerHost")]
    public string? V2RayServerHost { get; init; }

    /// <summary>ISO 8601 timestamp when the credentials were saved.</summary>
    [JsonPropertyName("savedAt")]
    public string SavedAt { get; init; } = "";
}

// ─── Credential Store ───

/// <summary>
/// Persists VPN handshake credentials per node so reconnections skip payment AND handshake.
/// Stored at the same directory as <see cref="StateManager"/>:
/// <list type="bullet">
///   <item>Windows: <c>%LocalAppData%\SentinelVPN\credentials.json</c></item>
///   <item>Linux/macOS: <c>~/.sentinel-sdk/credentials.json</c></item>
/// </list>
/// <para>
/// Thread-safe. Uses atomic writes (write .tmp, then rename) and owner-only file permissions.
/// Maximum 100 entries; oldest entries are pruned on save.
/// </para>
/// </summary>
public static class CredentialStore
{
    // ─── Constants ───

    /// <summary>Maximum number of credential entries to retain.</summary>
    private const int MaxEntries = 100;

    /// <summary>Thread-safety lock for all file operations.</summary>
    private static readonly object Lock = new();

    /// <summary>
    /// Optional logger for diagnostics. Set via <see cref="SetLogger"/> to route
    /// CredentialStore warnings through your application's logging framework.
    /// </summary>
    private static ISdkLogger? _logger;

    /// <summary>
    /// Set the logger used by CredentialStore for diagnostic warnings.
    /// </summary>
    /// <param name="logger">Logger instance, or null to suppress output.</param>
    public static void SetLogger(ISdkLogger? logger) => _logger = logger;

    // ─── Paths ───

    /// <summary>
    /// State directory path. Shared with <see cref="StateManager"/>.
    /// On Windows: <c>%LocalAppData%\SentinelVPN</c>.
    /// On Linux/macOS: <c>~/.sentinel-sdk</c>.
    /// </summary>
    private static readonly string StateDir = GetStateDir();

    /// <summary>Full path to the credentials file.</summary>
    private static readonly string CredentialsFile = Path.Combine(StateDir, "credentials.json");

    // ─── JSON Serialization ───

    /// <summary>Shared JSON options: indented, camelCase, ignore nulls on write.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ─── Public API ───

    /// <summary>
    /// Save handshake credentials for a node+session.
    /// If the store already contains <see cref="MaxEntries"/> entries, the oldest are pruned.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...) used as the key.</param>
    /// <param name="credentials">Credentials to persist.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="nodeAddress"/> or <paramref name="credentials"/> is null.</exception>
    public static void Save(string nodeAddress, SavedCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);
        ArgumentNullException.ThrowIfNull(credentials);

        lock (Lock)
        {
            try
            {
                var store = LoadStore();
                store[nodeAddress] = credentials;
                PruneStore(store);
                SaveStore(store);
            }
            catch (Exception e)
            {
                _logger?.Warn($"CredentialStore.Save warning: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Load saved credentials for a node.
    /// Returns null if no credentials are saved for the given node address.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...) to look up.</param>
    /// <returns>The saved <see cref="SavedCredentials"/>, or null if none exist.</returns>
    public static SavedCredentials? Load(string nodeAddress)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);

        lock (Lock)
        {
            try
            {
                var store = LoadStore();
                return store.TryGetValue(nodeAddress, out var creds) ? creds : null;
            }
            catch (Exception e)
            {
                _logger?.Warn($"CredentialStore.Load warning: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Clear saved credentials for a specific node.
    /// No-op if no credentials exist for the given address.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...) to remove.</param>
    public static void Clear(string nodeAddress)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);

        lock (Lock)
        {
            try
            {
                var store = LoadStore();
                if (store.Remove(nodeAddress))
                {
                    SaveStore(store);
                }
            }
            catch (Exception e)
            {
                _logger?.Warn($"CredentialStore.Clear warning: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Clear ALL saved credentials from the store.
    /// Deletes the credentials file entirely.
    /// </summary>
    public static void ClearAll()
    {
        lock (Lock)
        {
            try
            {
                if (File.Exists(CredentialsFile))
                {
                    File.Delete(CredentialsFile);
                }
            }
            catch (Exception e)
            {
                _logger?.Warn($"CredentialStore.ClearAll warning: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Get the number of credential entries currently stored.
    /// Returns 0 if the file does not exist or cannot be read.
    /// </summary>
    /// <returns>Number of entries in the credential store.</returns>
    public static int Count()
    {
        lock (Lock)
        {
            try
            {
                var store = LoadStore();
                return store.Count;
            }
            catch
            {
                return 0;
            }
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

        Directory.CreateDirectory(StateDir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var dirInfo = new DirectoryInfo(StateDir);
                var security = dirInfo.GetAccessControl();
                security.SetAccessRuleProtection(true, false);
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
    /// Load the credential store from disk.
    /// Returns an empty dictionary if the file doesn't exist or is corrupt.
    /// </summary>
    private static Dictionary<string, SavedCredentials> LoadStore()
    {
        try
        {
            if (!File.Exists(CredentialsFile))
            {
                return new Dictionary<string, SavedCredentials>();
            }

            var json = File.ReadAllText(CredentialsFile);
            var store = JsonSerializer.Deserialize<Dictionary<string, SavedCredentials>>(json, JsonOptions);
            return store ?? new Dictionary<string, SavedCredentials>();
        }
        catch
        {
            return new Dictionary<string, SavedCredentials>();
        }
    }

    /// <summary>
    /// Persist the credential store to disk using atomic write.
    /// Writes to a .tmp file first, then renames to prevent corruption.
    /// Sets owner-only file permissions.
    /// </summary>
    /// <param name="store">Credential entries to persist.</param>
    private static void SaveStore(Dictionary<string, SavedCredentials> store)
    {
        EnsureStateDir();
        var json = JsonSerializer.Serialize(store, JsonOptions);
        AtomicWrite(CredentialsFile, json);
    }

    /// <summary>
    /// Prune the credential store to keep only the most recent <see cref="MaxEntries"/> entries.
    /// Entries are sorted by <see cref="SavedCredentials.SavedAt"/> descending; oldest are removed.
    /// </summary>
    /// <param name="store">Credential dictionary to prune in-place.</param>
    private static void PruneStore(Dictionary<string, SavedCredentials> store)
    {
        if (store.Count <= MaxEntries) return;

        var toRemove = store
            .OrderByDescending(kvp => kvp.Value.SavedAt)
            .Skip(MaxEntries)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            store.Remove(key);
        }
    }

    /// <summary>
    /// Atomically write content to a file by writing to a .tmp file first, then renaming.
    /// This prevents corruption if the process crashes mid-write.
    /// On Windows, sets file ACL to restrict access. On Unix, sets mode 0600.
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
}
