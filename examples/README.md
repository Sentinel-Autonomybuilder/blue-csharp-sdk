# Sentinel SDK C# Examples

Runnable examples demonstrating the Sentinel dVPN SDK for .NET.

## Prerequisites

- .NET 8.0 SDK
- WireGuard installed (for WireGuard nodes) and/or V2Ray binary in `bin/`
- Administrator privileges for WireGuard connections

## Examples

| Example | Description | Wallet Needed | Admin Needed |
|---------|-------------|:---:|:---:|
| **ConnectDirect** | Minimal working VPN connection — create wallet, connect to a node, verify, disconnect | Yes | Yes (WG) |
| **QueryNodes** | Query online nodes from the chain and display as a table with pricing | No | No |
| **WalletBasics** | Generate a new wallet, display address, check P2P balance | No | No |

## Running

Each example is a standalone console app. Run from the example's directory:

```bash
# Query nodes (no wallet needed)
cd QueryNodes
dotnet run

# Generate a new wallet and check balance
cd WalletBasics
dotnet run

# Connect to a specific node (requires mnemonic + node address)
cd ConnectDirect
dotnet run -- "your mnemonic words here" sentnode1abc123...
```

## Notes

- All examples reference the SDK projects via `ProjectReference` — no NuGet package needed.
- `ConnectDirect` requires a funded wallet (at least ~0.05 P2P for a 1 GB session).
- `QueryNodes` is read-only and makes no transactions.
- Run `ConnectDirect` as administrator for WireGuard support.
