# Sentinel dVPN C# SDK -- Quick Start

Get a .NET VPN connection running in under 50 lines.

---

## Installation

```bash
# Meta-package (all three assemblies)
dotnet add package Sentinel.SDK

# Or install individually
dotnet add package Sentinel.SDK.Core     # Wallet + chain only
dotnet add package Sentinel.SDK.Node     # + handshake + VPN client
dotnet add package Sentinel.SDK.Tunnel   # + WireGuard/V2Ray
```

### Requirements

- .NET 8.0+
- Windows 10/11
- Admin privileges (for WireGuard tunnel service)
- V2Ray 5.2.1 binary (for V2Ray nodes only)

---

## 1. Generate a Wallet

```csharp
using Sentinel.SDK.Core;

// Generate a new wallet (12-word mnemonic)
using var wallet = SentinelWallet.Generate();
Console.WriteLine($"Mnemonic: {wallet.Mnemonic}");
Console.WriteLine($"Address:  {wallet.Address}");

// Or restore from an existing mnemonic
using var restored = SentinelWallet.FromMnemonic(
    "your twelve word mnemonic phrase goes here plus more words"
);
```

**Save the mnemonic securely.** It is the only way to recover the wallet. Never store it in source code.

---

## 2. Check Balance

```csharp
using Sentinel.SDK.Core;

using var wallet = SentinelWallet.FromMnemonic("...");
var client = new ChainClient();

var balance = await client.GetBalanceAsync(wallet.Address);
Console.WriteLine($"Balance: {balance.Display}");  // "12.34 P2P"
Console.WriteLine($"Raw:     {balance.Udvpn} udvpn");
```

---

## 3. List Online Nodes

```csharp
var nodes = await client.GetActiveNodesAsync(limit: 50);

foreach (var node in nodes)
{
    Console.WriteLine($"{node.Address} | {node.RemoteUrl} | {node.GigabytePrices.Length} prices");
}
```

---

## 4. Connect (One-Shot)

The simplest path: `SentinelVpnClient` handles everything -- balance check, node query, session creation, handshake, and tunnel installation.

```csharp
using Sentinel.SDK.Core;
using Sentinel.SDK.Node;

using var wallet = SentinelWallet.FromMnemonic("...");

using var vpn = new SentinelVpnClient(wallet, new SentinelVpnOptions
{
    Gigabytes = 1,
    FullTunnel = true,
});

// Subscribe to progress events (optional)
vpn.Progress += (_, e) => Console.WriteLine($"[{e.Step}] {e.Detail}");
vpn.Connected += (_, e) => Console.WriteLine($"Connected! Session: {e.Result.SessionId}");

// Auto-pick the best node and connect
var result = await vpn.ConnectAutoAsync(new ConnectAutoOptions
{
    Countries = new[] { "DE", "US" },   // Optional: filter by country
    ServiceType = "wireguard",           // Optional: "wireguard" or "v2ray"
    MaxAttempts = 3,                     // Try up to 3 nodes
});

Console.WriteLine($"VPN active via {result.ServiceType}");
Console.WriteLine($"Node:    {result.NodeAddress}");
Console.WriteLine($"Session: {result.SessionId}");

if (result.ServiceType == "wireguard")
    Console.WriteLine($"VPN IP:  {result.VpnIp}");
else
    Console.WriteLine($"SOCKS5:  127.0.0.1:{result.SocksPort}");
```

---

## 5. Connect to a Specific Node

```csharp
var result = await vpn.ConnectAsync("sentnode1abc123...");
```

---

## 6. Connect via Subscription

If you already have a plan subscription, skip the pay-per-GB session:

```csharp
var result = await vpn.ConnectViaSubscriptionAsync(
    subscriptionId: 12345,
    nodeAddress: "sentnode1abc123..."
);
```

---

## 7. Disconnect

```csharp
await vpn.DisconnectAsync();
```

Or let `Dispose()` handle it:

```csharp
using var vpn = new SentinelVpnClient(wallet);
// ... connect ...
// Automatic disconnect + cleanup when `using` block exits
```

---

## 8. Handle Errors with User-Friendly Messages

```csharp
try
{
    var result = await vpn.ConnectAutoAsync();
}
catch (SentinelException ex)
{
    // Machine-readable code for programmatic handling
    Console.WriteLine($"Error code: {ex.Code}");

    // User-friendly message for UI display
    Console.WriteLine($"Message:    {ErrorSeverity.UserMessage(ex.Code)}");

    // Should the app retry?
    if (ErrorSeverity.IsRetryable(ex.Code))
    {
        Console.WriteLine("This error is transient. Retrying...");
    }
    else
    {
        Console.WriteLine($"Severity: {ErrorSeverity.Get(ex.Code)}");
    }
}
```

