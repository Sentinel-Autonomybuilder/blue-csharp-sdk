# Sentinel dVPN C# SDK -- API Reference

Categorized public API catalog. The SDK is split across three NuGet packages:

```csharp
using Sentinel.SDK.Core;    // Wallet, chain, TX, messages, helpers, types, errors
using Sentinel.SDK.Node;    // Handshake, node status, session manager, VPN client
using Sentinel.SDK.Tunnel.WireGuard;  // WireGuard tunnel management
using Sentinel.SDK.Tunnel.V2Ray;      // V2Ray process management
```

---

## 1. VPN Client (SentinelVpnClient)

High-level connection orchestrator. Manages the full flow: wallet setup, chain queries, session creation, V3 handshake, and tunnel installation (WireGuard or V2Ray). Implements `IDisposable`.

### Constructor

```csharp
SentinelVpnClient(SentinelWallet wallet, SentinelVpnOptions? options = null)
```

Create a new VPN client with the given wallet and optional configuration.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsConnected` | `bool` | Whether the client has an active VPN connection. |

### Methods

```csharp
Task<ConnectionResult> ConnectAsync(string nodeAddress, CancellationToken ct = default)
```
Connect to a specific node by address (direct pay-per-GB). Checks balance, queries node status, creates on-chain session, performs V3 handshake, and installs tunnel.

```csharp
Task<ConnectionResult> ConnectAutoAsync(ConnectAutoOptions? options = null, CancellationToken ct = default)
```
Auto-pick the best available node and connect. Queries online nodes, filters by country/service type, sorts by fewest peers, and attempts connection to up to `MaxAttempts` nodes.

```csharp
Task<ConnectionResult> ConnectViaSubscriptionAsync(ulong subscriptionId, string nodeAddress, CancellationToken ct = default)
```
Connect to a node using an existing on-chain subscription. Allocates a session on the subscription instead of creating a new pay-per-GB session.

```csharp
Task DisconnectAsync()
```
Disconnect from the current node and clean up the tunnel. Stops V2Ray process or uninstalls WireGuard tunnel service.

```csharp
ConnectionStatus? GetStatus()
```
Get the current connection status with uptime, or null if disconnected.

```csharp
void Dispose()
```
Dispose the VPN client, disconnecting if still connected and releasing all resources.

### Events

| Event | Args Type | Description |
|-------|-----------|-------------|
| `Progress` | `ProgressEventArgs` | Raised during each step of the connection flow. |
| `Connected` | `ConnectionEventArgs` | Raised when a VPN connection is successfully established. |
| `Disconnected` | `DisconnectedEventArgs` | Raised when the VPN is disconnected. |
| `Error` | `ErrorEventArgs` | Raised when an error occurs during connection or tunnel operation. |

### Configuration Records

```csharp
record SentinelVpnOptions
{
    string[]? LcdUrls;         // LCD endpoint URLs (default: Constants.DefaultLcdUrls)
    string[]? RpcUrls;         // RPC endpoint URLs (default: Constants.DefaultRpcUrls)
    bool FullTunnel = true;    // Route all traffic through tunnel
    bool SystemProxy = true;   // Configure system SOCKS5 proxy for V2Ray
    string? V2RayExePath;      // Path to v2ray.exe (required for V2Ray nodes)
    int Gigabytes = 1;         // GB to subscribe for new sessions
    bool ForceNewSession;      // Always create new session (skip reuse check)
}
```

```csharp
record ConnectAutoOptions
{
    string[]? Countries;       // ISO 3166-1 alpha-2 country filter (e.g. ["DE", "US"])
    string? ServiceType;       // "wireguard" or "v2ray" filter
    int MaxAttempts = 3;       // Max nodes to try before giving up
    string[]? NodePool;        // Restrict to specific node addresses
}
```

### Result Records

```csharp
record ConnectionResult
{
    string SessionId;          // On-chain session ID
    string NodeAddress;        // Connected node address (sentnode1...)
    string ServiceType;        // "wireguard" or "v2ray"
    int? SocksPort;            // Local SOCKS5 port (V2Ray only)
    string? SocksUser;         // SOCKS5 username (V2Ray only)
    string? SocksPass;         // SOCKS5 password (V2Ray only)
    string? VpnIp;             // Assigned VPN IP (WireGuard only)
}
```

```csharp
record ConnectionStatus
{
    bool Connected;
    string? NodeAddress;
    string? SessionId;
    string? ServiceType;
    TimeSpan Uptime;
}
```

### Event Args

```csharp
class ProgressEventArgs : EventArgs
{
    string Step;    // "wallet", "balance", "subscribe", "handshake", "tunnel", "auto", "discovery"
    string Detail;  // Human-readable message
}

