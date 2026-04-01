# Sentinel dVPN SDK for C# / .NET

Native C# SDK for building Windows desktop VPN applications on the Sentinel dVPN network. Enables NordVPN-grade native apps using WPF, WinUI, MAUI, or any .NET framework.

## Architecture

```
Sentinel.SDK.Core      -- Wallet, chain client, TX building, 17 message types, error handling, helpers
Sentinel.SDK.Node      -- V3 handshake, node status, session management, SentinelVpnClient
Sentinel.SDK.Tunnel    -- WireGuard tunnel service + V2Ray process management (Windows)
```

## Quick Start

The simplest path: `SentinelVpnClient` orchestrates everything -- balance check, node selection, session creation, handshake, and tunnel installation.

```csharp
using Sentinel.SDK.Core;
using Sentinel.SDK.Node;

// 1. Create or restore wallet
using var wallet = SentinelWallet.FromMnemonic("your twelve word mnemonic phrase goes here ...");

// 2. Create VPN client
using var vpn = new SentinelVpnClient(wallet, new SentinelVpnOptions
{
    Gigabytes = 1,
    FullTunnel = true,
});

// 3. Subscribe to progress events
vpn.Progress += (_, e) => Console.WriteLine($"[{e.Step}] {e.Detail}");
vpn.Error += (_, e) => Console.WriteLine($"Error: {e.Exception.Message}");

// 4. Auto-pick best node and connect
var result = await vpn.ConnectAutoAsync(new ConnectAutoOptions
{
    Countries = new[] { "DE", "US" },
    ServiceType = "wireguard",
    MaxAttempts = 3,
});

Console.WriteLine($"VPN connected! Node: {result.NodeAddress}, Session: {result.SessionId}");
Console.ReadKey();

// 5. Disconnect (or let Dispose handle it)
await vpn.DisconnectAsync();
```

## Features

### SentinelVpnClient (High-Level Orchestrator)

- **ConnectAsync** -- Direct connection to a specific node (pay-per-GB)
- **ConnectAutoAsync** -- Auto-select best node with country/type filters and retry
- **ConnectViaSubscriptionAsync** -- Connect using an existing plan subscription
- **DisconnectAsync** -- Clean tunnel teardown
- **Events** -- `Progress`, `Connected`, `Disconnected`, `Error` for UI binding
- **Session reuse** -- Detects existing active sessions to avoid double-paying
- **409 recovery** -- Automatically creates new session on handshake conflict
- **Connection mutex** -- Prevents concurrent connect races
- **Clock drift detection** -- Skips V2Ray nodes with >120s drift (VMess AEAD failure)

### Wallet (SentinelWallet)

- BIP39 mnemonic generation (12/15/18/21/24 words)
- BIP44 key derivation (`m/44'/118'/0'/0/0`)
- secp256k1 signing (compact 64-byte signatures)
- Bech32 address encoding (sent1, sentnode1, sentprov1)
- Address comparison across prefixes (`IsSameKey`)
- `IDisposable` for key material lifecycle

### Chain Client (ChainClient)

- LCD REST API queries with endpoint failover (4 LCD endpoints)
- Automatic retry on network errors
- Broken pagination handling (fallback to `limit=5000`)
- Node, subscription, session, plan, and fee grant queries
- Plan discovery by probing individual IDs
- Available nodes through subscription lookup

### Transaction Builder

- SIGN_MODE_DIRECT protobuf wire-format encoding
- Automatic gas estimation per message type (1.4x safety multiplier)
- Sequence mismatch recovery (up to 3 retries)
- Double-spend detection before retry
- Batch message support (multiple messages per TX)

### Message Builder (17 Message Types)

