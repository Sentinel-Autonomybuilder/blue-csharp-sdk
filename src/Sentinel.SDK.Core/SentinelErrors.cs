namespace Sentinel.SDK.Core;

// ─── Exception Hierarchy ───

/// <summary>
/// Base exception for all Sentinel SDK errors. Every exception carries a machine-readable
/// <see cref="Code"/> and optional structured <see cref="Details"/>.
/// </summary>
public class SentinelException : Exception
{
    /// <summary>Machine-readable error code (e.g. "NODE_OFFLINE").</summary>
    public string Code { get; }

    /// <summary>Optional structured details about the error.</summary>
    public object? Details { get; }

    /// <summary>Initializes a new instance with code, message, and optional details.</summary>
    public SentinelException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }

    /// <summary>Initializes a new instance with code, message, inner exception, and optional details.</summary>
    public SentinelException(string code, string message, Exception innerException, object? details = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }
}

/// <summary>
/// Thrown when wallet operations fail (invalid mnemonic, insufficient balance, etc.).
/// </summary>
public class WalletException : SentinelException
{
    public WalletException(string code, string message, object? details = null)
        : base(code, message, details) { }

    public WalletException(string code, string message, Exception innerException, object? details = null)
        : base(code, message, innerException, details) { }
}

/// <summary>
/// Thrown when chain query or broadcast operations fail.
/// </summary>
public class ChainException : SentinelException
{
    public ChainException(string code, string message, object? details = null)
        : base(code, message, details) { }

    public ChainException(string code, string message, Exception innerException, object? details = null)
        : base(code, message, innerException, details) { }
}

/// <summary>
/// Thrown when node communication or status queries fail.
/// </summary>
public class NodeException : SentinelException
{
    public NodeException(string code, string message, object? details = null)
        : base(code, message, details) { }

    public NodeException(string code, string message, Exception innerException, object? details = null)
        : base(code, message, innerException, details) { }
}

/// <summary>
/// Thrown when tunnel setup or management fails (WireGuard, V2Ray).
/// </summary>
public class TunnelException : SentinelException
{
    public TunnelException(string code, string message, object? details = null)
        : base(code, message, details) { }

    public TunnelException(string code, string message, Exception innerException, object? details = null)
        : base(code, message, innerException, details) { }
}

/// <summary>
/// Thrown when a V3 handshake with a Sentinel node fails.
/// </summary>
public class HandshakeException : SentinelException
{
    public HandshakeException(string code, string message, object? details = null)
        : base(code, message, details) { }

    public HandshakeException(string code, string message, Exception innerException, object? details = null)
        : base(code, message, innerException, details) { }
}

/// <summary>
/// Thrown when input validation fails (invalid addresses, out-of-range values, malformed data).
/// </summary>
public class ValidationException : SentinelException
{
    public ValidationException(string code, string message, object? details = null)
        : base(code, message, details) { }

    public ValidationException(string code, string message, Exception innerException, object? details = null)
        : base(code, message, innerException, details) { }
}

// ─── Error Codes ───

/// <summary>
/// Machine-readable error code constants used across the SDK.
/// MUST match JS SDK errors.js ErrorCodes 1:1. String values are the contract.
/// </summary>
public static class ErrorCodes
{
    // ─── Validation ───
    public const string InvalidOptions = "INVALID_OPTIONS";
    public const string InvalidMnemonic = "INVALID_MNEMONIC";
    public const string InvalidNodeAddress = "INVALID_NODE_ADDRESS";
    public const string InvalidGigabytes = "INVALID_GIGABYTES";
    public const string InvalidUrl = "INVALID_URL";
    public const string InvalidPlanId = "INVALID_PLAN_ID";

    // ─── Node ───
    public const string NodeOffline = "NODE_OFFLINE";
    public const string NodeNoUdvpn = "NODE_NO_UDVPN";
    public const string NodeNotFound = "NODE_NOT_FOUND";
    public const string NodeClockDrift = "NODE_CLOCK_DRIFT";
    public const string NodeInactive = "NODE_INACTIVE";
    public const string NodeDatabaseCorrupt = "NODE_DATABASE_CORRUPT";
    public const string InvalidAssignedIp = "INVALID_ASSIGNED_IP";

    // ─── Chain ───
    public const string InsufficientBalance = "INSUFFICIENT_BALANCE";
    public const string BroadcastFailed = "BROADCAST_FAILED";
    public const string TxFailed = "TX_FAILED";
    public const string LcdError = "LCD_ERROR";
    public const string UnknownMsgType = "UNKNOWN_MSG_TYPE";
    public const string AllEndpointsFailed = "ALL_ENDPOINTS_FAILED";
    public const string SequenceMismatch = "SEQUENCE_MISMATCH";
    public const string ChainLag = "CHAIN_LAG";

    // ─── Session ───
    public const string SessionExists = "SESSION_EXISTS";
    public const string SessionExtractFailed = "SESSION_EXTRACT_FAILED";
    public const string SessionPoisoned = "SESSION_POISONED";

