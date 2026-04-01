using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Sentinel.SDK.Core;

// ─── Known Node Record ───

/// <summary>
/// Stored certificate information for a previously-connected node.
/// </summary>
/// <param name="Fingerprint">SHA-256 fingerprint of the node's TLS certificate (hex, uppercase, colon-separated).</param>
/// <param name="FirstSeen">UTC timestamp of the first connection to this node.</param>
/// <param name="LastSeen">UTC timestamp of the most recent connection to this node.</param>
public record KnownNode(string Fingerprint, DateTime FirstSeen, DateTime LastSeen);

// ─── Security Exception ───

/// <summary>
/// Thrown when a TLS certificate mismatch is detected (possible MITM attack).
/// </summary>
public class SecurityException : SentinelException
{
    /// <summary>Initializes a new SecurityException with code, message, and optional details.</summary>
    public SecurityException(string code, string message, object? details = null)
        : base(code, message, details) { }

    /// <summary>Initializes a new SecurityException with code, message, inner exception, and optional details.</summary>
    public SecurityException(string code, string message, Exception innerException, object? details = null)
        : base(code, message, innerException, details) { }
}

// ─── TOFU Trust Store ───

/// <summary>
/// Trust-On-First-Use TLS certificate store.
/// On first connection to a node, stores the cert fingerprint.
/// On subsequent connections, verifies the fingerprint matches.
/// Protects against MITM after initial connection.
///
/// Sentinel nodes use self-signed certificates (no CA issues certs for ephemeral IP servers).
/// Same concept as SSH known_hosts — pin on first use, reject if changed.
/// </summary>
public class TofuTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storePath;
    private readonly object _lock = new();
    private Dictionary<string, KnownNode> _knownNodes = new();

    /// <summary>
    /// Creates a new TOFU trust store, loading any previously-saved fingerprints from disk.
    /// </summary>
    /// <param name="storePath">
    /// Path to the known_nodes.json file.
    /// Defaults to <c>%LocalAppData%\SentinelVPN\known_nodes.json</c> on Windows,
    /// <c>~/.sentinel-sdk/known_nodes.json</c> on Linux/macOS.
    /// </param>
    public TofuTrustStore(string? storePath = null)
    {
        _storePath = storePath ?? GetDefaultStorePath();
        Load();
    }

    /// <summary>
    /// Verify or pin a node's certificate fingerprint.
    /// If the node is not yet known, stores the fingerprint and returns <c>true</c> (trust on first use).
    /// If the node is known and the fingerprint matches, updates <c>LastSeen</c> and returns <c>true</c>.
    /// If the node is known and the fingerprint does NOT match, returns <c>false</c> (possible MITM).
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...) used as the lookup key.</param>
    /// <param name="certFingerprint">SHA-256 fingerprint of the presented certificate.</param>
    /// <returns><c>true</c> if the certificate is trusted; <c>false</c> if a mismatch was detected.</returns>
    public bool VerifyOrPin(string nodeAddress, string certFingerprint)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);
        ArgumentNullException.ThrowIfNull(certFingerprint);

        lock (_lock)
        {
            if (_knownNodes.TryGetValue(nodeAddress, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, certFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Fingerprint matches — update LastSeen
                _knownNodes[nodeAddress] = existing with { LastSeen = DateTime.UtcNow };
                Save();
                return true;
            }

            // First connection — pin the fingerprint
            _knownNodes[nodeAddress] = new KnownNode(
                certFingerprint,
                DateTime.UtcNow,
                DateTime.UtcNow
            );
            Save();
            return true;
        }
    }

    /// <summary>
    /// Clear stored certificate for a specific node.
    /// Call this after a node legitimately rotates its TLS certificate.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...) to clear.</param>
    public void ClearNode(string nodeAddress)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);

        lock (_lock)
        {
            if (_knownNodes.Remove(nodeAddress))
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Clear all stored certificate fingerprints.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _knownNodes.Clear();
            Save();
        }
    }

    /// <summary>
    /// Get stored certificate info for a node, or <c>null</c> if the node is not yet known.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...) to look up.</param>
    /// <returns>The <see cref="KnownNode"/> record, or <c>null</c> if not found.</returns>
    public KnownNode? GetNode(string nodeAddress)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);

        lock (_lock)
        {
            return _knownNodes.GetValueOrDefault(nodeAddress);
        }
    }

    /// <summary>
    /// Get the number of known nodes in the store.
    /// </summary>
    public int Count
    {
        get { lock (_lock) { return _knownNodes.Count; } }
    }

    /// <summary>
    /// Create an <see cref="HttpClientHandler"/> with TOFU certificate validation for a specific node.
    /// The handler's <c>ServerCertificateCustomValidationCallback</c> extracts the SHA-256 fingerprint
    /// from the presented certificate and calls <see cref="VerifyOrPin"/>. If the fingerprint has changed
    /// since the first connection, a <see cref="SecurityException"/> is thrown.
    /// </summary>
    /// <param name="nodeAddress">Node address (sentnode1...) that this handler will connect to.</param>
    /// <returns>An <see cref="HttpClientHandler"/> configured with TOFU validation.</returns>
    public HttpClientHandler CreateHandler(string nodeAddress)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);

        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) =>
            {
                if (cert is null)
                {
                    throw new SecurityException(
                        ErrorCodes.TlsCertChanged,
                        $"No certificate presented by node {nodeAddress} — possible MITM or misconfigured node.",
                        new { nodeAddress }
                    );
                }

                var fingerprint = ComputeSha256Fingerprint(cert);

                if (!VerifyOrPin(nodeAddress, fingerprint))
                {
                    var existing = GetNode(nodeAddress);
                    throw new SecurityException(
                        ErrorCodes.TlsCertChanged,
                        $"TLS certificate CHANGED for {nodeAddress}. " +
                        $"Expected: {existing?.Fingerprint[..Math.Min(20, existing.Fingerprint.Length)]}... " +
                        $"Got: {fingerprint[..Math.Min(20, fingerprint.Length)]}... " +
                        $"This could indicate a man-in-the-middle attack. " +
                        $"If the node legitimately rotated its certificate, call ClearNode(\"{nodeAddress}\").",
                        new
                        {
                            nodeAddress,
                            expected = existing?.Fingerprint,
                            got = fingerprint,
                            firstSeen = existing?.FirstSeen,
                        }
                    );
                }

                // TOFU passed — allow the connection (even if the cert is self-signed)
                return true;
            },
        };
    }

    // ─── Private Helpers ───

    /// <summary>
    /// Compute the SHA-256 fingerprint of an X.509 certificate, formatted as uppercase hex
    /// with colon separators (e.g. "AB:CD:EF:01:...").
    /// </summary>
    internal static string ComputeSha256Fingerprint(X509Certificate2 cert)
    {
        var hashBytes = SHA256.HashData(cert.RawData);
        return BitConverter.ToString(hashBytes).Replace('-', ':');
    }

    /// <summary>
    /// Overload accepting the base <see cref="X509Certificate"/> type.
    /// </summary>
    internal static string ComputeSha256Fingerprint(X509Certificate cert)
    {
        using var cert2 = new X509Certificate2(cert);
        return ComputeSha256Fingerprint(cert2);
    }

    /// <summary>
    /// Returns the default store path based on the operating system.
    /// Windows: %LocalAppData%\SentinelVPN\known_nodes.json
    /// Linux/macOS: ~/.sentinel-sdk/known_nodes.json
    /// </summary>
    private static string GetDefaultStorePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SentinelVPN", "known_nodes.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".sentinel-sdk", "known_nodes.json");
    }

    /// <summary>
    /// Load known nodes from disk. Silently initializes to empty if the file doesn't exist or is corrupt.
    /// </summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                _knownNodes = new Dictionary<string, KnownNode>();
                return;
            }

            var json = File.ReadAllText(_storePath);
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, KnownNode>>(json, JsonOptions);
            _knownNodes = deserialized ?? new Dictionary<string, KnownNode>();
        }
        catch
        {
            // Corrupt or unreadable file — start fresh
            _knownNodes = new Dictionary<string, KnownNode>();
        }
    }

    /// <summary>
    /// Save known nodes to disk atomically (write to temp file, then rename).
    /// Creates the parent directory if it doesn't exist.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_knownNodes, JsonOptions);

            // Atomic write: write to temp file, then move into place
            var tempPath = _storePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Don't crash the application if we can't persist — log and continue
            Console.Error.WriteLine($"[sentinel-sdk] Failed to save known_nodes.json: {ex.Message}");
        }
    }
}
