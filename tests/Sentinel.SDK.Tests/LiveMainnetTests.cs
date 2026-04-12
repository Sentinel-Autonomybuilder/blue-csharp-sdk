using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;
using Xunit.Abstractions;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Live mainnet tests — runs against real Sentinel chain.
/// Reads mnemonic from SENTINEL_TEST_MNEMONIC env var or node-tester .env.
/// </summary>
public class LiveMainnetTests
{
    private readonly ITestOutputHelper _output;

    public LiveMainnetTests(ITestOutputHelper output) => _output = output;

    private static string? GetMnemonic()
    {
        var env = Environment.GetEnvironmentVariable("SENTINEL_TEST_MNEMONIC");
        if (!string.IsNullOrEmpty(env)) return env;

        // Fallback: read from node-tester .env
        var envPath = @"C:\Users\Connect\Desktop\sentinel-node-tester\.env";
        if (File.Exists(envPath))
        {
            foreach (var line in File.ReadAllLines(envPath))
            {
                if (line.StartsWith("MNEMONIC="))
                    return line["MNEMONIC=".Length..].Trim('"', '\'', ' ');
            }
        }
        return null;
    }

    // ─── Wallet ───

    [Fact]
    public void Wallet_FromMnemonic_DerivesSentAddress()
    {
        var mnemonic = GetMnemonic();
        if (mnemonic is null) { _output.WriteLine("SKIP: No mnemonic"); return; }

        using var wallet = SentinelWallet.FromMnemonic(mnemonic);
        _output.WriteLine($"Address: {wallet.Address}");
        Assert.StartsWith("sent1", wallet.Address);
    }

    [Fact]
    public async Task Wallet_GetBalance_ReturnsPositive()
    {
        var mnemonic = GetMnemonic();
        if (mnemonic is null) { _output.WriteLine("SKIP: No mnemonic"); return; }

        using var wallet = SentinelWallet.FromMnemonic(mnemonic);
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var balance = await chain.GetBalanceAsync(wallet.Address);
        _output.WriteLine($"Balance: {Helpers.FormatP2P(balance.Udvpn)} ({balance.Udvpn} udvpn)");
        Assert.True(balance.Udvpn > 0, "Wallet needs P2P for tests");
    }

    // ─── Chain Queries ───

    [Fact]
    public async Task Chain_GetActiveNodes_Returns900Plus()
    {
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var nodes = await chain.GetActiveNodesAsync(limit: 5000);
        _output.WriteLine($"Active nodes: {nodes.Count}");
        Assert.True(nodes.Count > 900);
        Assert.All(nodes, n => Assert.StartsWith("sentnode", n.Address));
    }

    [Fact]
    public async Task Chain_GetNode_ReturnsSingleNode()
    {
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var nodes = await chain.GetActiveNodesAsync(limit: 1);
        Assert.NotEmpty(nodes);
        var node = await chain.GetNodeAsync(nodes[0].Address);
        Assert.NotNull(node);
        _output.WriteLine($"Node: {node!.Address}, Prices: GB={node.GigabytePrices.Length} Hr={node.HourlyPrices.Length}");
    }

    [Fact]
    public async Task Chain_NodesHavePricing()
    {
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var nodes = await chain.GetActiveNodesAsync(limit: 100);
        var withGb = nodes.Count(n => n.GigabytePrices.Any(p => string.Equals(p.Denom, "udvpn")));
        _output.WriteLine($"With GB pricing: {withGb}/{nodes.Count}");
        Assert.True(withGb > 50);
    }

    [Fact]
    public async Task Chain_EndpointHealth()
    {
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var health = await chain.CheckEndpointHealthAsync();
        _output.WriteLine($"Endpoints: {health.Count}, Reachable: {health.Count(h => h.LatencyMs.HasValue)}");
        Assert.NotEmpty(health);
        Assert.Contains(health, h => h.LatencyMs.HasValue);
    }

    [Fact]
    public async Task Chain_NetworkOverview()
    {
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var overview = await chain.GetNetworkOverviewAsync();
        _output.WriteLine($"Total nodes: {overview.TotalNodes}, Countries: {overview.ByCountry.Count}");
        Assert.True(overview.TotalNodes > 900);
    }

    // ─── Plans ───

    [Fact]
    public async Task Chain_DiscoverPlans_FindsPlans()
    {
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var plans = await chain.DiscoverPlansAsync(maxId: 50);
        _output.WriteLine($"Plans found: {plans.Count}");
        foreach (var p in plans.Take(3))
            _output.WriteLine($"  Plan #{p.Id}: {p.Subscribers} subs, {p.NodeCount} nodes");
        Assert.True(plans.Count > 0);
    }

    [Fact]
    public async Task Chain_PlanNodes_ReturnNodes()
    {
        using var chain = new ChainClient();
        await chain.InitializeAsync();
        var nodes = await chain.GetPlanNodesAsync(44); // Plan we created in JS test
        _output.WriteLine($"Plan 44 nodes: {nodes.Count}");
        // Plan node linkage is chain state — may be 0 if nodes were unlinked
        Assert.NotNull(nodes);
    }

    // ─── Helpers ───