    // ─── Tunnel ───
    public const string V2RayNotFound = "V2RAY_NOT_FOUND";
    public const string V2RayAllFailed = "V2RAY_ALL_FAILED";
    public const string WireGuardNotAvailable = "WG_NOT_AVAILABLE";
    public const string WgNoConnectivity = "WG_NO_CONNECTIVITY";
    public const string TunnelSetupFailed = "TUNNEL_SETUP_FAILED";

    // ─── Security ───
    public const string TlsCertChanged = "TLS_CERT_CHANGED";

    // ─── Connection ───
    public const string Aborted = "ABORTED";
    public const string AllNodesFailed = "ALL_NODES_FAILED";
    public const string AlreadyConnected = "ALREADY_CONNECTED";
    public const string PartialConnectionFailed = "PARTIAL_CONNECTION_FAILED";

    // ─── Subscription / Plan ───
    public const string SubscribeFailed = "SUBSCRIBE_FAILED";
    public const string SubscriptionNotFound = "SUBSCRIPTION_NOT_FOUND";
    public const string ShareFailed = "SHARE_FAILED";

    // ─── C#-specific (extras not in JS — keep for backwards compat) ───
    public const string NotConnected = "NOT_CONNECTED";
    public const string HandshakeFailed = "HANDSHAKE_FAILED";
    public const string ConnectionInProgress = "CONNECTION_IN_PROGRESS";
    public const string NodeMisconfigured = "NODE_MISCONFIGURED";
    public const string NodeDbCorrupt = "NODE_DB_CORRUPT";
    public const string NodeRpcBroken = "NODE_RPC_BROKEN";

    // ─── Deprecated aliases (match old C# names → correct JS string values) ───
    [Obsolete("Use SessionExists instead")] public const string SessionAlreadyExists = "SESSION_EXISTS";
    [Obsolete("Use NodeClockDrift instead")] public const string ClockDriftTooHigh = "NODE_CLOCK_DRIFT";
}

// ─── Error Severity ───

/// <summary>
/// Maps error codes to severity levels and user-facing messages.
/// MUST match JS SDK errors.js ERROR_SEVERITY and userMessage() 1:1.
/// </summary>
public static class ErrorSeverity
{
    /// <summary>
    /// Get the severity level for a given error code.
    /// Returns "fatal", "retryable", "recoverable", or "infrastructure".
    /// Every code MUST be classified — "unknown" means a gap.
    /// </summary>
    public static string Get(string code) => code switch
    {
        // Fatal — don't retry, user action needed
        ErrorCodes.InvalidMnemonic => "fatal",
        ErrorCodes.InsufficientBalance => "fatal",
        ErrorCodes.InvalidNodeAddress => "fatal",
        ErrorCodes.InvalidOptions => "fatal",
        ErrorCodes.InvalidGigabytes => "fatal",
        ErrorCodes.InvalidUrl => "fatal",
        ErrorCodes.InvalidPlanId => "fatal",
        ErrorCodes.UnknownMsgType => "fatal",
        ErrorCodes.SessionPoisoned => "fatal",
        ErrorCodes.WireGuardNotAvailable => "fatal",
        ErrorCodes.AlreadyConnected => "fatal",
        ErrorCodes.NotConnected => "fatal",
        ErrorCodes.ConnectionInProgress => "fatal",

        // Retryable — try again, possibly different node
        ErrorCodes.NodeOffline => "retryable",
        ErrorCodes.NodeNoUdvpn => "retryable",
        ErrorCodes.NodeNotFound => "retryable",
        ErrorCodes.NodeClockDrift => "retryable",
        ErrorCodes.NodeInactive => "retryable",
        ErrorCodes.NodeDatabaseCorrupt => "retryable",
        ErrorCodes.NodeMisconfigured => "retryable",
        ErrorCodes.NodeRpcBroken => "retryable",
        ErrorCodes.V2RayAllFailed => "retryable",
        ErrorCodes.WgNoConnectivity => "retryable",
        ErrorCodes.TunnelSetupFailed => "retryable",
        ErrorCodes.BroadcastFailed => "retryable",
        ErrorCodes.TxFailed => "retryable",
        ErrorCodes.LcdError => "retryable",
        ErrorCodes.AllEndpointsFailed => "retryable",
        ErrorCodes.AllNodesFailed => "retryable",
        ErrorCodes.SequenceMismatch => "retryable",
        ErrorCodes.ChainLag => "retryable",
        ErrorCodes.NodeDbCorrupt => "retryable",

        // Recoverable — can resume with recovery methods
        ErrorCodes.SessionExtractFailed => "recoverable",
        ErrorCodes.PartialConnectionFailed => "recoverable",
        ErrorCodes.SessionExists => "recoverable",
        ErrorCodes.HandshakeFailed => "recoverable",
        ErrorCodes.SubscribeFailed => "retryable",
        ErrorCodes.SubscriptionNotFound => "retryable",
        ErrorCodes.ShareFailed => "retryable",

        // Infrastructure — check system state
        ErrorCodes.TlsCertChanged => "infrastructure",
        ErrorCodes.V2RayNotFound => "infrastructure",
        ErrorCodes.InvalidAssignedIp => "retryable",
        ErrorCodes.Aborted => "fatal",

        _ => "unknown",
    };

