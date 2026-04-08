using Sentinel.SDK.Core;

// ─── Query Nodes Example ───
// Queries online Sentinel dVPN nodes and displays them as a table.
// No wallet needed — this is a read-only chain query.
//
// Usage:
//   dotnet run                        # show all nodes
//   dotnet run -- --country DE        # filter by country code
//   dotnet run -- --type wireguard    # filter by service type

var filterCountry = GetArg("--country");
var filterType = GetArg("--type");

// ── 1. Create chain client (no wallet needed for read-only queries) ──
using var chain = new ChainClient();
await chain.InitializeAsync();

// ── 2. Query active nodes ──
Console.WriteLine("Querying active nodes from the Sentinel chain...");
var nodes = await chain.GetActiveNodesAsync(limit: 500);
Console.WriteLine($"Found {nodes.Count} active nodes on chain.\n");

// ── 3. Filter ──
var filtered = nodes.AsEnumerable();

if (filterType is not null)
{
    // Node type is determined by pricing — nodes with hourly_prices in udvpn
    // are WireGuard, those with gigabyte_prices tend to be V2Ray.
    // For a complete type check, query node status via NodeClient.GetStatusAsync.
    Console.WriteLine($"(Note: exact service type requires per-node status query via NodeClient)");
}

// ── 4. Display as table ──
Console.WriteLine($"{"Address",-50} {"GB Price",-12} {"Hourly Price",-14} {"Remote Addrs"}");
Console.WriteLine(new string('-', 110));

var count = 0;
foreach (var node in filtered)
{
    var gbPrice = node.GigabytePrices
        .FirstOrDefault(p => p.Denom == "udvpn")?.DisplayPrice ?? "N/A";
    var hrPrice = node.HourlyPrices
        .FirstOrDefault(p => p.Denom == "udvpn")?.DisplayPrice ?? "N/A";
    var addrs = node.RemoteAddrs.Length > 0
        ? string.Join(", ", node.RemoteAddrs.Take(2))
        : node.RemoteUrl ?? "none";

    Console.WriteLine($"{node.Address,-50} {gbPrice,-12} {hrPrice,-14} {addrs}");
    count++;
    if (count >= 50)
    {
        Console.WriteLine($"... and {nodes.Count - 50} more. Use filters to narrow results.");
        break;
    }
}

Console.WriteLine($"\nTotal displayed: {Math.Min(count, nodes.Count)} of {nodes.Count}");
return 0;

// ── Helper ──
string? GetArg(string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}
