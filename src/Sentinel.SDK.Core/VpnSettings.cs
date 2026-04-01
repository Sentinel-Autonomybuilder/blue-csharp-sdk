using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.SDK.Core;

// ─── VPN Settings ───────────────────────────────────────────────────────────

/// <summary>
/// Persistent VPN user settings. Saves and loads from a JSON file on disk.
/// <para>
/// Default path:
/// <list type="bullet">
///   <item>Windows: <c>%LocalAppData%\SentinelVPN\settings.json</c></item>
///   <item>Linux/macOS: <c>~/.sentinel-sdk/settings.json</c></item>
/// </list>
/// </para>
/// <para>
/// Loading handles missing files (returns defaults) and corrupt files (returns
/// defaults, logs warning to <see cref="Console.Error"/>). Saving uses an
/// atomic write pattern (write to .tmp, then rename) to prevent corruption.
/// </para>
/// <para>Thread-safe: <see cref="Load"/> and <see cref="Save"/> guard their
/// file I/O with a shared lock.</para>
/// </summary>
public class VpnSettings
{
    /// <summary>Shared lock for file I/O operations.</summary>
    private static readonly object IoLock = new();

    /// <summary>Optional logger for diagnostics.</summary>
    private static ISdkLogger? _logger;

    /// <summary>
    /// Set the logger used by VpnSettings for diagnostic warnings.
    /// </summary>
    /// <param name="logger">Logger instance, or null to suppress output.</param>
    public static void SetLogger(ISdkLogger? logger) => _logger = logger;

    // ─── JSON Serialization ─────────────────────────────────────────────────

    /// <summary>Shared JSON options: indented, camelCase.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ─── Properties ─────────────────────────────────────────────────────────

    /// <summary>
    /// Preferred country code (ISO 3166-1 alpha-2) for node selection.
    /// <c>null</c> means no preference (auto-select).
    /// </summary>
    [JsonPropertyName("preferredCountry")]
    public string? PreferredCountry { get; set; }

    /// <summary>
    /// Whether to automatically connect on application startup.
    /// </summary>
    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; }

    /// <summary>
    /// Whether the kill switch is enabled. When active, all network traffic
    /// is blocked if the VPN connection drops.
    /// </summary>
    [JsonPropertyName("killSwitch")]
    public bool KillSwitch { get; set; }

    /// <summary>
    /// Whether the application should start with Windows (via startup registry key
    /// or login item on macOS).
    /// </summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// The Sentinel node address (sentnode1...) that was last connected to.
    /// Used for quick reconnect.
    /// </summary>
    [JsonPropertyName("lastNodeAddress")]
    public string? LastNodeAddress { get; set; }

    /// <summary>
    /// The service type of the last connection: "wireguard" or "v2ray".
    /// </summary>
    [JsonPropertyName("lastServiceType")]
    public string? LastServiceType { get; set; }

    /// <summary>
    /// Whether to route all traffic through the VPN (full tunnel).
    /// When <c>false</c>, only specific routes are tunneled (split tunnel).
    /// Default: <c>true</c>.
    /// </summary>
    [JsonPropertyName("fullTunnel")]
    public bool FullTunnel { get; set; } = true;

    /// <summary>
    /// Whether to configure the OS system proxy to route through the VPN's
    /// SOCKS5 proxy (V2Ray connections). Default: <c>true</c>.
    /// </summary>
    [JsonPropertyName("systemProxy")]
    public bool SystemProxy { get; set; } = true;

    /// <summary>
    /// DNS preset name for WireGuard tunnel: "handshake" (default), "google", "cloudflare",
    /// or a custom DNS string like "9.9.9.9, 149.112.112.112".
    /// Handshake DNS is censorship-resistant and resolves both Handshake TLDs and ICANN domains.
    /// </summary>
    [JsonPropertyName("dnsPreset")]
    public string? DnsPreset { get; set; }

    // ─── Default Path ───────────────────────────────────────────────────────

    /// <summary>
    /// Default file path for settings persistence.
    /// On Windows: <c>%LocalAppData%\SentinelVPN\settings.json</c>.
    /// On Linux/macOS: <c>~/.sentinel-sdk/settings.json</c>.
    /// </summary>
    public static string DefaultPath { get; } = GetDefaultPath();

    // ─── Load ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Load settings from disk. Returns default settings if the file does not exist
    /// or is corrupt (with a warning logged to <see cref="Console.Error"/>).
    /// </summary>
    /// <param name="path">
    /// Override path. Pass <c>null</c> to use <see cref="DefaultPath"/>.
    /// </param>
    /// <returns>The loaded <see cref="VpnSettings"/> or a fresh default instance.</returns>
    public static VpnSettings Load(string? path = null)
    {
        var filePath = path ?? DefaultPath;

        lock (IoLock)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return new VpnSettings();
                }

                var json = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<VpnSettings>(json, JsonOptions);
                return settings ?? new VpnSettings();
            }
            catch (JsonException ex)
            {
                _logger?.Warn($"Corrupt settings file at {filePath}: {ex.Message} — using defaults");
                return new VpnSettings();
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Failed to load settings from {filePath}: {ex.Message} — using defaults");
                return new VpnSettings();
            }
        }
    }

    // ─── Save ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Save settings to disk using atomic write (write to .tmp, then rename).
    /// Creates the directory if it does not exist.
    /// </summary>
    /// <param name="path">
    /// Override path. Pass <c>null</c> to use <see cref="DefaultPath"/>.
    /// </param>
    /// <exception cref="IOException">
    /// Thrown if the write fails after creating the temp file (e.g. disk full,
    /// permissions). The original settings file is left intact.
    /// </exception>
    public void Save(string? path = null)
    {
        var filePath = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(filePath);

        lock (IoLock)
        {
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);
            AtomicWrite(filePath, json);
        }
    }

    // ─── Private Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Determine the platform-appropriate default path for the settings file.
    /// </summary>
    private static string GetDefaultPath()
    {
        string stateDir;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            stateDir = Path.Combine(localAppData, "SentinelVPN");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            stateDir = Path.Combine(home, ".sentinel-sdk");
        }

        return Path.Combine(stateDir, "settings.json");
    }

    /// <summary>
    /// Atomically write content to a file by writing to a .tmp file first, then renaming.
    /// This prevents corruption if the process crashes mid-write.
    /// </summary>
    /// <param name="filePath">Target file path.</param>
    /// <param name="content">UTF-8 content to write.</param>
    private static void AtomicWrite(string filePath, string content)
    {
        var tmpFile = filePath + ".tmp";
        File.WriteAllText(tmpFile, content);
        File.Move(tmpFile, filePath, overwrite: true);
    }
}
