# Handshake dVPN — Example WPF Desktop Client

> **This is an example application for builders, not a production release.** Use it as a starting point and reference for building your own Windows dVPN client with the Sentinel C# SDK.

A working example of a decentralized VPN desktop client built with **C# / WPF / .NET 8.0** on the [Sentinel Network](https://sentinel.co). Demonstrates how to integrate the Sentinel C# SDK into a real desktop application with a complete UI.

![Example](https://img.shields.io/badge/status-example-orange) ![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4) ![WPF](https://img.shields.io/badge/WPF-Desktop-blue) ![Sentinel](https://img.shields.io/badge/Sentinel-dVPN-green) ![License](https://img.shields.io/badge/license-MIT-lightgrey)

---

## What This Is

A **Windows-focused example application** that shows builders how to build a dVPN desktop app using:

- **WPF (Windows Presentation Foundation)** for the UI layer
- **Sentinel C# SDK** for blockchain interaction, node discovery, session management, and tunnel creation
- **WireGuard + V2Ray** tunnel protocols
- **Handshake DNS** (103.196.38.38) for decentralized name resolution through the tunnel

This is not a production release or a library — it's a complete, runnable example that demonstrates every pattern you need to build your own client. Fork it, rip it apart, use whatever you need.

---

## Features

| Feature | Implementation |
|---------|---------------|
| Wallet management | Create, import (mnemonic), persist encrypted |
| Node discovery | Query all active Sentinel nodes from chain |
| Node browser | Sidebar with search, country filter, protocol filter (WG/V2Ray) |
| Connection orb | Animated state indicator (off/connecting/connected) |
| WireGuard tunnels | Native WireGuard tunnel via SDK |
| V2Ray tunnels | VLess/VMess with multiple transports |
| Per-GB pricing | Pay per gigabyte of data used |
| Per-hour pricing | Pay per hour of connection time |
| Session management | View active sessions, data allocation, remaining bytes |
| Speed display | Real-time upload/download speed in status bar |
| IP display | Shows exit IP when connected |
| Node testing | Built-in test dashboard — test any node's speed and connectivity |
| Disk caching | Cache nodes, sessions, test results to local disk |
| Balance display | Live P2P token balance in topbar |
| Handshake DNS | All tunnel traffic resolves through HNS nameservers |

---

## Architecture

```
HandshakeDVPN/
  App.xaml                    # Theme definition — colors, fonts, button styles
  App.xaml.cs                 # Startup, backend initialization
  MainWindow.xaml             # Full UI layout (583 lines of XAML)
  MainWindow.xaml.cs          # All UI logic, event handlers, state management (3,669 lines)
  Services/
    IHnsVpnBackend.cs         # Interface + all data models (403 lines)
    NativeVpnClient.cs        # SDK wrapper — wallet, nodes, connect, disconnect (1,076 lines)
    DiskCache.cs              # Local persistence for nodes, sessions, test results (62 lines)
  Fonts/                      # Inter font family (Regular, Medium, SemiBold, Bold)
  HandshakeDVPN.csproj        # Project file — references Sentinel C# SDK
  app.manifest                # Admin elevation manifest (required for WireGuard)
```

**Total: ~5,980 lines of code across 7 source files.**

---

## UI Structure (XAML)

The UI is built entirely in XAML with code-behind — no MVVM framework, no third-party UI libraries. This makes it easy to read and learn from.

### Layout

```
Window (1360x920)
  TopBar (56px)
    Brand (Handshake logo + name)
    Wallet address + P2P balance
    Settings / Wallet / Logout buttons
  Body
    Sidebar (360px)
      Tab bar: Nodes | Sessions | Plans | Test
      Search box
      Filter buttons: All | WG | V2
      Scrollable node list
      Footer: node count + refresh
    Main Area
      Node info card (selected node details + pricing)
      Connection orb (animated circle — off/connecting/connected)
      Status text + sub-status
      Speed + IP bar (bottom)
    Test Dashboard (toggled via Test tab)
      Progress bar + stats
      Scrollable results with FAST/SLOW/FAIL badges
      Export (JSON/CSV) + Filter + Sort
```

### Theme System

All colors are defined as `SolidColorBrush` resources in `App.xaml`:

| Key | Color | Usage |
|-----|-------|-------|
| `Bg0` | `#FFFFFF` | Primary background |
| `Bg1` | `#FAFAFA` | Sidebar background |
| `Bg2` | `#F2F2F2` | Card backgrounds |
| `Bg3` | `#E8E8E8` | Hover states |
| `Bdr` | `#E0E0E0` | Borders |
| `T1` | `#000000` | Primary text |
| `T2` | `#555555` | Secondary text |
| `T3` | `#999999` | Muted text |
| `Green` | `#22C55E` | Connected / pass states |
| `Red` | `#DC2626` | Disconnected / fail states |
| `Amber` | `#D97706` | Warning states |
| `Blue` | `#4F8FFF` | Info accents |

### Button Styles

Three reusable button styles defined in `App.xaml`:

- **`BtnAcc`** — Primary action button (black background, white text, 8px corner radius)
- **`BtnGhost`** — Secondary button (transparent background, border, hover highlight)
- **`FilterBtn`** — Tab/filter button (no border, text-only, underline on active)

### Fonts

- **Sans:** Inter (bundled as resource — Regular, Medium, SemiBold, Bold)
- **Mono:** Cascadia Code / Consolas (system)
- **Emoji:** Segoe UI Emoji (system — used for country flags)

---

## Code-Behind Patterns (MainWindow.xaml.cs)

### State Management

The app manages state through private fields — no viewmodels, no binding framework:

```csharp
private string _connState = "off";  // "off" | "ing" | "on"
private string _activeFilter = "";   // "" | "wireguard" | "v2ray"
private string _payMode = "gb";      // "gb" | "hr"
private List<HnsNodeInfo> _allNodes = new();
private string? _selectedNode;
```

UI updates happen on the dispatcher:

```csharp
Dispatcher.Invoke(() => {
    StatusText.Text = "Connected";
    OrbBorderBrush.Color = Color.FromRgb(0x22, 0xC5, 0x5E);
});
```

### Connection Flow

1. User selects node from sidebar
2. Node info card appears with pricing (per-GB / per-hour)
3. User clicks the connection orb
4. `ConnectAsync()` → SDK handles session creation, payment, handshake, tunnel setup
5. Orb animates to green, IP updates, speed polling starts
6. Disconnect: orb click again → `DisconnectAsync()` → tunnel teardown

### Polling Timers

Three `DispatcherTimer` instances run while connected:

```csharp
_statusPoll  // 3s  — check tunnel status, update speed display
_ipPoll      // 60s — refresh exit IP
_balPoll     // 5m  — refresh P2P token balance
```

### Node List Rendering

Nodes are rendered as programmatic XAML `Border` elements in a `StackPanel`:

```csharp
var card = new Border {
    Background = isSelected ? AccLight : Transparent,
    CornerRadius = new CornerRadius(8),
    Padding = new Thickness(12, 10, 12, 10),
    Cursor = Cursors.Hand,
};
// Flag image + moniker + country/city + protocol badge + peers + price
```

Each card is interactive — click to select, updates the main area node info card.

---

## Services Layer

### IHnsVpnBackend.cs — Interface + Models

Defines the contract that `NativeVpnClient` implements:

```csharp
interface IHnsVpnBackend {
    Task<List<HnsNodeInfo>> GetNodesAsync();
    Task<string> ConnectAsync(string nodeAddress, int gigabytes, string denom);
    Task DisconnectAsync();
    Task<HnsStatus> GetStatusAsync();
    Task<string> GetBalanceAsync();
    // ... more
}
```

Key data models:
- `HnsNodeInfo` — node address, moniker, country, city, protocol type, peers, pricing
- `HnsStatus` — connection state, speed up/down, bytes transferred
- `ActiveSession` — session ID, node, allocation, bytes used/remaining

### NativeVpnClient.cs — SDK Integration

Direct integration with the Sentinel C# SDK:

```csharp
// Query all active nodes from the blockchain
var nodes = await _chainClient.GetActiveNodesAsync();

// Connect to a node (handles payment + handshake + tunnel)
await _vpnClient.ConnectAsync(nodeAddress, new ConnectOptions {
    Gigabytes = gigabytes,
    Denom = "udvpn",
    Dns = new[] { "103.196.38.38", "103.196.38.39" }, // Handshake DNS
});

// Disconnect and tear down tunnel
await _vpnClient.DisconnectAsync();
```

### DiskCache.cs — Local Persistence

Simple JSON file cache in `%LocalAppData%\HandshakeDVPN\`:

```csharp
public static void Save<T>(string key, T data) {
    var path = Path.Combine(CacheDir, $"{key}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(data));
}

public static T? Load<T>(string key) {
    var path = Path.Combine(CacheDir, $"{key}.json");
    if (!File.Exists(path)) return default;
    return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
}
```

Used for: node list (avoid re-querying chain on every launch), test results (persist across sessions), user preferences.

---

## Key UX Patterns for Builders

### 1. Connection Orb

The orb is a layered set of `Ellipse` elements with animated color transitions:

- **Off:** Gray rings, "Tap to connect" text
- **Connecting:** Pulsing amber animation
- **Connected:** Green rings, connected duration timer

### 2. Node Card with Dual Pricing

Each node shows both per-GB and per-hour pricing. User toggles between them:

```
[Per GB  4.2 P2P] [Per Hour  0.8 P2P]
GB: [1]                    = 4.2 P2P
```

### 3. Real-time Speed Display

Bottom bar shows live speeds while connected:

```
Download: 12.4 Mbps  |  Upload: 3.2 Mbps  |  IP: 185.x.x.x (DE)
```

### 4. Test Dashboard

Built-in node testing with results table:

| Node | Protocol | Speed | Result |
|------|----------|-------|--------|
| SuchNode (DE) | WireGuard | 18.3 Mbps | FAST |
| NodeRunner (US) | V2Ray | 6.1 Mbps | SLOW |

Export to JSON or CSV via `SaveFileDialog`.

---

## Prerequisites

- **Windows 10/11**
- **.NET 8.0 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Administrator privileges** — required for WireGuard tunnel creation
- **Sentinel C# SDK** — referenced as project dependency
- **P2P tokens** — needed to pay for node sessions (get from [Osmosis DEX](https://app.osmosis.zone))

## Build

```bash
dotnet build HandshakeDVPN.csproj
```

## Run

```bash
# Must run as administrator for WireGuard
dotnet run --project HandshakeDVPN.csproj
```

---

## SDK References

This app uses the [Sentinel C# SDK](https://github.com/Sentinel-Autonomybuilder/sentinel-dvpn-sdk-csharp):

- `Sentinel.SDK.Core` — blockchain client, wallet, queries
- `Sentinel.SDK.Node` — node discovery, session management
- `Sentinel.SDK.Tunnel` — WireGuard and V2Ray tunnel creation

---

## File-by-File Guide

| File | Lines | What to Learn |
|------|-------|---------------|
| `App.xaml` | 125 | Complete WPF theme system — resource dictionaries, color tokens, reusable button styles with hover/disabled states, font loading |
| `App.xaml.cs` | 62 | App startup, backend service initialization, unhandled exception setup |
| `MainWindow.xaml` | 583 | Full WPF layout — Grid/StackPanel/Border composition, sidebar + main area split, node list template, connection orb, status bar, test dashboard |
| `MainWindow.xaml.cs` | 3,669 | Event-driven UI logic — connection state machine, node rendering, search/filter, polling timers, test execution, export, animations |
| `IHnsVpnBackend.cs` | 403 | Service interface pattern — all data models, enums, method contracts for a VPN backend |
| `NativeVpnClient.cs` | 1,076 | SDK integration — wallet creation/import, chain queries, connect/disconnect flow, session management, Handshake DNS configuration |
| `DiskCache.cs` | 62 | Simple JSON file persistence — generic save/load with LocalAppData storage |

---

## License

MIT