class ConnectionEventArgs : EventArgs { ConnectionResult Result; }
class DisconnectedEventArgs : EventArgs { string Reason; }  // "user", "error", "dispose"
class ErrorEventArgs : EventArgs { Exception Exception; }
```

---

## 2. Wallet (SentinelWallet)

BIP39 mnemonic generation, BIP44 key derivation (m/44'/118'/0'/0/0), secp256k1 signing, and Bech32 address encoding. Implements `ISentinelWallet` and `IDisposable`.

### Factory Methods

```csharp
static SentinelWallet Generate(int strength = 128)
```
Generate a new random wallet with a BIP39 mnemonic. `strength`: 128 (12 words), 160 (15), 192 (18), 224 (21), 256 (24 words).

```csharp
static SentinelWallet FromMnemonic(string mnemonic)
```
Derive a wallet from an existing BIP39 mnemonic phrase.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Mnemonic` | `string` | BIP39 mnemonic phrase used to derive this wallet. |
| `Address` | `string` | Bech32-encoded account address with "sent" prefix. |

### Methods

```csharp
byte[] Sign(byte[] message)
```
Sign a 32-byte SHA256 hash with secp256k1. Returns 64-byte compact signature (r || s).

```csharp
byte[] GetPublicKeyCompressed()
```
Get the 33-byte compressed secp256k1 public key.

```csharp
string ToSentnode()
```
Convert this wallet's address to a `sentnode1...` node operator address.

```csharp
string ToSentprov()
```
Convert this wallet's address to a `sentprov1...` provider address.

```csharp
static bool IsSameKey(string addr1, string addr2)
```
Compare two Sentinel addresses across different prefixes (sent, sentnode, sentprov). Returns true if they encode the same underlying key bytes.

```csharp
void Dispose()
```
Mark the wallet as disposed. Prevents further signing operations.

### Interface: ISentinelWallet

```csharp
interface ISentinelWallet
{
    string Address { get; }
    byte[] Sign(byte[] hash);
    byte[] GetPublicKeyCompressed();
}
```

Minimal interface used by `Handshake` and `SessionManager`. Implement this to provide a custom wallet (e.g., hardware wallet, KMS).

---

## 3. Chain Client (ChainClient / IChainClient)

HTTP client for Sentinel chain LCD (REST) and RPC queries. Handles endpoint failover, pagination, and retry logic. Implements `IChainClient` and `IDisposable`.

### Constructor

```csharp
ChainClient(string[]? lcdUrls = null, string[]? rpcUrls = null)
```
Create a new chain client. LCD/RPC URLs fall back to `Constants.DefaultLcdUrls` / `Constants.DefaultRpcUrls`.

### Query Methods

```csharp
Task<Balance> GetBalanceAsync(string address, CancellationToken ct = default)
```
Get the udvpn balance for an address.

```csharp
Task<List<ChainNode>> GetActiveNodesAsync(int limit = 500, CancellationToken ct = default)
```
Get active nodes registered on the chain.

```csharp
Task<ChainNode?> GetNodeAsync(string nodeAddress, CancellationToken ct = default)
```
Get a single node by its `sentnode1...` address. Returns null if not found.

```csharp
Task<List<Subscription>> GetSubscriptionsAsync(string address, CancellationToken ct = default)
```
Get subscriptions for an account address.

```csharp
Task<List<ChainSession>> GetSessionsAsync(string address, string status = "1", CancellationToken ct = default)
```
Get sessions for an account address. Status "1" = active.

```csharp
Task<List<ChainNode>> GetPlanNodesAsync(int planId, CancellationToken ct = default)
```
Get nodes assigned to a plan. Uses `limit=5000` because Sentinel pagination is broken for plan nodes.

```csharp
Task<List<DiscoveredPlan>> DiscoverPlansAsync(int maxId = 100, CancellationToken ct = default)
```
Discover subscription plans by probing IDs from 1 to maxId.

```csharp
Task<List<FeeGrant>> QueryFeeGrantsAsync(string grantee, CancellationToken ct = default)
```
Query fee grants where the given address is the grantee.