| Category | Messages |
|----------|----------|
| Session | `StartSession`, `EndSession` |
| Subscription | `StartSubscription`, `SubStartSession`, `PlanStartSession` |
| Plan | `CreatePlan`, `UpdatePlanStatus`, `LinkNode`, `UnlinkNode` |
| Provider | `RegisterProvider`, `UpdateProviderDetails`, `UpdateProviderStatus` |
| Lease | `StartLease`, `EndLease` |
| Bank | `Send` |
| Fee Grant | `GrantFeeAllowance`, `RevokeFeeAllowance` |

### Error Handling

- **Typed exceptions** -- `SentinelException` base with `Code`, `Details`, `Message`
- **Hierarchy** -- `WalletException`, `ChainException`, `NodeException`, `TunnelException`, `HandshakeException`
- **Error codes** -- Machine-readable constants (`ErrorCodes.NodeOffline`, etc.)
- **Severity mapping** -- `ErrorSeverity.Get()` returns `"fatal"`, `"retryable"`, `"recoverable"`
- **User messages** -- `ErrorSeverity.UserMessage()` returns UI-ready strings

### Session Manager

- Find existing active sessions (avoid double-paying)
- Query bandwidth allocation (used/max/remaining bytes)

### Display Helpers

- `FormatP2P()` -- `40152030` to `"40.15 P2P"`
- `ShortAddress()` -- Truncate bech32 addresses for display
- `FormatBytes()` -- `1500000000` to `"1.4 GB"`
- `FormatExpiry()` -- ISO timestamp to `"23d left"`
- `FormatUptime()` -- TimeSpan to `"2h 15m"`
- `ParseChainDuration()` -- `"557817.72s"` to structured data

### Tunnel Management

- **WireGuard** -- Windows service installation, MTU 1280, keepalive 15s, admin check
- **V2Ray** -- Process lifecycle, JSON config generation, SOCKS5 with password auth
- **V2Ray config** -- Matches sentinel-go-sdk format exactly (non-negotiable rules)
- **Cleanup** -- Automatic on disconnect and dispose

## Requirements

- .NET 8.0+
- Windows 10/11 (for WireGuard tunnel management)
- Admin privileges (for WireGuard service installation)
- V2Ray 5.2.1 binary (for V2Ray nodes -- do NOT use 5.44.1+)

## Dependencies

| Package | Purpose |
|---------|---------|
| NBitcoin | BIP39, BIP44, secp256k1, Bech32 |
| Google.Protobuf | Protobuf message encoding (for BroadcastProtobufAsync) |
| NSec.Cryptography | X25519 (Curve25519) for WireGuard key generation |

## NuGet Packages (Planned)

```bash
dotnet add package Sentinel.SDK          # Meta-package (all three)
dotnet add package Sentinel.SDK.Core     # Wallet + chain only
dotnet add package Sentinel.SDK.Node     # + handshake + VPN client
dotnet add package Sentinel.SDK.Tunnel   # + WireGuard/V2Ray
```

## Building

```bash
dotnet build
dotnet test
```

## Chain Info

| Property | Value |
|----------|-------|
| Chain ID | `sentinelhub-2` |
| Cosmos SDK | `0.47.17` |
| Denom | `udvpn` (1 P2P = 1,000,000 udvpn) |
| HD Path | `m/44'/118'/0'/0/0` |
| Bech32 | `sent1` (account), `sentnode1` (node), `sentprov1` (provider) |
| Gas Price | `0.2 udvpn` per gas unit |

## Documentation

- [Quick Start](docs/QUICK-START.md) -- Get running in under 50 lines
- [API Reference](docs/API-REFERENCE.md) -- Complete public API catalog
- [Edge Cases](docs/EDGE-CASES.md) -- Gotchas and production lessons learned

## Protocol Specs

See `sentinel-proto/` for protobuf definitions and `Sentinel SDK/docs/` for:
- V3-HANDSHAKE-SPEC.md -- Handshake protocol
- V2RAY-CONFIG-SPEC.md -- V2Ray transport mapping
- WIREGUARD-CONFIG-SPEC.md -- WireGuard config mapping
- LCD-API-REFERENCE.md -- Chain REST API reference