    [Fact]
    public void Helpers_CountryNameToCode_AllVariants()
    {
        Assert.Equal("NL", Constants.CountryNameToCode("The Netherlands"));
        Assert.Equal("TR", Constants.CountryNameToCode("Türkiye"));
        Assert.Equal("CD", Constants.CountryNameToCode("DR Congo"));
        Assert.Equal("CZ", Constants.CountryNameToCode("Czechia"));
        Assert.Equal("RU", Constants.CountryNameToCode("Russian Federation"));
        Assert.Equal("VN", Constants.CountryNameToCode("Viet Nam"));
        Assert.Equal("KR", Constants.CountryNameToCode("South Korea"));
        Assert.Equal("AE", Constants.CountryNameToCode("UAE"));
        Assert.Equal("US", Constants.CountryNameToCode("us"));
        Assert.Null(Constants.CountryNameToCode("Atlantis"));
        _output.WriteLine("All 10 country variants passed");
    }

    [Fact]
    public void Helpers_GetFlagUrl()
    {
        Assert.Contains("flagcdn.com", Constants.GetFlagUrl("US"));
        Assert.Contains("/us.png", Constants.GetFlagUrl("US"));
        Assert.Equal("", Constants.GetFlagUrl(null));
    }

    [Fact]
    public void Helpers_UserMessage_AllCodes()
    {
        var codes = new[] {
            "INSUFFICIENT_BALANCE", "NODE_OFFLINE", "NODE_NO_UDVPN", "NODE_CLOCK_DRIFT",
            "V2RAY_ALL_FAILED", "V2RAY_NOT_FOUND", "WG_NOT_AVAILABLE", "WG_NO_CONNECTIVITY",
            "TUNNEL_SETUP_FAILED", "TLS_CERT_CHANGED", "BROADCAST_FAILED", "TX_FAILED",
            "ALREADY_CONNECTED", "ALL_NODES_FAILED", "ALL_ENDPOINTS_FAILED",
            "INVALID_MNEMONIC", "INVALID_NODE_ADDRESS", "INVALID_PLAN_ID",
            "SESSION_POISONED", "NODE_NOT_FOUND", "LCD_ERROR", "SESSION_EXISTS",
            "ABORTED", "CHAIN_LAG", "NODE_DATABASE_CORRUPT", "INVALID_ASSIGNED_IP",
            "NODE_INACTIVE",
        };
        foreach (var code in codes)
        {
            var msg = Helpers.UserMessage(code);
            Assert.NotEqual("An unexpected error occurred.", msg);
            _output.WriteLine($"  {code}: {msg}");
        }
    }

    [Fact]
    public void Helpers_EstimateSessionPrice()
    {
        var node = new ChainNode(
            Address: "sentnode1test",
            RemoteAddrs: ["1.2.3.4:8585"],
            RemoteUrl: "1.2.3.4:8585",
            GigabytePrices: [new PriceEntry("udvpn", "0.000040152030000000", "40152030")],
            HourlyPrices: [new PriceEntry("udvpn", "0.000033409250000000", "33409250")],
            Status: 1
        );

        var gbCost = Helpers.EstimateSessionPrice(node, "gb", 5);
        _output.WriteLine($"5 GB: {gbCost.CostDisplay} ({gbCost.CostUdvpn} udvpn)");
        Assert.True(gbCost.CostUdvpn > 0);
        Assert.Equal("GB", gbCost.Unit);

        var hrCost = Helpers.EstimateSessionPrice(node, "hour", 4);
        _output.WriteLine($"4 hours: {hrCost.CostDisplay} ({hrCost.CostUdvpn} udvpn)");
        Assert.True(hrCost.CostUdvpn > 0);
        Assert.Equal("hours", hrCost.Unit);
    }

    [Fact]
    public void Helpers_ComputeSessionAllocation()
    {
        var session = new ChainSession(
            "1", "sent1a", "sentnode1b",
            "500000000", "100000000", "1000000000",
            "44.7s", "0s", "active", null, null
        );

        var alloc = Helpers.ComputeSessionAllocation(session);
        _output.WriteLine($"Used: {alloc.UsedDisplay}, Max: {alloc.MaxDisplay}, Remaining: {alloc.RemainingDisplay}, {alloc.UsedPercent}%");
        Assert.Equal(60.0, alloc.UsedPercent);
        Assert.True(alloc.IsGbBased);
        Assert.False(alloc.IsHourlyBased);
    }

    [Fact]
    public void PriceEntry_UdvpnAmount_And_DisplayPrice()
    {
        var entry = new PriceEntry("udvpn", "0.000040152030000000", "40152030");
        Assert.Equal(40152030, entry.UdvpnAmount);
        Assert.Contains("P2P", entry.DisplayPrice);
        _output.WriteLine($"UdvpnAmount: {entry.UdvpnAmount}, Display: {entry.DisplayPrice}");
    }

    [Fact]
    public void Constants_AppTypes()
    {
        Assert.Equal("white_label", Constants.AppTypes.WhiteLabel);
        Assert.Equal("direct_p2p", Constants.AppTypes.DirectP2P);
        Assert.Equal("all_in_one", Constants.AppTypes.AllInOne);
        Assert.Equal(3, Constants.AppTypes.All.Length);
    }

    [Fact]
    public void Constants_DnsPresets()
    {
        var resolved = Constants.DnsPresets.Resolve("handshake");
        Assert.Contains("103.196.38.38", resolved);
        Assert.Contains("8.8.8.8", resolved); // fallback
        Assert.Contains("1.1.1.1", resolved); // fallback

        var google = Constants.DnsPresets.Resolve("google");
        Assert.StartsWith("8.8.8.8", google);
        Assert.Contains("103.196.38.38", google); // handshake fallback
    }

    [Fact]
    public void Constants_GbAndHourOptions()
    {
        Assert.Contains(1, Constants.GbOptions);
        Assert.Contains(50, Constants.GbOptions);
        Assert.Contains(1, Constants.HourOptions);
        Assert.Contains(24, Constants.HourOptions);
    }
}