```csharp
Task<IReadOnlyList<ActiveSession>> QueryActiveSessionsForAddressAsync(string walletAddress, CancellationToken ct = default)
```
Query all active sessions for a wallet address.

```csharp
Task<RawSessionAllocation?> QuerySessionAllocationAsync(ulong sessionId, CancellationToken ct = default)
```
Query bandwidth allocation for a specific session.

```csharp
Task<List<ChainNode>> QueryPlanNodesAsync(int planId, CancellationToken ct = default)
```
Query nodes assigned to a plan (uses single large request, pagination broken).

```csharp
Task<bool> HasActiveSubscriptionAsync(string address, int planId, CancellationToken ct = default)
```
Check whether an address has an active subscription for a given plan.

```csharp
Task<IReadOnlyList<ChainNode>> GetAvailableNodesAsync(string walletAddress, CancellationToken ct = default)
```
Get all nodes available through the wallet's active subscriptions. Queries subscriptions, extracts plan IDs, fetches plan nodes, deduplicates.

---

## 4. Transaction Builder (TransactionBuilder)

Builds, signs, and broadcasts Cosmos SDK transactions using SIGN_MODE_DIRECT with secp256k1 signing. Handles sequence mismatch recovery with automatic retry and double-spend detection.

### Constructor

```csharp
TransactionBuilder(SentinelWallet wallet, ChainClient client)
```

### Methods

```csharp
Task<TxResult> BroadcastAsync(params SentinelMessage[] messages)
```
Broadcast pre-encoded `SentinelMessage` objects (from `MessageBuilder`). Retries up to 3 times on sequence mismatch (code 32). Checks for double-spend before retrying.

```csharp
Task<TxResult> BroadcastProtobufAsync(params IMessage[] messages)
```
Broadcast protobuf `IMessage` objects. Wraps each as `google.protobuf.Any` with auto-detected type URLs. Includes gas estimation per message type and sequence retry.

---

## 5. Message Builder (MessageBuilder)

Static builders for all 17 Sentinel chain message types. Each returns a `SentinelMessage` record with a type URL and protobuf wire-format encoded bytes, ready for `TransactionBuilder.BroadcastAsync()`.

### Common Record

```csharp
record SentinelMessage(string TypeUrl, byte[] Value)
```

### Node Session Messages

```csharp
static SentinelMessage StartSession(string from, string nodeAddress, long gigabytes = 1, PriceEntry? maxPrice = null)
```
Start a direct pay-per-GB session on a node. Type: `/sentinel.node.v3.MsgStartSessionRequest`.

```csharp
static SentinelMessage EndSession(string from, ulong sessionId)
```
End an active session. Type: `/sentinel.session.v3.MsgEndSessionRequest`.

### Subscription Messages

```csharp
static SentinelMessage StartSubscription(string from, ulong planId, string denom = "udvpn")
```
Subscribe to a plan (without starting a session). Type: `/sentinel.subscription.v3.MsgStartSubscriptionRequest`.

```csharp
static SentinelMessage SubStartSession(string from, ulong subscriptionId, string nodeAddress)
```
Start a session on an existing subscription. Type: `/sentinel.subscription.v3.MsgStartSessionRequest`.

### Plan Messages

```csharp
static SentinelMessage PlanStartSession(string from, ulong planId, string denom = "udvpn", string? nodeAddress = null)
```
Subscribe to plan AND start session in one TX. Type: `/sentinel.plan.v3.MsgStartSessionRequest`.

```csharp
static SentinelMessage CreatePlan(string from, string bytes, long durationSeconds, PriceEntry[] prices, bool isPrivate = false)
```
Create a new subscription plan (starts INACTIVE). Type: `/sentinel.plan.v3.MsgCreatePlanRequest`.

```csharp
static SentinelMessage UpdatePlanStatus(string from, ulong planId, int status)
```
Activate or deactivate a plan. Status: 1=active, 2=inactive_pending, 3=inactive. Type: `/sentinel.plan.v3.MsgUpdatePlanStatusRequest`.

```csharp
static SentinelMessage LinkNode(string from, ulong planId, string nodeAddress)
```
Link a node to a plan. Type: `/sentinel.plan.v3.MsgLinkNodeRequest`.

