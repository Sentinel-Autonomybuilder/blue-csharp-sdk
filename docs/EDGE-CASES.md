# Sentinel dVPN C# SDK -- Edge Cases & Gotchas

Production-tested knowledge from building against the live Sentinel chain. Every entry here has caused real bugs. Read before shipping.

---

## Chain & LCD

### LCD Pagination Is Broken

The Sentinel LCD `count_total` field lies, and `next_key` is null on plan endpoints even when more pages exist. The SDK's `ChainClient` handles this automatically: when items returned equals the page limit but `next_key` is null, it falls back to a single `limit=5000` request.

**You will see this warning in stderr:**
```
[Sentinel.SDK] Broken pagination detected on /sentinel/nodes: got 100 items (== limit) but no next_key.
Falling back to limit=5000 single request.
```

This is expected behavior, not a bug in your app.

### Plan List Endpoint Returns 501

`/sentinel/plan/v3/plans` returns HTTP 501 (Not Implemented). You cannot list all plans in one call. Instead, use `ChainClient.DiscoverPlansAsync()` which probes individual plan IDs by hitting `/sentinel/plans/{id}` for IDs 1 through `maxId`.

### Provider Endpoint Is v2, Not v3

Provider queries use `/sentinel/provider/v2/...`, not v3. The v3 provider endpoints do not exist on the current chain (Cosmos SDK 0.47.17, Hub v12.0.0).

### Duration Strings Have "s" Suffix

On-chain duration values come as strings like `"557817.72s"` (seconds with optional decimal, trailing "s"). Use `Helpers.ParseChainDuration()` to parse them safely. Do not pass them to `TimeSpan.Parse()`.

### Session IDs Are ulong

Session IDs on chain can exceed `int.MaxValue`. The SDK uses `ulong` for session IDs internally. When serializing to JSON for APIs, convert to string (`sessionId.ToString()`) -- JavaScript cannot handle integers above `2^53` safely.

### `remote_addrs` (Array) Not `remote_url` (String) in v3 LCD

The v3 LCD response uses `remote_addrs` (string array), not `remote_url` (string). The SDK's parser handles both formats: it checks for `remote_url` first and wraps it in an array. If your code queries the LCD directly, be aware of this inconsistency.

---

## Sessions

### Chain Session Is Not Node Session

A session has two lifetimes:
1. **Chain session:** Created when a `MsgStartSession` TX is confirmed. Tracked by session ID on chain.
2. **Node session:** Created when the node's V3 API accepts the handshake for that session ID.