### Error Severity Quick Reference

| Severity | Meaning | Examples |
|----------|---------|----------|
| `fatal` | Don't retry. Fix root cause. | Invalid mnemonic, insufficient balance |
| `retryable` | Auto-retry with backoff. | Node offline, broadcast failed |
| `recoverable` | Try a different node. | Handshake failed |

---

## 9. Subscription Connection Decision Tree

Choosing the right connection method depends on your app's payment model:

```
Do you have a plan subscription?
  |
  +-- NO --> Use ConnectAsync() or ConnectAutoAsync()
  |          (pay-per-GB, creates new session with payment TX)
  |
  +-- YES --> Does the user already have an active subscription?
                |
                +-- NO --> First subscribe to the plan:
                |          var txBuilder = new TransactionBuilder(wallet, chainClient);
                |          await txBuilder.BroadcastAsync(
                |              MessageBuilder.StartSubscription(wallet.Address, planId)
                |          );
                |
                +-- YES --> Use ConnectViaSubscriptionAsync(subscriptionId, nodeAddress)
                            (no payment TX needed, uses subscription quota)
```

### Check Subscription Status

```csharp
// Check if user is already subscribed to a plan
bool hasSub = await client.HasActiveSubscriptionAsync(wallet.Address, planId: 42);

// Get all subscriptions
var subs = await client.GetSubscriptionsAsync(wallet.Address);
foreach (var sub in subs)
{
    Console.WriteLine($"Plan {sub.PlanId}: {sub.Status} ({Helpers.FormatExpiry(sub.InactiveAt)})");
}

// Get nodes available through subscriptions
var availableNodes = await client.GetAvailableNodesAsync(wallet.Address);
```

---

## 10. Session Management

```csharp
using Sentinel.SDK.Node;

// Check for existing session (avoid double-paying)
var existingId = await SessionManager.FindExistingSessionAsync(
    client, wallet.Address, "sentnode1abc123..."
);

if (existingId.HasValue)
    Console.WriteLine($"Reusing session {existingId.Value}");

// Check bandwidth allocation
var allocation = await SessionManager.GetSessionAllocationAsync(client, sessionId: 12345);
if (allocation is not null)
{
    Console.WriteLine($"Used: {Helpers.FormatBytes(allocation.UsedBytes)} / {Helpers.FormatBytes(allocation.MaxBytes)}");
    Console.WriteLine($"Remaining: {allocation.PercentUsed}% used");
}
```

---

## 11. Display Helpers

```csharp
using Sentinel.SDK.Core;

Helpers.FormatP2P(40_152_030);           // "40.15 P2P"
Helpers.ShortAddress("sent12e03w...");   // "sent12e03wzmx...fjhzg"
Helpers.FormatBytes(1_500_000_000);      // "1.4 GB"
Helpers.FormatExpiry("2026-04-15T12:00:00Z");  // "29d left"
Helpers.FormatUptime(TimeSpan.FromHours(2.25)); // "2h 15m"
Helpers.ParseChainDuration("557817.72s");       // (557817.72, 154, 56, "154h 56m")
```

---

## Full Example: WPF VPN App

```csharp
public class VpnViewModel : INotifyPropertyChanged, IDisposable
{
    private SentinelWallet? _wallet;
    private SentinelVpnClient? _vpn;

    public async Task ConnectAsync()
    {
        _wallet = SentinelWallet.FromMnemonic(MnemonicInput);

        _vpn = new SentinelVpnClient(_wallet, new SentinelVpnOptions
        {
            Gigabytes = 1,
            FullTunnel = true,
        });

        _vpn.Progress += (_, e) => StatusText = e.Detail;
        _vpn.Error += (_, e) => StatusText = ErrorSeverity.UserMessage(
            (e.Exception as SentinelException)?.Code ?? ""
        );

        try
        {
            var result = await _vpn.ConnectAutoAsync(new ConnectAutoOptions
            {
                Countries = new[] { "DE" },
                ServiceType = "wireguard",
            });

            StatusText = $"Connected to {Helpers.ShortAddress(result.NodeAddress)}";
            IsConnected = true;
        }
        catch (SentinelException ex)
        {
            StatusText = ErrorSeverity.UserMessage(ex.Code);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_vpn is not null)
        {
            await _vpn.DisconnectAsync();
            IsConnected = false;
            StatusText = "Disconnected";
        }
    }

    public void Dispose()
    {
        _vpn?.Dispose();
        _wallet?.Dispose();
    }
}
```

---

## Next Steps

- [API Reference](API-REFERENCE.md) -- Complete public API catalog
- [Edge Cases](EDGE-CASES.md) -- Gotchas and production lessons learned