```csharp
static SentinelMessage UnlinkNode(string from, ulong planId, string nodeAddress)
```
Unlink a node from a plan. Type: `/sentinel.plan.v3.MsgUnlinkNodeRequest`.

### Provider Messages

```csharp
static SentinelMessage RegisterProvider(string from, string name, string? identity = null, string? website = null, string? description = null)
```
Register as a dVPN provider. Type: `/sentinel.provider.v3.MsgRegisterProviderRequest`.

```csharp
static SentinelMessage UpdateProviderDetails(string from, string? name = null, string? identity = null, string? website = null, string? description = null)
```
Update provider details. `from` uses `sentprov` prefix. Type: `/sentinel.provider.v3.MsgUpdateProviderDetailsRequest`.

```csharp
static SentinelMessage UpdateProviderStatus(string from, int status)
```
Activate or deactivate provider. Status: 1=active, 2=inactive_pending, 3=inactive. Type: `/sentinel.provider.v3.MsgUpdateProviderStatusRequest`.

### Lease Messages

```csharp
static SentinelMessage StartLease(string from, string nodeAddress, long hours, PriceEntry? maxPrice = null)
```
Lease a node from its operator. Type: `/sentinel.lease.v1.MsgStartLeaseRequest`.

```csharp
static SentinelMessage EndLease(string from, ulong leaseId)
```
End an active lease. Type: `/sentinel.lease.v1.MsgEndLeaseRequest`.

### Cosmos Bank Messages

```csharp
static SentinelMessage Send(string from, string to, long amountUdvpn)
```
Send P2P tokens to an address. Type: `/cosmos.bank.v1beta1.MsgSend`.

### Fee Grant Messages

```csharp
static SentinelMessage GrantFeeAllowance(string granter, string grantee, long? spendLimitUdvpn = null, DateTime? expiration = null)
```
Grant fee allowance (granter pays gas for grantee). Type: `/cosmos.feegrant.v1beta1.MsgGrantAllowance`.

```csharp
static SentinelMessage RevokeFeeAllowance(string granter, string grantee)
```
Revoke a fee grant. Type: `/cosmos.feegrant.v1beta1.MsgRevokeAllowance`.

---

## 6. Handshake

Static class that performs V3 handshakes with Sentinel dVPN nodes to establish tunnel sessions.

```csharp
static Task<object> HandshakeAsync(ISentinelWallet wallet, string nodeUrl, ulong sessionId, HandshakeType type, CancellationToken ct = default)
```
Performs a V3 handshake. Returns `WireGuardHandshakeResult` when type is `WireGuard`, or `V2RayHandshakeResult` when type is `V2Ray`.

### HandshakeType Enum

| Value | Description |
|-------|-------------|
| `WireGuard` | WireGuard tunnel -- generates X25519 keypair. |
| `V2Ray` | V2Ray tunnel -- generates UUID identifier. |

### Result Records

```csharp
record WireGuardHandshakeResult(
    string ServerPublicKey,       // Base64-encoded X25519 public key of the node
    string[] AssignedAddresses,   // Client addresses (e.g. ["10.8.0.2/24", "fd1d::2/128"])
    string ServerEndpoint,        // Node WireGuard endpoint ("ip:port")
    byte[] ClientPrivateKey       // Raw X25519 private key bytes
)
```

```csharp
record V2RayHandshakeResult(
    string Uuid,                  // UUID for VLess/VMess authentication
    int ProxyProtocol,            // 1=VLess, 2=VMess
    int Transport,                // 1=ds, 2=gun, 3=grpc, 4=http, 5=mkcp, 6=quic, 7=tcp, 8=ws
    int Tls,                      // 0=none, 1=tls
    int Port                      // Listening port on the node
)
```

---

## 7. Node Client (NodeClient)

Static class for querying Sentinel dVPN node status and metadata.

```csharp
static Task<NodeStatus> GetStatusAsync(string nodeUrl, CancellationToken ct = default)
```
Query a node for its current status. Returns type, moniker, peers, location, bandwidth, and estimated clock drift.

### Result Records

```csharp
record NodeStatus(
    string Type,              // "wireguard" or "v2ray"
    string Moniker,           // Human-readable node name
    int Peers,                // Current connected peers
    Location Location,        // Geographic location
    Bandwidth Bandwidth,      // Upload/download speeds
    double? ClockDriftSec     // Estimated clock drift in seconds
)

record Location(string City, string Country, double Latitude, double Longitude)
record Bandwidth(long Upload, long Download)
```

