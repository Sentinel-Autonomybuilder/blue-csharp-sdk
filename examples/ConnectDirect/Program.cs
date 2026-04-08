using Sentinel.SDK.Core;
using Sentinel.SDK.Node;

// ─── Connect Direct Example ───
// Minimal working VPN connection using the Sentinel SDK.
// Creates a wallet, runs preflight checks, connects to a specific node,
// verifies the tunnel is working, then disconnects.
//
// Usage:
//   dotnet run -- <mnemonic> <sentnode1...address>
//
// Requirements:
//   - WireGuard installed (for WireGuard nodes) or V2Ray binary in bin/
//   - Run as administrator (WireGuard requires elevated privileges)
//   - Wallet must have P2P tokens (at least ~0.05 P2P for 1 GB session)

if (args.Length < 2)
{
    Console.WriteLine("Usage: dotnet run -- \"<mnemonic>\" <sentnode1...address>");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run -- \"word1 word2 ... word24\" sentnode1abc123...");
    return 1;
}

var mnemonic = args[0];
var nodeAddress = args[1];

// ── 1. Preflight system check ──
Console.WriteLine("Running preflight checks...");
var preflight = DependencyCheck.Preflight(new PreflightOptions { AutoClean = true });
Console.WriteLine($"  {preflight.Summary}");
Console.WriteLine($"  WireGuard: {(preflight.Ready.WireGuard ? "Ready" : "Not available")}");
Console.WriteLine($"  V2Ray:     {(preflight.Ready.V2Ray ? "Ready" : "Not available")}");

if (!preflight.Ready.AnyProtocol)
{
    Console.WriteLine("ERROR: No VPN protocol available. Install WireGuard or V2Ray.");
    return 1;
}

// ── 2. Create wallet from mnemonic ──
Console.WriteLine("Importing wallet...");
var wallet = SentinelWallet.FromMnemonic(mnemonic);
Console.WriteLine($"  Address: {wallet.Address}");

// ── 3. Create VPN client ──
using var client = new SentinelVpnClient(wallet, new SentinelVpnOptions
{
    Gigabytes = 1,
    FullTunnel = true,
    SystemProxy = true,
});

client.Progress += (_, e) => Console.WriteLine($"  [{e.Step}] {e.Detail}");

// ── 4. Connect to node ──
Console.WriteLine($"Connecting to {nodeAddress}...");
var result = await client.ConnectAsync(nodeAddress);
Console.WriteLine($"  Session ID:    {result.SessionId}");
Console.WriteLine($"  Service type:  {result.ServiceType}");
Console.WriteLine($"  VPN IP:        {result.VpnIp ?? "N/A (V2Ray)"}");
Console.WriteLine($"  SOCKS port:    {result.SocksPort?.ToString() ?? "N/A (WireGuard)"}");

// ── 5. Verify connection ──
Console.WriteLine("Verifying tunnel...");
var verification = await client.VerifyConnectionAsync();
Console.WriteLine($"  Working: {verification.Working}");
Console.WriteLine($"  VPN IP:  {verification.VpnIp ?? "unknown"}");

// ── 6. Wait for user ──
Console.WriteLine();
Console.WriteLine("VPN is active. Press Enter to disconnect...");
Console.ReadLine();

// ── 7. Disconnect ──
Console.WriteLine("Disconnecting...");
await client.DisconnectAsync();
Console.WriteLine("Disconnected. Done.");

return 0;
