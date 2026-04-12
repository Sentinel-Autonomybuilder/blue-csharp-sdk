# Sentinel dVPN SDK — C# / .NET 8

Complete protocol library for building decentralized VPN applications on the [Sentinel network](https://sentinel.co) with **C# and .NET 8**. WireGuard and V2Ray tunnels, wallet management, session handling, and all chain message types for Sentinel v3. 814+ tests, zero external service dependencies. Includes a full **WPF desktop client example** with documented UI patterns.

**Also available:** [JavaScript SDK](https://github.com/Sentinel-Autonomybuilder/sentinel-dvpn-sdk) | [AI Connect](https://github.com/Sentinel-Autonomybuilder/sentinel-ai-connect) (zero-config JS wrapper)

## Quick Start

```csharp
using var vpn = new SentinelVpnClient(SentinelWallet.FromMnemonic(mnemonic), new());
var result = await vpn.ConnectAutoAsync(new ConnectAutoOptions { Countries = ["DE", "US"] });
await vpn.DisconnectAsync(); // or let IDisposable handle it
```

## Install

```bash
dotnet add package Sentinel.SDK          # Meta-package (all three)
dotnet add package Sentinel.SDK.Core     # Wallet + chain only
dotnet add package Sentinel.SDK.Node     # + handshake + VPN client
dotnet add package Sentinel.SDK.Tunnel   # + WireGuard/V2Ray
```

Requires .NET 8.0+. WireGuard nodes require admin privileges; without admin, the SDK connects via V2Ray nodes (~70% of the network). V2Ray 5.2.1 binary required for V2Ray nodes (do NOT use 5.44.1+).

## NuGet Packages

| Package | Description |
|---------|-------------|
| **Sentinel.SDK.Core** | Wallet, chain client, protobuf encoding, transaction building, error hierarchy, helpers, state persistence |
| **Sentinel.SDK.Node** | Node discovery, V3 handshake, session management, `SentinelVpnClient` orchestrator |
| **Sentinel.SDK.Tunnel** | WireGuard tunnel service, V2Ray process management, kill switch, DNS leak prevention |

## Architecture

```
                    ┌──────────────────────────────────────────┐
                    │           Your Application               │
                    └─────────────────┬────────────────────────┘
                                      │
                    ┌─────────────────▼────────────────────────┐
                    │         Sentinel.SDK.Node                 │
                    │                                           │
                    │  SentinelVpnClient (orchestrator)         │
                    │    ConnectAsync          SessionManager   │
                    │    ConnectAutoAsync      Handshake        │
                    │    ConnectViaSubAsync    NodeClient        │
                    │    DisconnectAsync       DiagnoseAsync    │
                    │    Events: Progress, Connected,           │
                    │            Disconnected, Error            │
                    └──────┬───────────────────────┬───────────┘
                           │                       │
          ┌────────────────▼──────────┐  ┌────────▼─────────────────┐
          │    Sentinel.SDK.Core      │  │  Sentinel.SDK.Tunnel     │
          │                           │  │                           │
          │  SentinelWallet           │  │  WireGuard/               │
          │  ChainClient (LCD+RPC)    │  │    WireGuardTunnel       │
          │  TransactionBuilder       │  │    KillSwitch            │
          │  MessageBuilder (17 msgs) │  │    DnsLeakPrevention     │
          │  SentinelErrors (42 codes)│  │  V2Ray/                  │
          │  AutoReconnect            │  │    V2RayConfigBuilder    │
          │  CircuitBreaker           │  │    V2RayProcess          │
          │  NetworkMonitor           │  └───────────────────────────┘
          │  NodeCache, SpeedTest     │
          │  SessionTracker           │
          │  VpnSettings, Helpers     │
          │  ISdkLogger, StateManager │
          │  TofuTrustStore           │
          └───────────────────────────┘
```

## Full Example

```csharp
using Sentinel.SDK.Core;
using Sentinel.SDK.Node;

// 1. Create or restore wallet
using var wallet = SentinelWallet.FromMnemonic("your twelve word mnemonic phrase goes here ...");

// 2. Create VPN client with options
using var vpn = new SentinelVpnClient(wallet, new SentinelVpnOptions
{
    Gigabytes = 1,
    FullTunnel = true,
    Logger = new ConsoleSdkLogger(),
});

// 3. Subscribe to events for UI binding
vpn.Progress += (_, e) => Console.WriteLine($"[{e.Step}] {e.Detail}");
vpn.Connected += (_, e) => Console.WriteLine($"Connected to {e.NodeAddress}");
vpn.Disconnected += (_, e) => Console.WriteLine($"Disconnected: {e.Reason}");
vpn.Error += (_, e) => Console.WriteLine($"Error [{e.Code}]: {e.Message}");

// 4. Auto-pick best node and connect
var result = await vpn.ConnectAutoAsync(new ConnectAutoOptions
{
    Countries = ["DE", "US"],
    ServiceType = "wireguard",
    MaxAttempts = 3,
});

Console.WriteLine($"VPN active! Node: {result.NodeAddress}, Session: {result.SessionId}");

// 5. Verify traffic is routed through VPN
var verify = await vpn.VerifyConnectionAsync();
Console.WriteLine($"External IP: {verify.ExternalIp}");

// 6. Disconnect (or let Dispose handle it)
await vpn.DisconnectAsync();
```

## Key Features

- **`IDisposable` everywhere** -- `SentinelWallet`, `SentinelVpnClient`, `ChainClient` all implement `IDisposable` for deterministic cleanup. `using` statements guarantee tunnel teardown and key material disposal
- **Full `async/await`** -- every network operation is async with `CancellationToken` support
- **`ISdkLogger` interface** -- plug in your logging framework (`Serilog`, `NLog`, `ILogger<T>` adapter). Ships with `ConsoleSdkLogger` and `NullSdkLogger`
- **Event-driven** -- `Progress`, `Connected`, `Disconnected`, `Error` events for clean UI binding (WPF, MAUI, Avalonia)
- **Record types** -- immutable options (`SentinelVpnOptions`, `ConnectAutoOptions`) and results (`ConnectionResult`, `ConnectionDiagnostics`)
- **Typed exception hierarchy** -- `SentinelException` base with `WalletException`, `ChainException`, `NodeException`, `TunnelException`, `HandshakeException`, each carrying `.Code` and `.Details`
- **Two tunnel protocols** -- WireGuard (kernel-level, fastest) and V2Ray (userspace, no admin required) with automatic fallback
- **42 typed error codes** -- machine-readable `.Code` on every exception, with severity levels (`fatal`, `retryable`, `recoverable`) and human-friendly messages via `ErrorSeverity.UserMessage()`
- **17 chain message types** -- sessions, subscriptions, plans, providers, leases, fee grants, bank send
- **Session reuse** -- detects existing active sessions to avoid double-paying
- **409 conflict recovery** -- automatically creates new session on handshake conflict
- **Clock drift detection** -- skips V2Ray nodes with >120s drift (VMess AEAD failure)
- **Auto-reconnect** -- `AutoReconnect` class with exponential backoff and configurable retry policy
- **Circuit breaker** -- `CircuitBreaker` prevents repeated connections to failing nodes
- **Network monitor** -- `NetworkMonitor` tracks system network state changes
- **Kill switch** -- firewall rules block all non-VPN traffic while connected
- **DNS leak prevention** -- forces DNS through the tunnel
- **TOFU TLS** -- trust-on-first-use certificate pinning per node
- **LCD failover** -- automatic rotation across 4 LCD endpoints
- **Speed testing** -- direct and SOCKS5 proxy speed measurement
- **State persistence** -- save/load connection state across process restarts

## Project Structure

```
csharp-sdk/
├── Sentinel.SDK.sln
├── src/
│   ├── Sentinel.SDK.Core/              # Foundation layer
│   │   ├── Wallet.cs                     Key generation, BIP39/BIP44, secp256k1, Bech32
│   │   ├── ISentinelWallet.cs            Wallet interface for testability
│   │   ├── ChainClient.cs               LCD queries with failover + broken pagination handling
│   │   ├── IChainClient.cs              Chain client interface for DI
│   │   ├── TransactionBuilder.cs         SIGN_MODE_DIRECT, gas estimation, sequence recovery
│   │   ├── MessageBuilder.cs             17 Cosmos message types (protobuf)
│   │   ├── ProtobufWriter.cs             Low-level protobuf wire format
│   │   ├── SentinelErrors.cs             Exception hierarchy + 42 error codes + severity map
│   │   ├── ISdkLogger.cs                 Pluggable logger interface + Console/Null implementations
│   │   ├── AutoReconnect.cs              Reconnection with exponential backoff
│   │   ├── CircuitBreaker.cs             Fail-fast for repeatedly-failing nodes
│   │   ├── NetworkMonitor.cs             System network state tracking
│   │   ├── NodeCache.cs                  In-memory node cache with TTL
│   │   ├── CredentialStore.cs            Encrypted credential persistence
│   │   ├── SessionTracker.cs             Session state + payment mode tracking
│   │   ├── StateManager.cs               Connection state persistence
│   │   ├── VpnSettings.cs               Typed settings with defaults
│   │   ├── SpeedTest.cs                  Direct + SOCKS5 speed measurement
│   │   ├── DynamicTransportRates.cs      Transport reliability scoring
│   │   ├── BatchBuilder.cs               Batch TX construction (operator use)
│   │   ├── NodeTester.cs                 Network audit tooling (operator use)
│   │   ├── TofuTrustStore.cs             TLS certificate pinning store
│   │   ├── DependencyCheck.cs            Runtime dependency verification
│   │   ├── SystemProxy.cs               System proxy configuration
│   │   ├── Constants.cs                  Chain IDs, endpoints, gas prices
│   │   ├── Helpers.cs                    FormatP2P, FormatBytes, ShortAddress, ...
│   │   └── Types.cs                      Shared types, enums, records
│   │
│   ├── Sentinel.SDK.Node/               # Connection layer
│   │   ├── SentinelVpnClient.cs          High-level orchestrator (Connect/Auto/Plan/Disconnect)
│   │   ├── SentinelVpnService.cs         Background service wrapper
│   │   ├── Handshake.cs                  V3 handshake protocol implementation
│   │   ├── NodeClient.cs                 Node status + metadata queries
│   │   └── SessionManager.cs             Session lifecycle + allocation tracking
│   │
│   └── Sentinel.SDK.Tunnel/             # Tunnel layer
│       ├── WireGuard/
│       │   ├── WireGuardTunnel.cs          Windows service tunnel management
│       │   ├── KillSwitch.cs               Firewall-based traffic blocking
│       │   └── DnsLeakPrevention.cs        DNS override + leak prevention
│       └── V2Ray/
│           ├── V2RayConfigBuilder.cs       JSON config matching sentinel-go-sdk format
│           └── V2RayProcess.cs             V2Ray process lifecycle management
│
├── tests/
│   └── Sentinel.SDK.Tests/              # 814+ tests across 33 test classes
│
└── docs/
    ├── QUICK-START.md                   Get running in under 50 lines
    ├── API-REFERENCE.md                 Complete public API catalog
    └── EDGE-CASES.md                    Gotchas and production lessons learned
```

## Example: Full WPF Desktop Client

The [`examples/HandshakeDVPN/`](examples/HandshakeDVPN/) directory contains a complete, runnable dVPN desktop application built with WPF and .NET 8. Use it as a reference for building your own Windows desktop VPN client.

**What's included (5,980 lines across 7 files):**

| File | Lines | What You Learn |
|------|-------|----------------|
| `App.xaml` | 125 | Complete WPF theme — color tokens, font loading, reusable button/textbox styles with hover/disabled states |
| `App.xaml.cs` | 62 | App startup, backend initialization, exception handling |
| `MainWindow.xaml` | 583 | Full layout — sidebar node browser, connection orb, status bar, test dashboard, wallet overlay |
| `MainWindow.xaml.cs` | 3,669 | Connection state machine, node rendering, search/filter, polling timers, speed display, animations |
| `Services/IHnsVpnBackend.cs` | 403 | Service interface + all data models (nodes, sessions, status, pricing) |
| `Services/NativeVpnClient.cs` | 1,076 | SDK integration — wallet, chain queries, connect/disconnect, session management, DNS config |
| `Services/DiskCache.cs` | 62 | JSON file persistence to LocalAppData |

**Features:** node browser with search/filter, animated connection orb, per-GB and per-hour pricing, real-time speed display, built-in node tester with export, Handshake DNS integration, wallet management (create/import/send), session tracking.

See the [example README](examples/HandshakeDVPN/README.md) for the full architecture guide, UI structure, and code-behind patterns.

## Error Handling

Every SDK error extends `SentinelException` with a machine-readable `.Code`:

```csharp
using Sentinel.SDK.Core;

try
{
    var result = await vpn.ConnectAutoAsync(opts);
}
catch (NodeException ex) when (ErrorSeverity.Get(ex.Code) == "retryable")
{
    // Try another node
    logger.Warn($"Retryable: {ex.Code} -- {ErrorSeverity.UserMessage(ex.Code)}");
}
catch (ChainException ex)
{
    logger.Error($"Chain error [{ex.Code}]: {ex.Message}");
}
catch (TunnelException ex)
{
    logger.Error($"Tunnel error [{ex.Code}]: {ex.Message}");
}
```

Exception hierarchy: `SentinelException` > `WalletException`, `ChainException`, `NodeException`, `TunnelException`, `HandshakeException`.

## Message Builder (17 Message Types)

| Category | Messages |
|----------|----------|
| Session | `StartSession`, `EndSession` |
| Subscription | `StartSubscription`, `SubStartSession`, `PlanStartSession` |
| Plan | `CreatePlan`, `UpdatePlanStatus`, `LinkNode`, `UnlinkNode` |
| Provider | `RegisterProvider`, `UpdateProviderDetails`, `UpdateProviderStatus` |
| Lease | `StartLease`, `EndLease` |
| Bank | `Send` |
| Fee Grant | `GrantFeeAllowance`, `RevokeFeeAllowance` |

## Security

| Feature | Description |
|---------|-------------|
| **Kill switch** | Firewall rules block all traffic outside the VPN tunnel |
| **DNS leak prevention** | Overrides system DNS to prevent queries outside the tunnel |
| **TOFU TLS** | Pins node TLS certificates on first contact; alerts on change |
| **On-chain sessions** | Session start/end recorded on the Sentinel blockchain |
| **Key disposal** | `IDisposable` ensures wallet key material is zeroed on cleanup |
| **No accounts** | Wallet-based authentication only. No servers, no sign-ups |
| **No external dependencies** | Connects directly to decentralized nodes. No relay servers |
| **Credential store** | Encrypted persistence for sensitive configuration |

## Sentinel Chain v3

The SDK targets Sentinel chain v3. Key differences from v2:

- Nodes use `service_type` (not `type`) and `remote_addrs` array (not `remote_url` string)
- Sessions are wrapped in `base_session`
- Active node status is `status=1` (not `STATUS_ACTIVE`)
- Provider queries remain on v2 (`/sentinel/provider/v2/`)
- Token: **P2P** (chain denom: `udvpn`, 1 P2P = 1,000,000 udvpn)

LCD failover endpoints (rotated automatically):
1. `https://lcd.sentinel.co`
2. `https://api.sentinel.quokkastake.io`
3. `https://sentinel-api.polkachu.com`
4. `https://sentinel.api.trivium.network:1317`

| Property | Value |
|----------|-------|
| Chain ID | `sentinelhub-2` |
| Denom | `udvpn` (1 P2P = 1,000,000 udvpn) |
| HD Path | `m/44'/118'/0'/0/0` |
| Bech32 | `sent1` (account), `sentnode1` (node), `sentprov1` (provider) |
| Gas Price | `0.2 udvpn` per gas unit |

## Dependencies

| Package | Purpose |
|---------|---------|
| NBitcoin | BIP39, BIP44, secp256k1, Bech32 |
| Google.Protobuf | Protobuf message encoding |
| NSec.Cryptography | X25519 (Curve25519) for WireGuard key generation |

## Building and Testing

814+ tests across 33 test classes covering wallet operations, chain queries, transaction building, message encoding, handshake protocol, V2Ray configuration, WireGuard tunnels, error handling, session management, and live mainnet integration.

```bash
dotnet build
dotnet test
```

## Documentation

- [Quick Start](docs/QUICK-START.md) -- Get running in under 50 lines
- [API Reference](docs/API-REFERENCE.md) -- Complete public API catalog
- [Edge Cases](docs/EDGE-CASES.md) -- Gotchas and production lessons learned
- Protocol specs: V3-HANDSHAKE-SPEC, V2RAY-CONFIG-SPEC, WIREGUARD-CONFIG-SPEC, LCD-API-REFERENCE (see `sentinel-proto/` and SDK docs)

## License

[MIT](LICENSE)