---

## 8. Session Manager (SessionManager)

Static class for session lifecycle management: discovery and allocation queries.

```csharp
static Task<ulong?> FindExistingSessionAsync(IChainClient client, string walletAddress, string nodeAddress, CancellationToken ct = default)
```
Search for an existing active session between a wallet and a node. Returns the session ID, or null if no active session exists.

```csharp
static Task<SessionAllocation?> GetSessionAllocationAsync(IChainClient client, ulong sessionId, CancellationToken ct = default)
```
Query bandwidth allocation for a session. Returns allocation details or null.

### Result Record

```csharp
record SessionAllocation(
    long MaxBytes,           // Total bytes allocated
    long UsedBytes,          // Bytes consumed so far
    long RemainingBytes,     // MaxBytes - UsedBytes
    int PercentUsed          // Usage percentage (0-100)
)
```

---

## 9. WireGuard Tunnel (WireGuardTunnel)

Manages a WireGuard tunnel on Windows via `wireguard.exe` service commands. Requires administrator privileges. Implements `IDisposable`.

### Constructor

```csharp
WireGuardTunnel(string tunnelName = "wgsent0")
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `TunnelName` | `string` | Tunnel interface name (default: "wgsent0"). |
| `IsActive` | `bool` | Whether the tunnel service is currently running. |

### Methods

```csharp
Task InstallAsync(WireGuardConfig config, CancellationToken ct = default)
```
Write .conf file, set permissions, install tunnel service, and wait for activation.

```csharp
Task UninstallAsync(CancellationToken ct = default)
```
Remove the tunnel service and clean up the configuration file.

### Configuration Record

```csharp
record WireGuardConfig(
    byte[] ClientPrivateKey,       // X25519 private key (32 bytes)
    string[] AssignedAddresses,    // Client addresses (e.g. ["10.8.0.2/24"])
    string ServerPublicKey,        // Base64-encoded server public key
    string ServerEndpoint,         // "ip:port"
    bool FullTunnel = true,        // AllowedIPs = 0.0.0.0/0, ::/0
    string[]? SplitIPs = null      // Specific IPs for split-tunnel mode
)
```

### Hard-coded Values

| Setting | Value | Reason |
|---------|-------|--------|
| MTU | 1280 | Required by Sentinel nodes (not default 1420). |
| PersistentKeepalive | 15 | Required for NAT traversal (not default 25). |
| DNS | 10.8.0.1 | Node-provided DNS resolver. |
| Config dir | `C:\ProgramData\sentinel-wg` | Restricted permissions (SYSTEM + Administrators). |

---

## 10. V2Ray Process (V2RayProcess)

Manages a V2Ray process lifecycle on Windows. Starts V2Ray with a generated config, provides a local SOCKS5 proxy, and handles clean shutdown. Implements `IDisposable`.

### Constructor

```csharp
V2RayProcess(string v2rayExePath)
```
Create a new V2Ray process manager. Throws if the exe path does not exist.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsRunning` | `bool` | Whether the V2Ray process is currently running. |
| `SocksPort` | `int` | Local SOCKS5 proxy port (set after StartAsync). |
| `SocksUser` | `string?` | SOCKS5 proxy username for authentication. |
| `SocksPass` | `string?` | SOCKS5 proxy password for authentication. |

### Methods

```csharp
Task StartAsync(V2RayConfig config, CancellationToken ct = default)
```
Write temp config, launch v2ray.exe, and wait for SOCKS5 port to accept connections (10s timeout).

```csharp
Task StopAsync(CancellationToken ct = default)
```
Kill the V2Ray process tree and delete the temp config file.

```csharp
string GetStderr()
```
Get captured stderr output from the V2Ray process.

---

## 11. V2Ray Config Builder (V2RayConfigBuilder)

Static class that builds V2Ray JSON configuration matching the Sentinel JS SDK format.

```csharp
static string BuildConfig(V2RayConfig config)
```
Build a complete V2Ray JSON configuration string.

```csharp
static V2RayConfigResult BuildConfigWithAuth(V2RayConfig config)
```
Build a V2Ray JSON configuration with SOCKS5 authentication credentials.

### Configuration Record