These are independent. A chain session can exist without a node session (if the handshake hasn't happened yet). Critically, a node session can be "used up" by a failed handshake -- the node remembers the session ID and returns HTTP 409 "already exists in database" on subsequent handshake attempts.

### 409 "Already Exists" -- Session Poisoning

When a handshake fails mid-flight (network timeout, V2Ray config error, etc.), the node marks the session ID as consumed. Further handshake attempts with the same session ID return 409 Conflict.

The `SentinelVpnClient` handles this automatically: on 409, it creates a new chain session and retries the handshake. If you use `Handshake.HandshakeAsync()` directly, catch `SentinelHandshakeException` where `Code == ErrorCodes.SessionAlreadyExists` and create a new session.

### Sessions Nest Data Under `base_session`

Some LCD session query responses nest the actual session data under a `base_session` key. When parsing LCD responses directly, check for this nesting pattern. The `ChainClient` handles this internally.

### Always Wait 5 Seconds After Session TX

After broadcasting a `MsgStartSession` TX, wait at least 5 seconds before attempting the handshake. The node needs time to observe the TX on chain. The `SentinelVpnClient` includes this delay automatically.

### Reuse Existing Sessions

Always call `SessionManager.FindExistingSessionAsync()` before creating a new session. If an active session already exists for the wallet+node pair, reuse it -- no payment needed. The `SentinelVpnClient` does this by default (controlled by `ForceNewSession` option).

---

## Plans & Subscriptions

### Plans Start INACTIVE

When you create a plan via `MessageBuilder.CreatePlan()`, it starts with status INACTIVE. You must separately broadcast `MessageBuilder.UpdatePlanStatus(from, planId, status: 1)` to activate it.

### Fee Grants: Granter Cannot Equal Grantee

The chain rejects `MsgGrantAllowance` where the granter and grantee are the same address. Validate before broadcasting:

```csharp
if (granter == grantee)
    throw new SentinelException("FEE_GRANT_SELF", "Cannot grant fees to yourself");
```

### Self-Subscription Is Allowed

A plan owner CAN subscribe to their own plan. This is a valid on-chain operation. If you're counting subscribers, be aware that the owner's subscription inflates the count by 1. Filter it out if needed using `SentinelWallet.IsSameKey()`.

---

## V2Ray

### VMess Clock Drift >120s Causes Silent Failure

VMess uses AEAD with timestamp-based authentication. If the node's clock differs from the client's clock by more than 120 seconds, connections silently fail -- data appears to flow but decryption fails, causing a "bandwidth drain" with no usable traffic.

The SDK checks `NodeStatus.ClockDriftSec` and throws `ClockDriftTooHigh` for V2Ray nodes with >120s drift. If using the Handshake directly, check this yourself.

### V2Ray Must Be Version 5.2.1

V2Ray 5.44.1+ has observatory subsystem bugs that cause routing failures with Sentinel nodes. Always use version 5.2.1 exactly. The SDK does not enforce this version check, but your app should:

```csharp
// Recommended: validate V2Ray version at startup
var versionOutput = RunProcess("v2ray.exe", "version");
if (!versionOutput.Contains("5.2.1"))
    Warn("V2Ray version mismatch. Use 5.2.1 for reliable connections.");
```

### Transport Number Mapping

Sentinel encodes transport types as integers. The mapping is:

| Number | Transport | V2Ray Network |
|--------|-----------|---------------|
| 1 | domainsocket | ds |
| 2 | gun | gun |
| 3 | grpc | grpc |
| 4 | http | http |
| 5 | mkcp | kcp |
| 6 | quic | quic |
| 7 | tcp | tcp |
| 8 | websocket | ws |

**gun (2) and grpc (3) are DIFFERENT protocols.** gun uses raw HTTP/2 framing; grpc uses the gRPC library. Do not merge them.

### SOCKS5 Must Use Password Authentication

The V2Ray local SOCKS5 proxy MUST use password authentication (not `noauth`). A `noauth` SOCKS5 proxy on `127.0.0.1` is an open proxy that any local process can exploit. The SDK generates random credentials automatically via `V2RayConfigBuilder.BuildConfigWithAuth()`.

### V2Ray Config Non-Negotiables

These rules come from matching the sentinel-go-sdk's `client.json.tmpl` exactly. Deviating causes silent connection failures:

- VLess: `encryption = "none"`, **NO** `flow` field
- VMess: `alterId = 0`, **NO** `security` in user object
- UUID field name: `"uuid"` (not `"id"` or `"ID"`)
- **NO** per-outbound transport settings
- **NO** `serverName` in `tlsSettings`
- `allowInsecure = true` in `tlsSettings` (nodes use self-signed certs)

### grpc/none Works, grpc/tls Does Not

From 780-node scan data: `grpc` transport with no TLS has ~58% success rate. `grpc` with TLS has 0% success rate. The SDK does not block `grpc/tls`, but expect failures.

---

## WireGuard

### Admin/Root Privileges Required

WireGuard tunnel installation requires administrator privileges on Windows. The `WireGuardTunnel` class checks for admin on `InstallAsync()` and throws `SentinelException` with code `ADMIN_REQUIRED` if not elevated.

Your app should either:
- Request elevation at startup (manifest `requireAdministrator`)
- Use a separate elevated service process for tunnel management
- Fall back to V2Ray (which doesn't need admin) when WireGuard is unavailable

### MTU Must Be 1280

Sentinel nodes require MTU 1280. The default WireGuard MTU (1420) causes packet fragmentation and connection failures. The SDK hard-codes MTU 1280 in the generated config.

### PersistentKeepalive Must Be 15

The keepalive interval must be 15 seconds, not the WireGuard default of 25. NAT traversal requires the shorter interval to maintain the UDP hole punch through firewalls. The SDK hard-codes this value.

---

## UUID / Cryptography

### UUID Must Be RFC 4122 Big-Endian

.NET `Guid.ToByteArray()` returns mixed-endian bytes (first three groups are little-endian, last two are big-endian). This does NOT match the RFC 4122 / Go `uuid.UUID` byte ordering expected by Sentinel nodes.

The SDK handles this correctly by parsing the UUID hex string directly:

```csharp
// WRONG: mixed-endian, will produce invalid UUIDs
var badBytes = guid.ToByteArray();

// CORRECT: RFC 4122 big-endian, matches Go uuid.UUID
var uuidStr = guid.ToString("N");  // 32 hex chars, no dashes
var goodBytes = Convert.FromHexString(uuidStr);
```

If you build handshake requests manually, use the hex string approach.

---

## HttpClient

### Share HttpClient Instances

Creating a new `HttpClient` per request causes socket exhaustion (TIME_WAIT socket accumulation). The SDK uses shared static `HttpClient` instances in `Handshake`, `NodeClient`, and `ChainClient`.

If you make HTTP calls outside the SDK (e.g., speed tests through the SOCKS5 proxy), reuse a single `HttpClient`:

```csharp
// WRONG: socket exhaustion
foreach (var url in urls)
{
    using var client = new HttpClient();  // Bad!
    await client.GetAsync(url);
}

// CORRECT: reuse
private static readonly HttpClient SharedClient = new();
foreach (var url in urls)
{
    await SharedClient.GetAsync(url);
}
```

### Self-Signed TLS Certificates

Sentinel nodes use self-signed TLS certificates. The SDK's `HttpClient` instances are configured with `ServerCertificateCustomValidationCallback = (_, _, _, _) => true`. If you create your own `HttpClient` for node communication, you must do the same:

```csharp
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
};
var client = new HttpClient(handler);
```

---

## Token Naming

### `udvpn` Is the Chain Denom, `P2P` Is the Display Name

- On chain and in code: use `udvpn` (the micro-denomination). `Constants.Denom` = `"udvpn"`.
- In user-facing UI: use `P2P`. `1 P2P = 1,000,000 udvpn`.
- Use `Helpers.FormatP2P()` to format for display.

---

## Transaction Sequence

### Sequence Mismatch Recovery

The `TransactionBuilder` automatically retries up to 3 times on sequence mismatch errors (chain error code 32). Before each retry, it checks whether the previous TX was already committed (double-spend protection).

If you see code 32 errors in logs, this is normal -- it means two transactions raced and the SDK is self-healing.

### Gas Estimation

The SDK estimates gas per message type with a 1.4x safety multiplier:

| Message Type | Base Gas |
|-------------|----------|
| MsgSubscribe | 250,000 |
| MsgStart* | 200,000 |
| MsgEnd* | 150,000 |
| MsgSend | 100,000 |
| Default | 200,000 |

For `BroadcastAsync()` (SentinelMessage), gas is `200,000 * message_count`.

---

## Concurrency

### Connection Mutex

`SentinelVpnClient` uses a `SemaphoreSlim` to prevent concurrent connection attempts. If `ConnectAsync()` is already running, a second call throws `CONNECTION_IN_PROGRESS` immediately (non-blocking check). This prevents race conditions with session creation and tunnel installation.

### CancellationToken Support

All async methods accept `CancellationToken`. Cancel propagates cleanly through chain queries, handshake HTTP calls, and tunnel installation waits. After cancellation, no partial state is left behind.

---

## Platform Notes (Windows)

### WireGuard Config Directory

WireGuard configs are written to `C:\ProgramData\sentinel-wg\` with restricted ACLs (SYSTEM + Administrators only). The SDK creates this directory automatically.

### V2Ray Temp Config

V2Ray configs are written to `%TEMP%\sentinel-v2ray-{guid}.json`. They are deleted on `StopAsync()` and `Dispose()`. If the process crashes, stale temp files may remain in the temp directory.

### WireGuard Service Naming

The WireGuard tunnel service is registered as `WireGuardTunnel$wgsent0` (or custom tunnel name). The SDK polls `sc.exe query` to detect when the service transitions to RUNNING state.

---

## Address Conversion

### Bech32 Prefix Conversion

The same underlying key bytes can be encoded with different Bech32 prefixes:
- `sent1...` -- user account
- `sentnode1...` -- node operator
- `sentprov1...` -- provider

Use `wallet.ToSentnode()` and `wallet.ToSentprov()` for conversions. Use `SentinelWallet.IsSameKey()` to compare addresses across prefixes.

Provider operations (CreatePlan, LinkNode, etc.) require the `sentprov1...` address as the `from` field.