    /// <summary>
    /// Returns true if the error is retryable (transient failure).
    /// </summary>
    public static bool IsRetryable(string code) => Get(code) == "retryable";

    /// <summary>
    /// Get a user-friendly message for a given error code.
    /// MUST match JS SDK userMessage() 1:1 for every code.
    /// </summary>
    public static string UserMessage(string code) => code switch
    {
        ErrorCodes.InsufficientBalance => "Not enough P2P tokens. Fund your wallet to continue.",
        ErrorCodes.NodeOffline => "This node is offline. Try a different server.",
        ErrorCodes.NodeNoUdvpn => "This node does not accept P2P tokens.",
        ErrorCodes.NodeClockDrift => "Node clock is out of sync. Try a different server.",
        ErrorCodes.NodeInactive => "Node went inactive. Try a different server.",
        ErrorCodes.NodeDatabaseCorrupt => "Node has a corrupted database. Try a different server.",
        ErrorCodes.NodeNotFound => "Node not found on chain. It may be inactive.",
        ErrorCodes.NodeMisconfigured => "Node is misconfigured. Try a different server.",
        ErrorCodes.NodeDbCorrupt => "Node database is corrupt. Try a different server.",
        ErrorCodes.NodeRpcBroken => "Node backend is temporarily unavailable. Try again later.",
        ErrorCodes.InvalidAssignedIp => "Node returned an invalid IP address during handshake. Try a different server.",
        ErrorCodes.V2RayAllFailed => "Could not establish tunnel. Node may be overloaded.",
        ErrorCodes.V2RayNotFound => "V2Ray binary not found. Check your installation.",
        ErrorCodes.WireGuardNotAvailable => "WireGuard is not available. Install it or use V2Ray nodes.",
        ErrorCodes.WgNoConnectivity => "VPN tunnel has no internet connectivity.",
        ErrorCodes.TunnelSetupFailed => "Tunnel setup failed. Try again or pick another server.",
        ErrorCodes.TlsCertChanged => "Node certificate changed unexpectedly. This could indicate a security issue.",
        ErrorCodes.BroadcastFailed => "Transaction failed. Check your balance and try again.",
        ErrorCodes.TxFailed => "Chain transaction rejected. Check balance and gas.",
        ErrorCodes.LcdError => "Chain query failed. Try again later.",
        ErrorCodes.AllEndpointsFailed => "All chain endpoints are unreachable. Try again later.",
        ErrorCodes.SequenceMismatch => "Transaction sequence error. Retry automatically.",
        ErrorCodes.ChainLag => "Session not yet confirmed on node. Wait a moment and try again.",
        ErrorCodes.UnknownMsgType => "Unknown message type. Check SDK version compatibility.",
        ErrorCodes.AlreadyConnected => "Already connected. Disconnect first.",
        ErrorCodes.NotConnected => "Not connected to any node.",
        ErrorCodes.ConnectionInProgress => "A connection attempt is already in progress.",
        ErrorCodes.AllNodesFailed => "All servers failed. Check your network connection.",
        ErrorCodes.HandshakeFailed => "Connection handshake failed. Try again.",
        ErrorCodes.SessionExists => "An active session already exists. Use recovery to resume.",
        ErrorCodes.SessionExtractFailed => "Session creation succeeded but ID extraction failed. Use recovery.",
        ErrorCodes.SessionPoisoned => "Session is poisoned (previously failed). Start a new session.",
        ErrorCodes.PartialConnectionFailed => "Payment succeeded but connection failed. Use recovery to retry.",
        ErrorCodes.Aborted => "Connection was cancelled.",
        ErrorCodes.SubscribeFailed => "Failed to subscribe to the plan. Check your balance and try again.",
        ErrorCodes.SubscriptionNotFound => "Subscription not found after payment. Check chain state.",
        ErrorCodes.ShareFailed => "Failed to share subscription bandwidth. Try again.",
        ErrorCodes.InvalidMnemonic => "Invalid wallet phrase. Must be 12 or 24 words.",
        ErrorCodes.InvalidNodeAddress => "Invalid node address.",
        ErrorCodes.InvalidOptions => "Invalid connection options provided.",
        ErrorCodes.InvalidGigabytes => "Invalid bandwidth amount. Must be a positive number.",
        ErrorCodes.InvalidUrl => "Invalid URL format.",
        ErrorCodes.InvalidPlanId => "Invalid plan ID.",
        _ => "An unexpected error occurred.",
    };
}