```csharp
record V2RayConfig(
    string ServerHost,            // Node IP or hostname
    int Port,                     // Port from handshake
    string Protocol,              // "vless" or "vmess"
    string Transport,             // "tcp", "ws", "grpc", "gun", "http", "kcp", "quic", "ds"
    bool Tls,                     // Whether TLS is enabled
    string Uuid,                  // V2Ray UUID string
    int LocalSocksPort = 10808    // Local SOCKS5 listen port
)

record V2RayConfigResult(
    string ConfigJson,            // Complete V2Ray JSON config
    string SocksUser,             // SOCKS5 proxy username
    string SocksPass              // SOCKS5 proxy password
)
```

### V2Ray Config Non-Negotiables

- **VLess:** `encryption = "none"`, NO `flow` field.
- **VMess:** `alterId = 0`, NO `security` in user object.
- **UUID:** Field name must be `"uuid"`.
- **NO** per-outbound transport settings.
- **NO** `serverName` in `tlsSettings`.
- **`allowInsecure = true`** in `tlsSettings`.
- **gun (2) and grpc (3) are DIFFERENT protocols.** gun = raw H2, grpc = gRPC lib.

---

## 12. Error Handling

### Exception Hierarchy

```
Exception
  SentinelException           -- Base: all SDK errors carry Code + Details
    WalletException           -- Wallet operations (invalid mnemonic, etc.)
    ChainException            -- Chain/TX failures (broadcast, LCD error)
      SentinelSessionException  -- Session management failures
    NodeException             -- Node communication failures
      SentinelNodeException     -- Node status query failures
    TunnelException           -- Tunnel setup failures (WG, V2Ray)
    HandshakeException        -- V3 handshake failures
      SentinelHandshakeException  -- Specific handshake failures
```

### SentinelException Properties

| Property | Type | Description |
|----------|------|-------------|
| `Code` | `string` | Machine-readable error code (e.g. "NODE_OFFLINE"). |
| `Details` | `object?` | Optional structured details about the error. |
| `Message` | `string` | Human-readable error description (inherited from Exception). |

### Error Codes (ErrorCodes)

| Constant | Value | Category |
|----------|-------|----------|
| `InvalidMnemonic` | `INVALID_MNEMONIC` | Wallet |
| `InsufficientBalance` | `INSUFFICIENT_BALANCE` | Wallet |
| `NodeOffline` | `NODE_OFFLINE` | Node |
| `NodeNotFound` | `NODE_NOT_FOUND` | Node |
| `BroadcastFailed` | `BROADCAST_FAILED` | Chain |
| `TxFailed` | `TX_FAILED` | Chain |
| `SequenceMismatch` | `SEQUENCE_MISMATCH` | Chain |
| `WireGuardNotAvailable` | `WG_NOT_AVAILABLE` | Tunnel |
| `V2RayNotFound` | `V2RAY_NOT_FOUND` | Tunnel |
| `TunnelSetupFailed` | `TUNNEL_SETUP_FAILED` | Tunnel |
| `AllNodesFailed` | `ALL_NODES_FAILED` | Connection |
| `AlreadyConnected` | `ALREADY_CONNECTED` | Connection |
| `NotConnected` | `NOT_CONNECTED` | Connection |
| `HandshakeFailed` | `HANDSHAKE_FAILED` | Connection |
| `SessionAlreadyExists` | `SESSION_ALREADY_EXISTS` | Connection |
| `ClockDriftTooHigh` | `CLOCK_DRIFT_TOO_HIGH` | Connection |
| `ConnectionInProgress` | `CONNECTION_IN_PROGRESS` | Connection |

### Error Severity (ErrorSeverity)

```csharp
static string Get(string code)
```
Returns `"fatal"`, `"retryable"`, `"recoverable"`, or `"unknown"`.

```csharp
static bool IsRetryable(string code)
```
Returns true if the error is retryable (transient failure).

```csharp
static string UserMessage(string code)
```
Returns a user-friendly message for a given error code.

**Severity mapping:**

| Severity | Codes | Action |
|----------|-------|--------|
| `fatal` | `INVALID_MNEMONIC`, `INSUFFICIENT_BALANCE` | Don't retry. Fix the root cause. |
| `retryable` | `NODE_OFFLINE`, `BROADCAST_FAILED`, `ALL_NODES_FAILED` | Auto-retry with backoff. |
| `recoverable` | `HANDSHAKE_FAILED` | Try a different node. |

---

## 13. Display Helpers (Helpers)

Static utility methods for formatting chain data into human-readable strings.

```csharp
static string FormatP2P(long udvpn, int decimals = 2)
```
Format micro-denomination as P2P display string. `FormatP2P(40_152_030)` returns `"40.15 P2P"`.

```csharp
static string ShortAddress(string addr, int prefix = 12, int suffix = 6)
```
Truncate a bech32 address for display. `ShortAddress("sent1example9pqrse8q4m6lz8alxqv5hkx3fkxe0q")` returns `"sent1example...fkxe0q"`.

```csharp
static string FormatBytes(long bytes)
```
Format byte count into human-readable string (e.g. "1.5 GB", "340 MB").

```csharp
static string FormatExpiry(string isoTimestamp)
```
Format ISO timestamp into relative expiry string (e.g. "23d left", "4h left", "expired").

```csharp
static string FormatUptime(TimeSpan uptime)
```
Format TimeSpan as compact uptime string (e.g. "2h 15m").

```csharp
static (double Seconds, int Hours, int Minutes, string Formatted) ParseChainDuration(string durationStr)
```
Parse a Sentinel chain duration string like `"557817.72s"` into structured data.

---

## 14. Types (Records)

All data records used across the SDK.

```csharp
record Balance(long Udvpn, decimal P2P, string Display)
```
Wallet balance with micro-denomination, decimal, and display values.

```csharp
record ChainNode(string Address, string[] RemoteAddrs, string? RemoteUrl, PriceEntry[] GigabytePrices, PriceEntry[] HourlyPrices, int Status)
```
A dVPN node registered on the chain. Status: 1 = active, 2 = inactive.

```csharp
record PriceEntry(string Denom, string BaseValue, string QuoteValue)
```
A pricing entry with denomination and value information.

```csharp
record Subscription(string Id, string AccAddress, string PlanId, PriceEntry? Price, string Status, string StartAt, string InactiveAt)
```
A subscription linking an account to a node or plan.

```csharp
record ChainSession(string Id, string AccAddress, string NodeAddress, string DownloadBytes, string UploadBytes, string MaxBytes, string Status)
```
An active or historical bandwidth session.

```csharp
record DiscoveredPlan(int Id, int Subscribers, int NodeCount, PriceEntry? Price)
```
A discovered subscription plan from the chain.

```csharp
record FeeGrant(string Granter, string Grantee, object Allowance)
```
A fee grant allowing one account to pay fees on behalf of another.

```csharp
record TxResult(string TxHash, int Code, string RawLog, bool Success)
```
Result of a broadcast transaction. Code 0 = success.

```csharp
record ActiveSession(ulong Id, string NodeAddress, SessionStatus Status)
```
An active session as returned by chain queries.

```csharp
record RawSessionAllocation(long MaxBytes, long UsedBytes)
```
Raw allocation data from an on-chain session query.

### Enums

```csharp
enum SessionStatus { Active, Inactive }
enum HandshakeType { WireGuard, V2Ray }
```

---

## 15. Constants

Static chain values, endpoints, and configuration.

| Constant | Value | Description |
|----------|-------|-------------|
| `Constants.ChainId` | `"sentinelhub-2"` | Sentinel mainnet chain ID. |
| `Constants.Denom` | `"udvpn"` | Micro-denomination (1 P2P = 1,000,000 udvpn). |
| `Constants.GasPrice` | `"0.2"` | Gas price in udvpn per gas unit. |
| `Constants.BechPrefix` | `"sent"` | Bech32 prefix for user accounts. |
| `Constants.NodePrefix` | `"sentnode"` | Bech32 prefix for node operator accounts. |
| `Constants.ProviderPrefix` | `"sentprov"` | Bech32 prefix for provider accounts. |
| `Constants.DefaultLcdUrls` | `string[4]` | LCD REST API endpoints with fallback ordering. |
| `Constants.DefaultRpcUrls` | `string[3]` | RPC endpoints with fallback ordering. |

### Default Endpoints

**LCD (REST API):**
1. `https://lcd.sentinel.co`
2. `https://api.sentinel.quokkastake.io`
3. `https://sentinel-api.polkachu.com`
4. `https://sentinel-rest.publicnode.com`

**RPC:**
1. `https://rpc.sentinel.co:443`
2. `https://sentinel-rpc.polkachu.com`
3. `https://rpc.mathnodes.com`
